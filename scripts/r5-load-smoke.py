#!/usr/bin/env python3
"""R5: нагрузка записи и устойчивость к подвисаниям системы (слабый ноутбук, HDD, своп).

Два null-sink с тонами (микрофон 440 Гц, система 660 Гц), Recorder пишет MP3 --seconds секунд.
1. Через 8 с — CPU и память процесса записи за 10 с спокойной работы.
2. Затем процесс замораживается SIGSTOP на 1, 3 и 6 с (так выглядит подвисание: система не
   даёт процессу времени), между заморозками — по 4 с работы.
Проверяем: запись не оборвалась (код 0), длина файла — сколько задано, оба канала звучат
в конце, пропуски (если были) отмечены в файле событий, а не обрывают запись.
"""
import argparse, array, json, math, os, signal, subprocess, sys, threading, time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "tools/EliteRec.Recorder/bin/Release/net10.0/EliteRec.Recorder.dll"


def pactl(*args):
    return subprocess.check_output(["pactl", *args], text=True, timeout=5).strip()


class Tone:
    def __init__(self, sink, hz):
        self.stopped = threading.Event()
        chunk = array.array("h", (int(9000 * math.sin(2 * math.pi * hz * i / 48000)) for i in range(4800))).tobytes()
        self.player = subprocess.Popen(["paplay", "--raw", "--format=s16le", "--rate=48000", "--channels=1",
                                        "--latency-msec=50", "--device=" + sink], stdin=subprocess.PIPE, stderr=subprocess.DEVNULL)

        def feed():
            try:
                while not self.stopped.is_set():
                    self.player.stdin.write(chunk); self.player.stdin.flush()
            except (BrokenPipeError, ValueError, OSError):
                pass
        threading.Thread(target=feed, daemon=True).start()

    def stop(self):
        self.stopped.set(); self.player.kill()


def usage(pid):
    tick = os.sysconf("SC_CLK_TCK")
    fields = open(f"/proc/{pid}/stat").read().rsplit(")", 1)[1].split()
    rss = next(int(l.split()[1]) for l in open(f"/proc/{pid}/status") if l.startswith("VmRSS"))
    return (int(fields[11]) + int(fields[12])) / tick, rss // 1024


def decode(path):
    raw = subprocess.check_output(["gst-launch-1.0", "-q", "filesrc", "location=" + str(path), "!", "decodebin", "!", "audioconvert", "!",
                                   "audioresample", "!", "audio/x-raw,format=S16LE,channels=2,rate=48000,layout=interleaved", "!", "fdsink", "fd=1"],
                                  stderr=subprocess.DEVNULL)
    return array.array("h", raw)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--seconds", type=int, default=40)
    parser.add_argument("--freezes", default="1,3,6")
    parser.add_argument("--format", default="mp3", choices=["mp3", "wav", "flac", "opus"])
    options = parser.parse_args()
    directory = ROOT / "artifacts/r5" / time.strftime("%Y%m%d-%H%M%S")
    directory.mkdir(parents=True)
    tag = f"eliterec_r5_{os.getpid()}"
    original = json.loads(pactl("--format=json", "info"))
    modules, tones = [], []
    try:
        for name, hz in ((tag + "_mic", 440), (tag + "_sys", 660)):
            modules.append(pactl("load-module", "module-null-sink", "sink_name=" + name, "rate=48000"))
        pactl("set-default-sink", original["default_sink_name"]); pactl("set-default-source", original["default_source_name"])
        tones = [Tone(tag + "_mic", 440), Tone(tag + "_sys", 660)]
        base = directory / "call"
        with (directory / "recorder.log").open("w") as log:
            recorder = subprocess.Popen(["dotnet", str(DLL), "--source", tag + "_mic.monitor", "--system-source", tag + "_sys.monitor",
                                         "--output", str(base), "--format", options.format, "--seconds", str(options.seconds)], stdout=log, stderr=log)
            time.sleep(8)
            cpu0, _ = usage(recorder.pid); t0 = time.monotonic(); time.sleep(10)
            cpu1, rss = usage(recorder.pid)
            load = {"cpu_percent_of_core": round(100 * (cpu1 - cpu0) / (time.monotonic() - t0), 1), "rss_mb": rss}
            for freeze in [float(f) for f in options.freezes.split(",")]:
                os.kill(recorder.pid, signal.SIGSTOP); time.sleep(freeze); os.kill(recorder.pid, signal.SIGCONT)
                time.sleep(4)
            code = recorder.wait(timeout=options.seconds + 60)
        snapshot = json.loads(Path(str(base) + ".json").read_text())
        events_path = Path(str(base) + "_события.txt")
        events = events_path.read_text(encoding="utf-8").splitlines() if events_path.exists() else []
        frames = decode(Path(str(base) + "." + options.format))
        left, right = frames[0::2], frames[1::2]
        tail = slice(len(left) - 48000, len(left))
        rms = lambda x: math.sqrt(sum(v * v for v in x) / max(1, len(x))) / 32768
        report = {"exit": code, "load": load, "seconds": round(len(left) / 48000, 2), "error": snapshot.get("Error"),
                  "missing_ms": round((snapshot["Synchronization"]["MissingMicrophoneFrames"] + snapshot["Synchronization"]["MissingSystemFrames"]) / 48),
                  "tail_rms": [round(rms(left[tail]), 3), round(rms(right[tail]), 3)], "events": events, "folder": str(directory)}
        print(json.dumps(report, ensure_ascii=False, indent=2))
        assert code == 0, (directory / "recorder.log").read_text()
        assert abs(report["seconds"] - options.seconds) < 1, "длина записи не та"
        assert min(report["tail_rms"]) > .05, "после подвисаний канал замолчал"
        return 0
    finally:
        for tone in tones: tone.stop()
        for module in modules: pactl("unload-module", module)


if __name__ == "__main__":
    sys.exit(main())
