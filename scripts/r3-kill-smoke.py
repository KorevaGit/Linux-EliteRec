#!/usr/bin/env python3
"""R3: kill -9 посреди записи -> части на диске -> восстановление Recorder --recover.

Два виртуальных null-sink с тоном, Recorder пишет MP3, через --kill-after секунд
процесс получает SIGKILL. Затем ждём окно активности (15 с) и восстанавливаем.
Проверяем: итоговые файлы есть, частей нет, длительность близка к записанной.
MP3: каждый кадр MPEG-1 L3 48 кГц, микс MONO 128, стерео STEREO 192 (не joint).
FLAC и Opus: файл целиком декодируется GStreamer (flacdec / oggdemux ! opusdec).
WAV: размеры в заголовке.
"""
import argparse, array, json, math, os, signal, subprocess, sys, threading, time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "tools/EliteRec.Recorder/bin/Release/net10.0/EliteRec.Recorder.dll"
RATES = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0]


def pactl(*args):
    return subprocess.check_output(["pactl", *args], text=True, timeout=5).strip()


def frames(path, mode, bitrate):
    data = path.read_bytes(); pos = count = 0
    while pos < len(data):
        h = int.from_bytes(data[pos:pos + 4], "big")
        assert h & 0xfffe0000 == 0xfffa0000 and (h >> 10) & 3 == 1, f"{path.name}: не MPEG-1 L3 48k на {pos}"
        assert (h >> 6) & 3 == mode and RATES[(h >> 12) & 15] == bitrate, f"{path.name}: режим/битрейт на {pos}"
        pos += 144000 * bitrate // 48000 + ((h >> 9) & 1); count += 1
    assert pos == len(data), f"{path.name}: обрезанный кадр"
    return count


def gst_seconds(path, *decoder):
    subprocess.run(["gst-launch-1.0", "-q", "filesrc", "location=" + str(path), "!", *decoder,
                    "!", "fakesink"], check=True, timeout=60)
    out = subprocess.check_output(["gst-discoverer-1.0", str(path)], text=True)
    h, m, sec = next(l for l in out.splitlines() if "Duration" in l).split(": ")[1].split(":")
    return int(h) * 3600 + int(m) * 60 + float(sec)


def wav_seconds(path, channels):
    data = path.read_bytes()
    assert int.from_bytes(data[22:24], "little") == channels
    size = int.from_bytes(data[40:44], "little")
    assert size == len(data) - 44 and int.from_bytes(data[4:8], "little") == len(data) - 8, f"{path.name}: размеры"
    return size / (2 * channels) / 48000


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--kill-after", type=float, default=13)
    parser.add_argument("--format", choices=["mp3", "flac", "opus", "wav"], default="mp3")
    options = parser.parse_args()
    directory = ROOT / "artifacts/r3" / time.strftime("%Y%m%d-%H%M%S")
    directory.mkdir(parents=True)
    names = [f"eliterec_r3_{os.getpid()}_{n}" for n in ("mic", "sys")]
    original = json.loads(pactl("--format=json", "info"))
    modules, players, stopped = [], [], threading.Event()
    try:
        for name in names:
            modules.append(pactl("load-module", "module-null-sink", "sink_name=" + name,
                                 "sink_properties=device.description=EliteRec_R3_Test", "rate=48000"))
        pactl("set-default-sink", original["default_sink_name"])
        pactl("set-default-source", original["default_source_name"])
        for name in names:
            players.append(subprocess.Popen(["paplay", "--raw", "--format=s16le", "--rate=48000", "--channels=1",
                                             "--latency-msec=50", "--device=" + name], stdin=subprocess.PIPE))
        tone = array.array("h", (int(9000 * math.sin(2 * math.pi * 440 * i / 48000)) for i in range(4800))).tobytes()

        def feed():
            try:
                while not stopped.is_set():
                    for player in players: player.stdin.write(tone); player.stdin.flush()
            except (BrokenPipeError, ValueError, OSError):
                pass
        threading.Thread(target=feed, daemon=True).start()

        base = directory / "call"
        with (directory / "recorder.log").open("w") as log:
            recorder = subprocess.Popen(["dotnet", str(DLL), "--source", names[0] + ".monitor", "--system-source",
                                         names[1] + ".monitor", "--output", str(base), "--seconds", "600",
                                         "--format", options.format], stdout=log, stderr=log)
            time.sleep(options.kill_after)
            assert recorder.poll() is None, "Recorder завершился раньше времени"
            os.kill(recorder.pid, signal.SIGKILL); recorder.wait()
        parts = sorted(p.name for p in directory.glob(".*.part"))
        ext = "." + options.format
        assert parts == [".call" + ext + ".part", ".call_mono" + ext + ".part"], parts
        early = json.loads(subprocess.check_output(["dotnet", str(DLL), "--recover", str(directory)], text=True))
        assert early == [], "свежие части не должны восстанавливаться"
        time.sleep(16)
        result = json.loads(subprocess.check_output(["dotnet", str(DLL), "--recover", str(directory)], text=True))
        assert len(result) == 1 and len(result[0]["Files"]) == 2, result
        assert not list(directory.glob(".*.part")), "части остались"
        mix_path, stereo_path = directory / ("call_mono" + ext), directory / ("call" + ext)
        if options.format == "mp3":
            mix, stereo = (frames(mix_path, 3, 128) * 1152 / 48000, frames(stereo_path, 0, 192) * 1152 / 48000)
        elif options.format == "flac":
            mix, stereo = (gst_seconds(p, "flacparse", "!", "flacdec") for p in (mix_path, stereo_path))
        elif options.format == "opus":
            mix, stereo = (gst_seconds(p, "oggdemux", "!", "opusdec") for p in (mix_path, stereo_path))
        else:
            mix, stereo = wav_seconds(mix_path, 1), wav_seconds(stereo_path, 2)
        seconds = min(mix, stereo)
        report = {"format": options.format, "kill_after_s": options.kill_after, "recovered_s": round(seconds, 2),
                  "mix_s": round(mix, 2), "stereo_s": round(stereo, 2), "folder": str(directory)}
        print(json.dumps(report, ensure_ascii=False, indent=2))
        # Прогрев 0,75 с и задержка запуска dotnet; сброс на диск — каждые 5 с, но kill -9 не теряет кэш ОС.
        assert seconds >= options.kill_after - 5, "восстановлено слишком мало"
        return 0
    finally:
        stopped.set()
        for player in players: player.kill()
        for module in reversed(modules): pactl("unload-module", module)


if __name__ == "__main__":
    sys.exit(main())
