#!/usr/bin/env python3
"""Проверка продуктового libpulse-каталога и подписки на временном null-sink.

Звук не захватывает, громкость реальных устройств не меняет, файл настроек не трогает.
Нужна предварительная Release-сборка и пользовательская сессия Pulse/PipeWire.
"""
import json
import os
from pathlib import Path
import select
import signal
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "src/EliteRec.App/bin/Release/net10.0/EliteRec.App.dll"


def pactl(*args):
    return subprocess.check_output(["pactl", *args], text=True, timeout=5).strip()


def main():
    original = json.loads(pactl("--format=json", "info"))
    snapshot = json.loads(subprocess.check_output(["dotnet", str(DLL), "--devices"], text=True, timeout=15))
    assert snapshot["ServerAvailable"], snapshot
    assert snapshot["DefaultInputId"] == original["default_source_name"]
    assert snapshot["DefaultOutputId"] == original["default_sink_name"]
    assert all(device["CaptureSourceId"] for device in snapshot["Outputs"])
    module = None
    name = "eliterec_r1_" + uuid.uuid4().hex[:10]
    process = subprocess.Popen(["dotnet", str(DLL), "--watch-devices"], stdout=subprocess.PIPE)
    pending = b""

    def event_until(predicate, seconds=10):
        nonlocal pending
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            while b"\n" in pending:
                line, pending = pending.split(b"\n", 1)
                event = json.loads(line)
                if predicate(event):
                    return event
            if process.poll() is not None:
                raise RuntimeError("Watcher unexpectedly exited: " + str(process.returncode))
            ready, _, _ = select.select([process.stdout], [], [], min(.2, max(0, end - time.monotonic())))
            if ready:
                pending += os.read(process.stdout.fileno(), 65536)
        raise TimeoutError("Expected device notification did not arrive")

    def has(event, kind):
        return any(c["DeviceId"] == name and c["Direction"] == 1 and c["Kind"] == kind
                   for c in event["Changes"])

    def restore_defaults():
        current = json.loads(pactl("--format=json", "info"))
        if current["default_sink_name"] == name:
            pactl("set-default-sink", original["default_sink_name"])
        if current["default_source_name"] == name + ".monitor":
            pactl("set-default-source", original["default_source_name"])

    try:
        event_until(lambda e: e["Snapshot"]["ServerAvailable"])
        module = pactl("load-module", "module-null-sink", "sink_name=" + name,
                       "sink_properties=device.description=EliteRec_R1_Test")
        added = event_until(lambda e: has(e, 0))
        output = next(d for d in added["Snapshot"]["Outputs"] if d["Id"] == name)
        assert output["CaptureSourceId"] == name + ".monitor", output
        assert all(d["Id"] != name + ".monitor" for d in added["Snapshot"]["Inputs"])
        restore_defaults()
        pactl("unload-module", module)
        module = None
        removed = event_until(lambda e: has(e, 1))
        assert all(d["Id"] != name for d in removed["Snapshot"]["Outputs"])
        process.send_signal(signal.SIGINT)
        assert process.wait(timeout=10) == 0
        directory = ROOT / "artifacts/r1"
        directory.mkdir(parents=True, exist_ok=True)
        result = {"catalog_defaults": "pass", "added": "pass", "monitor_mapping": "pass",
                  "monitor_excluded_from_inputs": "pass", "removed": "pass", "graceful_shutdown": "pass"}
        (directory / "device-smoke.json").write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result, indent=2))
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=5)
        try:
            restore_defaults()
        finally:
            if module is not None:
                pactl("unload-module", module)


if __name__ == "__main__":
    main()
