#!/usr/bin/env python3
"""R4: «выдернули гарнитуру» (или выключили её) посреди записи и вернули.

Три виртуальных null-sink: гарнитура (440 Гц), запасной микрофон (880 Гц), система (660 Гц).
Recorder пишет WAV, микрофон — монитор гарнитуры, запасной — `--fallback-source`.
На --unplug-at секунде модуль гарнитуры выгружается, на --replug-at — загружается снова.
--mode silence: устройство остаётся, но тон останавливается — монитор null-sink даёт ровные
нули, как выключенная беспроводная гарнитура с приёмником в USB. Переход — через 3 с тишины.
Проверяем: запись не остановилась и длится сколько задано, потерь нет, левый канал
звучит 440 → 880 → 440 Гц, правый всё время 660 Гц, в файле событий — пропажа,
переход на запасной и возврат.
"""
import argparse, array, json, math, os, subprocess, sys, threading, time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "tools/EliteRec.Recorder/bin/Release/net10.0/EliteRec.Recorder.dll"


def pactl(*args):
    return subprocess.check_output(["pactl", *args], text=True, timeout=5).strip()


def load(name):
    return pactl("load-module", "module-null-sink", "sink_name=" + name,
                 "sink_properties=device.description=EliteRec_R4_Test", "rate=48000")


class Tone:
    """paplay с синусом в null-sink; перезапускается после возврата устройства."""

    def __init__(self, sink, hz):
        self.sink, self.stopped = sink, threading.Event()
        self.chunk = array.array("h", (int(9000 * math.sin(2 * math.pi * hz * i / 48000)) for i in range(4800))).tobytes()
        self.player = None
        self.start()

    def start(self):
        self.player = subprocess.Popen(["paplay", "--raw", "--format=s16le", "--rate=48000", "--channels=1",
                                        "--latency-msec=50", "--device=" + self.sink], stdin=subprocess.PIPE,
                                       stderr=subprocess.DEVNULL)
        player = self.player

        def feed():
            try:
                while not self.stopped.is_set() and player.poll() is None:
                    player.stdin.write(self.chunk); player.stdin.flush()
            except (BrokenPipeError, ValueError, OSError):
                pass
        threading.Thread(target=feed, daemon=True).start()

    def stop(self):
        self.stopped.set(); self.player.kill()


def dominant_hz(samples):
    crossings = sum(1 for a, b in zip(samples, samples[1:]) if (a < 0) != (b < 0))
    rms = math.sqrt(sum(x * x for x in samples) / len(samples))
    return crossings * 48000 / len(samples) / 2, rms


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--seconds", type=int, default=14)
    parser.add_argument("--unplug-at", type=float, default=4)
    parser.add_argument("--replug-at", type=float, default=8)
    parser.add_argument("--mode", choices=["unplug", "silence"], default="unplug")
    options = parser.parse_args()
    directory = ROOT / "artifacts/r4" / time.strftime("%Y%m%d-%H%M%S")
    directory.mkdir(parents=True)
    tag = f"eliterec_r4_{os.getpid()}"
    headset, spare, system = tag + "_headset", tag + "_spare", tag + "_sys"
    original = json.loads(pactl("--format=json", "info"))
    modules, tones = {}, {}
    try:
        for name in (headset, spare, system):
            modules[name] = load(name)
        pactl("set-default-sink", original["default_sink_name"])
        pactl("set-default-source", original["default_source_name"])
        for name, hz in ((headset, 440), (spare, 880), (system, 660)):
            tones[name] = Tone(name, hz)
        base = directory / "call"
        with (directory / "recorder.log").open("w") as log:
            recorder = subprocess.Popen(["dotnet", str(DLL), "--source", headset + ".monitor",
                                         "--fallback-source", spare + ".monitor", "--system-source", system + ".monitor",
                                         "--output", str(base), "--seconds", str(options.seconds)], stdout=log, stderr=log)
            started = time.monotonic() + .75  # прогрев Recorder до начала записи
            time.sleep(max(0, started + options.unplug_at - time.monotonic()))
            tones[headset].stop()
            if options.mode == "unplug": pactl("unload-module", modules.pop(headset))
            time.sleep(max(0, started + options.replug_at - time.monotonic()))
            if options.mode == "unplug": modules[headset] = load(headset)
            tones[headset] = Tone(headset, 440)
            assert recorder.wait(timeout=options.seconds + 30) == 0, (directory / "recorder.log").read_text()

        snapshot = json.loads(Path(str(base) + ".json").read_text())
        stats = snapshot["Synchronization"]
        events = Path(str(base) + "_события.txt").read_text(encoding="utf-8").splitlines()
        data = (directory / "call.wav").read_bytes()[44:]
        frames = array.array("h", data)
        if sys.byteorder != "little": frames.byteswap()
        left, right = frames[0::2], frames[1::2]
        seconds = len(left) / 48000
        windows = []
        for start in range(0, len(left) - 9600, 24000):  # окна по 0,2 с каждые 0,5 с
            l_hz, l_rms = dominant_hz([x / 32768 for x in left[start:start + 9600]])
            r_hz, _ = dominant_hz([x / 32768 for x in right[start:start + 9600]])
            windows.append((round(start / 48000, 1), round(l_hz), round(l_rms, 3), round(r_hz)))

        def left_at(t):
            return min(windows, key=lambda w: abs(w[0] - t))[1]
        report = {"mode": options.mode, "seconds": round(seconds, 2), "events": events,
                  "missing": [stats["MissingMicrophoneFrames"], stats["MissingSystemFrames"]],
                  "gap_ms": round(stats.get("GapMicrophoneFrames", 0) / 48), "left_hz": {
                      "before": left_at(options.unplug_at / 2),
                      "unplugged": left_at((options.unplug_at + options.replug_at) / 2),
                      "returned": left_at((options.replug_at + options.seconds) / 2 + 1)},
                  "right_hz_range": [min(w[3] for w in windows), max(w[3] for w in windows)],
                  "folder": str(directory)}
        print(json.dumps(report, ensure_ascii=False, indent=2))
        assert abs(seconds - options.seconds) < .5, "запись остановилась или растянулась"
        assert report["missing"] == [0, 0], "потери данных"
        assert abs(report["left_hz"]["before"] - 440) < 20
        assert abs(report["left_hz"]["unplugged"] - 880) < 20, "не переключились на запасной"
        if options.mode == "silence":
            assert len(events) == 2 and "молчит" in events[0] and headset in events[1], events
            return 0
        assert abs(report["left_hz"]["returned"] - 440) < 20, "не вернулись на гарнитуру"
        assert all(abs(hz - 660) < 20 for hz in report["right_hz_range"]), "правый канал пострадал"
        assert len(events) == 3 and "пропал" in events[0] and "отключено" in events[1] and headset in events[2], events
        return 0
    finally:
        for tone in tones.values(): tone.stop()
        for module in modules.values(): pactl("unload-module", module)


if __name__ == "__main__":
    sys.exit(main())
