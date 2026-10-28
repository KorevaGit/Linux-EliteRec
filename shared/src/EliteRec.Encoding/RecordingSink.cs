using EliteRec.Core.Recording;

namespace EliteRec.Encoding;

/// <summary>
/// Файлы записи в MP3, FLAC, Opus или WAV: стерео L=микрофон / R=система — всегда, под обычным именем
/// (его отправляют на расшифровку); общий моно-микс — по желанию, с суффиксом <c>_mono</c>.
/// Пока идёт запись, файлы скрыты (<c>.имя.mp3.part</c>) и каждые 5 с сбрасываются на диск;
/// после падения их подбирает <see cref="RecordingRecovery"/>. Нормальное завершение
/// дописывает хвост кодера и переименовывает части в итоговые файлы.
/// </summary>
public sealed class RecordingSink : IRecordingSink
{
    private const long SyncFrames = SynchronizedAudioMixer.SampleRate * 5L;
    private readonly Part _stereo;
    private readonly Part? _mono;
    private readonly string _eventsPath;
    private long _sinceSync;
    private bool _disposed;
    public RecordingFiles Files { get; }
    public long Frames { get; private set; }

    public RecordingSink(string basePath, RecordingFormat format, bool mono = false)
    {
        string full = Path.GetFullPath(basePath), extension = PartFormat.Extension(format);
        string folder = Path.GetDirectoryName(full)!, name = Path.GetFileName(full);
        Directory.CreateDirectory(folder);
        Files = new(full + extension, mono ? full + RecordingRecovery.MonoSuffix + extension : null);
        _eventsPath = full + EventsSuffix;
        if (Files.All.Any(File.Exists)) throw new IOException($"Файл записи уже существует: {name}.");
        _stereo = new Part(PartPath(folder, name, extension), Files.StereoPath, format, stereo: true);
        if (Files.MonoPath is { } monoPath)
        {
            try { _mono = new Part(PartPath(folder, name + RecordingRecovery.MonoSuffix, extension), monoPath, format, stereo: false); }
            catch { _stereo.Abandon(); throw; }
        }
    }

    /// <summary>Файл отметок рядом с записью; появляется только при первом событии.</summary>
    public const string EventsSuffix = "_события.txt";

    /// <summary>
    /// Строка «чч:мм:сс  текст» в файл отметок. Пишется сразу и сбрасывается на диск: файл обычный,
    /// не часть, поэтому переживает сбой без восстановления.
    /// </summary>
    public void Mark(TimeSpan at, string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var file = new FileStream(_eventsPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        file.Write(System.Text.Encoding.UTF8.GetBytes(at.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + text + "\n"));
        file.Flush(flushToDisk: true);
    }

    internal static string PartPath(string folder, string name, string extension) =>
        Path.Combine(folder, "." + name + extension + PartFormat.PartSuffix);

    public void Write(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system, ReadOnlySpan<float> mix)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (microphone.Length != system.Length || microphone.Length != mix.Length) throw new ArgumentException("Размеры каналов различаются.");
        _stereo.Write(microphone, system);
        _mono?.Write(mix, []);
        Frames += mix.Length;
        _sinceSync += mix.Length;
        if (_sinceSync >= SyncFrames)
        {
            _sinceSync = 0;
            _stereo.Sync(); _mono?.Sync();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stereo.Complete(); }
        finally { _mono?.Complete(); }
    }

    private sealed class Part
    {
        private readonly string _partPath, _finalPath;
        private readonly FileStream _file;
        private readonly IPartEncoder _encoder;

        internal Part(string partPath, string finalPath, RecordingFormat format, bool stereo)
        {
            _partPath = partPath; _finalPath = finalPath;
            _file = new FileStream(partPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            try { _encoder = PartFormat.Create(format, stereo, _file); }
            catch { _file.Dispose(); File.Delete(partPath); throw; }
        }

        internal void Write(ReadOnlySpan<float> left, ReadOnlySpan<float> right) => _encoder.Write(left, right);

        internal void Sync()
        {
            _encoder.Checkpoint();
            _file.Flush(flushToDisk: true);
        }

        internal void Complete()
        {
            try
            {
                _encoder.Finish();
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
