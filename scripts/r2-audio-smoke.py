#!/usr/bin/env python3
"""Синтетическая запись двух мониторов, WAV, разделение каналов и временной сдвиг.

Речь не захватывается, звук в реальные устройства не подаётся. По умолчанию 20 с;
--seconds 3600 — реальная часовая выдержка, --pause-for 2 — проверка паузы.
"""
import argparse
import array
import json
import math
import signal
import shutil
from pathlib import Path
import subprocess
import sys
import threading
import time
import uuid
import wave
import warnings

with warnings.catch_warnings():
    warnings.simplefilter("ignore", DeprecationWarning)
    try:
        import audioop
    except ImportError:
        audioop = None

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "tools/EliteRec.Recorder/bin/Release/net10.0/EliteRec.Recorder.dll"


def pactl(*args):
    return subprocess.check_output(["pactl", *args], text=True, timeout=5).strip()


def validate(base, seconds, pause_for):
    report = json.loads(base.with_suffix(".json").read_text())
    if report["Error"] is not None:
        raise RuntimeError(report["Error"])
    onsets = [[], []]
    with wave.open(str(base) + ".wav", "rb") as stereo:
        assert stereo.getnchannels() == 2 and stereo.getframerate() == 48000 and stereo.getsampwidth() == 2
        frames = stereo.getnframes()
        assert abs(frames / 48000 - (seconds - pause_for)) < .15, frames / 48000
        last = [-10000, -10000]
        index = 0
        peaks = [0, 0]
        while block := stereo.readframes(480):
            if audioop is not None:
                channels = [audioop.tomono(block, 2, 1, 0), audioop.tomono(block, 2, 0, 1)]
                values = [audioop.max(channel, 2) for channel in channels]
            else:
                samples = array.array("h", block)
                if sys.byteorder != "little": samples.byteswap()
                channels = [samples[::2], samples[1::2]]
                values = [max(map(abs, channel), default=0) for channel in channels]
            for channel, value in enumerate(values):
                peaks[channel] = max(peaks[channel], value)
                if value > 2000:
                    if index - last[channel] > 20:
                        pcm = channels[channel]
                        if isinstance(pcm, bytes):
                            pcm = array.array("h", pcm)
                            if sys.byteorder != "little": pcm.byteswap()
                        crossing = next(i for i, sample in enumerate(pcm) if abs(sample) > 2000)
                        onsets[channel].append((index * 480 + crossing) / 48000)
                    last[channel] = index
            index += 1
    assert min(peaks) > 5000, peaks
    with wave.open(str(base) + "_mono.wav", "rb") as mono:
        assert mono.getnchannels() == 1 and mono.getframerate() == 48000 and mono.getnframes() == frames
    # Отбрасываем первые/последние 2 с: старт/стоп могут обрезать один маркер.
    lags = [min(abs(a - b) for b in onsets[1]) for a in onsets[0] if 2 < a < frames / 48000 - 2]
    assert len(lags) >= max(1, seconds - pause_for - 6), len(lags)
    maximum = round(max(lags) * 1000, 6)
    assert maximum <= 50, f"Рассогласование каналов {maximum:.1f} мс"
    assert all(value == 0 for value in report["Capture"].values()), report["Capture"]
    sync = report["Synchronization"]
    assert sync["MissingMicrophoneFrames"] == sync["MissingSystemFrames"] == 0, sync
    return {"wall_seconds_requested": seconds, "pause_seconds": pause_for, "wav_seconds": frames / 48000,
            "markers_compared": len(lags), "max_channel_lag_ms": maximum, "pcm_peaks": peaks,
            "onset_resolution_samples": 1,
            "capture": report["Capture"], "synchronization": report["Synchronization"], "status": "pass"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--seconds", type=int, default=20)
    parser.add_argument("--pause-for", type=int, default=0)
    parser.add_argument("--stall-ms", type=int, default=0)
    parser.add_argument("--close-active", action="store_true")
    parser.add_argument("--validate-only", type=Path)
    options = parser.parse_args()
    if options.validate_only is not None:
        result = validate(options.validate_only / "recording", options.seconds, options.pause_for)
        (options.validate_only / "validation-exact.json").write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result, indent=2))
        return
    if options.seconds < 8 or options.pause_for < 0 or options.pause_for + 6 >= options.seconds:
        parser.error("Нужно >= 8 секунд и достаточно времени до/после паузы")
    token = uuid.uuid4().hex[:8]
    directory = ROOT / "artifacts/r2" / token
    directory.mkdir(parents=True)
    base = directory / "recording"
    # Длинный прогон привязан к неизменной копии сборки, пока исходники продолжают меняться.
    runtime = directory / "runtime"
    shutil.copytree(DLL.parent, runtime)
    recorder_dll = runtime / DLL.name
    original = json.loads(pactl("--format=json", "info"))
    names = ["eliterec_r2_mic_" + token, "eliterec_r2_sys_" + token]
    modules, players = [], []
    recorder = None
    stopped = threading.Event()

    def restore_defaults():
        current = json.loads(pactl("--format=json", "info"))
        if current["default_sink_name"] in names: pactl("set-default-sink", original["default_sink_name"])
        if current["default_source_name"] in [n + ".monitor" for n in names]:
            pactl("set-default-source", original["default_source_name"])

    try:
        for name in names:
            modules.append(pactl("load-module", "module-null-sink", "sink_name=" + name,
                                 "sink_properties=device.description=EliteRec_R2_Test", "rate=48000"))
        restore_defaults()
        for name in names:
            players.append(subprocess.Popen(["paplay", "--raw", "--format=s16le", "--rate=48000", "--channels=1",
                                             "--latency-msec=50", "--process-time-msec=10", "--device=" + name], stdin=subprocess.PIPE))
        pattern = []
        for part in range(10):
            samples = array.array("h", (int(9000 * math.sin(2 * math.pi * 440 * i / 48000))
                                        if i < 5760 else 0 for i in range(part * 4800, (part + 1) * 4800)))
            if sys.byteorder != "little": samples.byteswap()
            pattern.append(samples.tobytes())

        def feed():
            try:
                part = 0
                while not stopped.is_set():
                    for player in players:
                        player.stdin.write(pattern[part % 10])
                        player.stdin.flush()
                    part += 1
            except (BrokenPipeError, ValueError, OSError):
                pass

        sender = threading.Thread(target=feed, daemon=True)
        sender.start()
        args = ["dotnet", str(recorder_dll), "--source", names[0] + ".monitor", "--system-source", names[1] + ".monitor",
                "--output", str(base), "--seconds", str(options.seconds)]
        if options.pause_for: args += ["--pause-after", "3", "--pause-for", str(options.pause_for)]
        if options.close_active: args += ["--close-active", "1"]
        with (directory / "recorder.log").open("w") as log:
            recorder = subprocess.Popen(args, stdout=log, stderr=log)
            started = time.monotonic()
            next_stall = 3
            while recorder.poll() is None:
                if time.monotonic() - started > options.seconds + 25:
                    raise TimeoutError("Запись не завершилась вовремя")
                if options.stall_ms and time.monotonic() - started >= next_stall:
                    recorder.send_signal(signal.SIGSTOP)
                    time.sleep(min(options.stall_ms, 800) / 1000)
                    recorder.send_signal(signal.SIGCONT)
                    next_stall += 3
                time.sleep(.1)
            if recorder.returncode != 0: raise RuntimeError((directory / "recorder.log").read_text())
        result = validate(base, options.seconds, options.pause_for)
        if options.close_active:
            session = json.loads(base.with_suffix(".json").read_text())
            assert session["State"] == 4
            result["dispose_active_session"] = "pass"
        (directory / "validation.json").write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps({**result, "directory": str(directory)}, indent=2), flush=True)
    finally:
        stopped.set()
        for process in [recorder, *players]:
            if process is not None and process.poll() is None:
                process.kill(); process.wait(timeout=5)
        try:
            restore_defaults()
        finally:
            for module in reversed(modules): pactl("unload-module", module)


if __name__ == "__main__":
    main()
