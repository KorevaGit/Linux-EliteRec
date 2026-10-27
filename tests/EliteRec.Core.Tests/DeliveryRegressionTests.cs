using System.Text.Json;
using EliteRec.Core.Recording;
using Xunit;

namespace EliteRec.Core.Tests;

public class DeliveryRegressionTests
{
    private sealed record AudioTraceEvent(AudioChannel? Channel, double Time, long Frame, int Count);
    [Fact]
    public void DelayedDeliveryWaitsForSamplesInsteadOfInventingMissingAudio()
    {
        var events = JsonSerializer.Deserialize<AudioTraceEvent[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "delayed-delivery.json")))!;
        var mixer = new SynchronizedAudioMixer();
        float[] zeros = new float[48000], microphone = new float[480], system = new float[480];
        var pending = new Queue<long>();
        int waits = 0;
        foreach (var e in events)
        {
            if (e.Channel is { } channel) mixer.Push(channel, e.Time, zeros.AsSpan(0, e.Count));
            else pending.Enqueue(e.Frame);
            while (pending.TryPeek(out long frame))
            {
                if (!mixer.TryRender(frame, microphone, system)) { waits++; break; }
                pending.Dequeue();
            }
        }
        Assert.Equal(0, mixer.Statistics.MissingMicrophoneFrames + mixer.Statistics.MissingSystemFrames);
        Assert.True(waits > 0, "В журнале есть задержка доставки, тракт должен её переждать.");
        // После возврата доставки все уже запланированные блоки должны дописаться.
        foreach (var channel in Enum.GetValues<AudioChannel>())
        {
            var last = events.Last(e => e.Channel == channel);
            mixer.Push(channel, last.Time + last.Count / 48000.0, zeros.AsSpan(0, 24000));
        }
        while (pending.TryPeek(out long frame) && mixer.TryRender(frame, microphone, system)) pending.Dequeue();
        Assert.Empty(pending);
        Assert.Equal(0, mixer.Statistics.MissingMicrophoneFrames + mixer.Statistics.MissingSystemFrames);
    }
}
