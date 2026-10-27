using EliteRec.Core.Recording;

namespace EliteRec.Encoding;

/// <summary>
/// Пара MP3: микс моно 128 кбит/с и стерео 192 кбит/с, L=микрофон / R=система.
/// Пока идёт запись, файлы скрыты (<c>.имя.mp3.part</c>) и каждые 5 с сбрасываются на диск;
/// после падения их подбирает <see cref="RecordingRecovery"/>. Нормальное завершение
/// дописывает хвост кодера и переименовывает части в итоговые файлы.
/// </summary>
public sealed class Mp3RecordingSink : IRecordingSink
{
    internal const string PartSuffix = ".mp3.part";
    private const long SyncFrames = SynchronizedAudioMixer.SampleRate * 5L;
    private readonly Part _mix, _stereo;
    private long _sinceSync;
    private bool _disposed;
    public RecordingFiles Files { get; }
    public long Frames { get; private set; }

    public Mp3RecordingSink(string basePath)
    {
        string full = Path.GetFullPath(basePath);
        string folder = Path.GetDirectoryName(full)!, name = Path.GetFileName(full);
        Directory.CreateDirectory(folder);
        Files = new(full + ".mp3", full + "_stereo.mp3");
        if (File.Exists(Files.MixPath) || File.Exists(Files.StereoPath)) throw new IOException($"Файл записи уже существует: {name}.");
        _mix = new Part(PartPath(folder, name), Files.MixPath, stereo: false);
        try { _stereo = new Part(PartPath(folder, name + RecordingRecovery.StereoSuffix), Files.StereoPath, stereo: true); }
        catch { _mix.Abandon(); throw; }
    }

    internal static string PartPath(string folder, string name) => Path.Combine(folder, "." + name + PartSuffix);

    public void Write(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system, ReadOnlySpan<float> mix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (microphone.Length != system.Length || microphone.Length != mix.Length) throw new ArgumentException("Размеры каналов различаются.");
        _mix.Write(mix, []);
        _stereo.Write(microphone, system);
        Frames += mix.Length;
        _sinceSync += mix.Length;
        if (_sinceSync >= SyncFrames)
        {
            _sinceSync = 0;
            _mix.Sync(); _stereo.Sync();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mix.Complete(); }
        finally { _stereo.Complete(); }
    }

    private sealed class Part
    {
        private readonly string _partPath, _finalPath;
        private readonly FileStream _file;
        private readonly Mp3Encoder _encoder;

        internal Part(string partPath, string finalPath, bool stereo)
        {
            _partPath = partPath; _finalPath = finalPath;
            _file = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            try { _encoder = new Mp3Encoder(stereo); }
            catch { _file.Dispose(); File.Delete(partPath); throw; }
        }

        internal void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right) => _encoder.Write(left, right, _file);

        internal void Sync() => _file.Flush(flushToDisk: true);

        internal void Complete()
        {
            try
            {
                _encoder.Finish(_file);
                _file.Flush(flushToDisk: true);
            }
            finally { _encoder.Dispose(); _file.Dispose(); }
            File.Move(_partPath, _finalPath, overwrite: false);
        }

        internal void Abandon()
        {
            _encoder.Dispose(); _file.Dispose();
            File.Delete(_partPath);
        }
    }
}
