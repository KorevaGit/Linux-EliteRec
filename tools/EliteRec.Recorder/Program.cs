using System.Diagnostics;
using System.Text.Json;
using EliteRec.Audio.Pulse;
using EliteRec.Core.Recording;
using EliteRec.Encoding;

try
{
    if (args.Length == 2 && args[0] == "--replay") return TraceReplay.Run(args[1]);
    if (args.Length == 2 && args[0] == "--recover")
    {
        var recovered = RecordingRecovery.Find(Path.GetFullPath(args[1]))
            .Select(r => new { r.Name, Seconds = r.Duration.TotalSeconds, Files = RecordingRecovery.Recover(r) }).ToList();
        Console.WriteLine(JsonSerializer.Serialize(recovered, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 0; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || !new[] { "--source", "--system-source", "--output", "--seconds", "--pause-after", "--pause-for", "--close-active", "--format", "--fallback-source" }.Contains(args[i])
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
    string format = values.GetValueOrDefault("--format", "wav");
    if (format is not ("wav" or "mp3" or "flac" or "opus")) throw new ArgumentException("--format: wav, mp3, flac или opus.");
    await using var session = await PulseRecordingSession.OpenAsync(Required("--source"), Required("--system-source"));
    using var cancelled = new CancellationTokenSource();
    ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
    Console.CancelKeyPress += handler;
    try
    {
        await Task.Delay(750, cancelled.Token); // Прогрев временной шкалы до начала сохранения.
        var clock = Stopwatch.StartNew();
        await session.StartRecordingAsync(() => new RecordingSink(path, format switch { "mp3" => RecordingFormat.Mp3, "flac" => RecordingFormat.Flac, "opus" => RecordingFormat.Opus, _ => RecordingFormat.Wav }));
        bool paused = false, resumed = false;
        string microphone = Required("--source");
        string? fallback = values.GetValueOrDefault("--fallback-source");
        double nextReturn = 0;
        while (clock.Elapsed.TotalSeconds < seconds && !cancelled.IsCancellationRequested)
        {
            if (session.Snapshot.State == RecordingState.Faulted)
            {
                await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(session.Snapshot, new JsonSerializerOptions { WriteIndented = true }));
                throw new InvalidOperationException(session.Snapshot.Error);
            }
            // Как окно в R4: пропал или молчит (ровно нули > 3 с) микрофон — запасной источник;
            // раз в секунду пробуем вернуть исходный, только если он зазвучал.
            var current = session.Snapshot;
            if (fallback is not null && current.MicrophoneSource == microphone && current.MicrophoneSilenceSeconds > 3)
            {
                await session.SwitchSourceAsync(AudioChannel.Microphone, fallback, "Микрофон: " + fallback + " (выбранное устройство молчит — выключено?)");
                nextReturn = clock.Elapsed.TotalSeconds + 1;
            }
            else if (current.MicrophoneSource is null)
            {
                if (fallback is null) throw new InvalidOperationException("Источник звука отключён: Microphone");
                await session.SwitchSourceAsync(AudioChannel.Microphone, fallback, "Микрофон: " + fallback + " (выбранное устройство отключено)");
                nextReturn = clock.Elapsed.TotalSeconds + 1;
            }
            else if (current.MicrophoneSource == fallback && clock.Elapsed.TotalSeconds >= nextReturn)
            {
                nextReturn = clock.Elapsed.TotalSeconds + 1;
                // Не открылся — запасной продолжает писать (новый поток открывается до закрытия старого).
                try { await session.SwitchSourceAsync(AudioChannel.Microphone, microphone, "Микрофон: " + microphone, requireSound: true); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
            }
            if (current.SystemSource is null) throw new InvalidOperationException("Источник звука отключён: System");
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
