using System.Diagnostics;
using System.Text.Json;

namespace EliteRec.Probe;

internal sealed record EncodingReport(DateTimeOffset StartedUtc, int AudioSeconds, double WallSeconds,
    double CpuSeconds, double OneCoreRealtimePercent, double RealtimeSpeed,
    long PeakWorkingSetBytes, Mp3Inspection Mix, Mp3Inspection SeparateChannels);

internal static class EncodingProbe
{
    internal static EncodingReport Run(ProbeOptions options)
    {
        string path = Path.GetFullPath(options.Output ?? Path.Combine("artifacts", "r0", "encode-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        if (Directory.Exists(path)) throw new ArgumentException("Каталог encode уже существует. Укажите новый --output.");
        Directory.CreateDirectory(path);
        using var mixFile = new FileStream(Path.Combine(path, "synthetic.mp3"), FileMode.CreateNew);
        using var stereoFile = new FileStream(Path.Combine(path, "synthetic_stereo.mp3"), FileMode.CreateNew);
        using var monoEncoder = new ProbeMp3Encoder(stereo: false);
        using var stereoEncoder = new ProbeMp3Encoder(stereo: true);
        using var process = Process.GetCurrentProcess();
        var started = DateTimeOffset.UtcNow;
        var cpuStart = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        float[] mic = new float[1024], system = new float[1024], mix = new float[1024];
        long total = options.Seconds * 48000L;
        for (long offset = 0; offset < total; offset += 1024)
        {
            int count = (int)Math.Min(1024, total - offset);
            for (int i = 0; i < count; i++)
            {
                double t = (offset + i) / 48000.0;
                // Независимые каналы, переменный уровень, паузы; только синтетика.
                mic[i] = (float)(.35 * Math.Sin(2 * Math.PI * 440 * t) * (.5 + .5 * Math.Sin(t * 3)));
                system[i] = (int)t % 7 == 0 ? 0 : (float)(.25 * Math.Sin(2 * Math.PI * 730 * t));
                mix[i] = mic[i] + system[i];
            }
            monoEncoder.Write(mix.AsSpan(0, count), mix.AsSpan(0, count), mixFile);
            stereoEncoder.Write(mic.AsSpan(0, count), system.AsSpan(0, count), stereoFile);
        }
        monoEncoder.Finish(mixFile);
        stereoEncoder.Finish(stereoFile);
        mixFile.Flush(flushToDisk: true); stereoFile.Flush(flushToDisk: true);
        clock.Stop();
        double cpu = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        mixFile.Position = 0; stereoFile.Position = 0;
        var report = new EncodingReport(started, options.Seconds, clock.Elapsed.TotalSeconds, cpu,
            cpu / options.Seconds * 100, options.Seconds / clock.Elapsed.TotalSeconds,
            process.PeakWorkingSet64, Mp3Inspection.Read(mixFile, false), Mp3Inspection.Read(stereoFile, true));
        File.WriteAllText(Path.Combine(path, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.Error.WriteLine($"Синтетические MP3 и отчёт: {path}");
        return report;
    }
}
