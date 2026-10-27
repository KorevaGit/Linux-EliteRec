#!/usr/bin/env python3
"""Проверка монитора и исчезновения тестового устройства. Только синтетический звук.

Запускается после Release-сборки. Не воспроизводит звук в колонках, не меняет
громкость, не сохраняет речь. Свой null-sink удаляет в finally. Нужны pactl/paplay.
"""
import array
import json
import math
from pathlib import Path
import subprocess
import sys
import time
import uuid
import wave

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "tools/EliteRec.Probe/bin/Release/net10.0/EliteRec.Probe.dll"


def pactl(*args):
    return subprocess.check_output(["pactl", *args], text=True, timeout=5).strip()


def main():
    token = uuid.uuid4().hex[:10]
    sink = "eliterec_r0_" + token
    directory = ROOT / "artifacts/r0" / ("monitor-" + token)
    directory.mkdir(parents=True)
    original = json.loads(pactl("--format=json", "info"))
    module = None
    capture = None
    playback = None

    def restore_test_defaults():
        current = json.loads(pactl("--format=json", "info"))
        if current["default_sink_name"] == sink:
            pactl("set-default-sink", original["default_sink_name"])
        if current["default_source_name"] == sink + ".monitor":
            pactl("set-default-source", original["default_source_name"])

    try:
        module = pactl("load-module", "module-null-sink", "sink_name=" + sink,
                       "sink_properties=device.description=EliteRec_R0_Test", "rate=48000")
        tone = directory / "synthetic.wav"
        samples = array.array("h", (int(8000 * math.sin(2 * math.pi * 440 * i / 48000))
                                    for i in range(48000 * 8)))
        if sys.byteorder != "little":
            samples.byteswap()
        with wave.open(str(tone), "wb") as output:
            output.setparams((1, 2, 48000, 0, "NONE", "not compressed"))
            output.writeframes(samples.tobytes())

        def launch(name, source, output_sink):
            return subprocess.Popen(["dotnet", str(DLL), "capture", "--seconds", "6",
                                     "--source", source, "--sink", output_sink,
                                     "--output", str(directory / name)], stdout=subprocess.DEVNULL)

        # Реальный микрофон и монитор изолированного вывода. Только статистика.
        capture = launch("signal.json", original["default_source_name"], sink)
        time.sleep(1)
        playback = subprocess.Popen(["paplay", "--device=" + sink, str(tone)])
        code = capture.wait(timeout=15)
        if code != 0:
            raise RuntimeError("Захват тестового монитора завершился с ошибкой: " + str(code))
        signal = json.loads((directory / "signal.json").read_text())
        if signal["Failure"] is not None or not .1 < signal["SystemAudio"]["Peak"] < .5:
            raise RuntimeError("Тестовый сигнал не появился на мониторе в ожидаемом уровне.")
        playback.terminate()
        playback.wait(timeout=5)
        playback = None

        # Монитор временного вывода заменяет микрофон только в этом тесте отказа.
        capture = launch("disconnect.json", sink + ".monitor", original["default_sink_name"])
        time.sleep(2)
        restore_test_defaults()
        pactl("unload-module", module)
        module = None
        code = capture.wait(timeout=15)
        if code != 3:
            raise RuntimeError("Исчезновение источника не вернуло ожидаемый код 3: " + str(code))
        disconnected = json.loads((directory / "disconnect.json").read_text())
        if not disconnected["Failure"]:
            raise RuntimeError("Отсутствует диагностика исчезновения источника.")
        print(json.dumps({"monitor_signal": "pass", "source_disappearance": "pass",
                          "system_peak": signal["SystemAudio"]["Peak"],
                          "reports": str(directory)}, indent=2))
    finally:
        for child in (capture, playback):
            if child is not None and child.poll() is None:
                child.kill()
                child.wait(timeout=5)
        # Если политика сервера успела выбрать временный вывод, вернём прежний.
        try:
            restore_test_defaults()
        finally:
            if module is not None:
                pactl("unload-module", module)


if __name__ == "__main__":
    main()
