using System.Text.Json;
using EliteRec.Core.Recording;

internal sealed record AudioTraceEvent(AudioChannel? Channel, double Time, long Frame, int Count);

internal static class TraceReplay
{
    internal static int Run(string path)
    {
        var events = JsonSerializer.Deserialize<AudioTraceEvent[]>(File.ReadAllText(path))!;
        var mixer = new SynchronizedAudioMixer();
        float[] silence = new float[96000], microphone = new float[480], system = new float[480];
        double firstTime = events.First(e => e.Channel is null).Frame / 48000.0;
        double lastTime = events.Last(e => e.Channel is null).Frame / 48000.0;
        double warmup = Math.Min(20, (lastTime - firstTime) / 4);
        long missing = 0;
        var pending = new Queue<AudioTraceEvent>();
        foreach (var e in events)
        {
            if (e.Channel is { } channel) mixer.Push(channel, e.Time, silence.AsSpan(0, e.Count));
            else pending.Enqueue(e);
            while (pending.TryPeek(out var request))
            {
                var before = mixer.Statistics;
                if (!mixer.TryRender(request.Frame, microphone.AsSpan(0, request.Count), system.AsSpan(0, request.Count))) break;
                pending.Dequeue();
                var after = mixer.Statistics;
                if (request.Frame / 48000.0 - firstTime > warmup)
                    missing += after.MissingMicrophoneFrames + after.MissingSystemFrames - before.MissingMicrophoneFrames - before.MissingSystemFrames;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Events = events.Length, MissingFramesAfterWarmup = missing,
            PendingBlocksAwaitingDataAtEndOfTrace = pending.Count, Statistics = mixer.Statistics }, new JsonSerializerOptions { WriteIndented = true }));
        return missing == 0 ? 0 : 3;
    }
}
