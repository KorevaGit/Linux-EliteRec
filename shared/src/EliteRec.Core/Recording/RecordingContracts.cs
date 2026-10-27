namespace EliteRec.Core.Recording;

public enum RecordingState { Preview, Recording, Paused, Faulted, Closed }

public sealed record RecordingFiles(string MixPath, string StereoPath);
public sealed record CaptureStatistics(long Holes, long Overflows, long UntimedFrames);
/// <param name="MicrophoneSource">Текущий source канала; null — источника нет, канал пишется тишиной.</param>
public sealed record RecordingSnapshot(RecordingState State, long RecordedFrames, float MicrophonePeak,
    float SystemPeak, SynchronizerStatistics Synchronization, string? Error = null, RecordingFiles? Files = null,
    CaptureStatistics? Capture = null, string? MicrophoneSource = null, string? SystemSource = null)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(RecordedFrames / (double)SynchronizedAudioMixer.SampleRate);
}

public interface IRecordingSink : IDisposable
{
    RecordingFiles Files { get; }
    long Frames { get; }
    void Write(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system, ReadOnlySpan<float> mix);
    /// <summary>Отметка о событии записи (переключение устройства) на позиции <paramref name="at"/>.</summary>
    void Mark(TimeSpan at, string text) { }
}

public interface IRecordingSession : IAsyncDisposable
{
    RecordingSnapshot Snapshot { get; }
    Task StartRecordingAsync(Func<IRecordingSink> createSink);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopRecordingAsync();
    /// <summary>
    /// Заменяет источник канала на ходу (null — источника нет, тишина). Во время записи
    /// <paramref name="note"/> попадает в отметки на текущей позиции.
    /// </summary>
    Task SwitchSourceAsync(AudioChannel channel, string? source, string note);
}
