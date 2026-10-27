namespace EliteRec.Core.Recording;

public enum RecordingState { Preview, Recording, Paused, Faulted, Closed }

public sealed record RecordingFiles(string MixPath, string StereoPath);
public sealed record CaptureStatistics(long Holes, long Overflows, long UntimedFrames);
public sealed record RecordingSnapshot(RecordingState State, long RecordedFrames, float MicrophonePeak,
    float SystemPeak, SynchronizerStatistics Synchronization, string? Error = null, RecordingFiles? Files = null,
    CaptureStatistics? Capture = null)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(RecordedFrames / (double)SynchronizedAudioMixer.SampleRate);
}

public interface IRecordingSink : IDisposable
{
    RecordingFiles Files { get; }
    long Frames { get; }
    void Write(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system, ReadOnlySpan<float> mix);
}

public interface IRecordingSession : IAsyncDisposable
{
    RecordingSnapshot Snapshot { get; }
    Task StartRecordingAsync(Func<IRecordingSink> createSink);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopRecordingAsync();
}
