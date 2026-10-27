#!/usr/bin/env python3
"""R2: исчезновение виртуального источника завершает запись и закрывает оба WAV.

Сигнал — тишина двух собственных null-sink. Реальный микрофон не используется.
"""
import json
from pathlib import Path
import subprocess
import time
import uuid
import wave

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "tools/EliteRec.Recorder/bin/Release/net10.0/EliteRec.Recorder.dll"


def pactl(*args):
    return subprocess.check_output(["pactl", *args], text=True, timeout=5).strip()


def main():
    token = uuid.uuid4().hex[:8]
    folder = ROOT / "artifacts/r2" / ("failure-" + token)
    folder.mkdir(parents=True)
    names = ["eliterec_r2_fail_mic_" + token, "eliterec_r2_fail_sys_" + token]
    original = json.loads(pactl("--format=json", "info"))
    modules = []
    recorder = None
    try:
        for name in names:
            modules.append(pactl("load-module", "module-null-sink", "sink_name=" + name,
                                 "sink_properties=device.description=EliteRec_R2_Failure_Test"))
        base = folder / "recording"
        with (folder / "recorder.log").open("w") as log:
            recorder = subprocess.Popen(["dotnet", str(DLL), "--source", names[0] + ".monitor",
                                         "--system-source", names[1] + ".monitor", "--seconds", "15",
                                         "--output", str(base)], stdout=log, stderr=log)
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline:
                if recorder.poll() is not None: raise RuntimeError((folder / "recorder.log").read_text())
                part = folder / ".recording.wav.part"  # с R3 файл скрыт, пока идёт запись
                if part.exists() and part.stat().st_size > 48000: break
                time.sleep(.1)
            else: raise TimeoutError("Запись не началась")
            pactl("unload-module", modules[0])
            modules[0] = None
            assert recorder.wait(timeout=10) == 2
        counts = []
        for path, channels in [(str(base) + ".wav", 1), (str(base) + "_stereo.wav", 2)]:
            with wave.open(path, "rb") as recording:
                assert recording.getnchannels() == channels and recording.getnframes() > 0
                counts.append(recording.getnframes())
                assert Path(path).stat().st_size == 44 + counts[-1] * 2 * channels
        assert counts[0] == counts[1], counts
        result = {"source_disappearance": "pass", "both_wav_finalized": "pass", "frames": counts[0]}
        (folder / "validation.json").write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps({**result, "directory": str(folder)}, indent=2))
    finally:
        if recorder is not None and recorder.poll() is None: recorder.kill(); recorder.wait(timeout=5)
        current = json.loads(pactl("--format=json", "info"))
        try:
            if current["default_sink_name"] in names: pactl("set-default-sink", original["default_sink_name"])
            if current["default_source_name"] in [n + ".monitor" for n in names]:
                pactl("set-default-source", original["default_source_name"])
        finally:
            for module in reversed(modules):
                if module is not None: pactl("unload-module", module)


if __name__ == "__main__":
    main()
