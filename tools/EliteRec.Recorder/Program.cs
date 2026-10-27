using System.Diagnostics;
using System.Text.Json;
using EliteRec.Audio.Pulse;
using EliteRec.Core.Recording;
using EliteRec.Encoding;

try
{
    if (args.Length == 2 && args[0] == "--replay") return TraceReplay.Run(args[1]);
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || !new[] { "--source", "--system-source", "--output", "--seconds", "--pause-after", "--pause-for", "--close-active" }.Contains(args[i])
            || !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Некорректный аргумент: " + args[i]);
    }
    string Required(string key) => values.GetValueOrDefault(key) ?? throw new ArgumentException("Требуется " + key);
    int Number(string key, int fallback) => values.TryGetValue(key, out var value) && int.TryParse(value, out int n)
        ? n : values.ContainsKey(key) ? throw new ArgumentException("Требуется число: " + key) : fallback;
    int seconds = Number("--seconds", 10), pauseAfter = Number("--pause-after", 0), pauseFor = Number("--pause-for", 0);
    bool closeActive = Number("--close-active", 0) == 1;
    if (seconds is < 1 or > 20000 || pauseAfter < 0 || pauseFor < 0 || pauseAfter + pauseFor >= seconds)
        throw new ArgumentException("Некорректная длительность (1–20000 с) или пауза.");
    string path = Path.GetFullPath(Required("--output"));
    await using var session = await PulseRecordingSession.OpenAsync(Required("--source"), Required("--system-source"));
    using var cancelled = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
    Console.CancelKeyPress += handler;
    try
    {
        await Task.Delay(750, cancelled.Token); // Прогрев временной шкалы до начала сохранения.
        var clock = Stopwatch.StartNew();
        await session.StartRecordingAsync(() => new WavRecordingSink(path));
        bool paused = false, resumed = false;
        while (clock.Elapsed.TotalSeconds < seconds && !cancelled.IsCancellationRequested)
        {
            if (session.Snapshot.State == RecordingState.Faulted)
            {
                await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(session.Snapshot, new JsonSerializerOptions { WriteIndented = true }));
                throw new InvalidOperationException(session.Snapshot.Error);
            }
            if (pauseFor > 0 && !paused && clock.Elapsed.TotalSeconds >= pauseAfter) { await session.PauseAsync(); paused = true; }
            if (paused && !resumed && clock.Elapsed.TotalSeconds >= pauseAfter + pauseFor) { await session.ResumeAsync(); resumed = true; }
            await Task.Delay(10);
        }
        if (closeActive) await session.DisposeAsync();
        else await session.StopRecordingAsync();
        string json = JsonSerializer.Serialize(session.Snapshot, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path + ".json", json);
        Console.WriteLine(json);
    }
    finally { Console.CancelKeyPress -= handler; }
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or TimeoutException
    or OperationCanceledException or DllNotFoundException)
{
    Console.Error.WriteLine("EliteRec.Recorder: " + ex.Message);
    return 2;
}
