using System.Buffers.Binary;

namespace EliteRec.Encoding;

/// <summary>Незавершённая запись: скрытые части MP3, оставшиеся после падения программы или ПК.</summary>
public sealed record IncompleteRecording(string Folder, string Name, string? MixPart, string? StereoPart,
    TimeSpan Duration, DateTime LastWriteTime);

/// <summary>Поиск и восстановление частей <see cref="Mp3RecordingSink"/>.</summary>
public static class RecordingRecovery
{
    internal const string StereoSuffix = "_stereo";
    private const int FrameSamples = 1152;
    // Части активной записи сбрасываются каждые 5 с; свежие файлы могут принадлежать идущей записи.
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(15);

    public static IReadOnlyList<IncompleteRecording> Find(string folder, DateTime? nowUtc = null)
    {
        if (!Directory.Exists(folder)) return [];
        DateTime now = nowUtc ?? DateTime.UtcNow;
        var parts = new Dictionary<string, (string? Mix, string? Stereo)>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(folder, "." + "*" + Mp3RecordingSink.PartSuffix))
        {
            if (now - File.GetLastWriteTimeUtc(path) < ActiveWindow) continue;
            string file = Path.GetFileName(path);
            string name = file[1..^Mp3RecordingSink.PartSuffix.Length];
            bool stereo = name.EndsWith(StereoSuffix, StringComparison.Ordinal);
            if (stereo) name = name[..^StereoSuffix.Length];
            parts.TryGetValue(name, out var pair);
            parts[name] = stereo ? pair with { Stereo = path } : pair with { Mix = path };
        }
        return parts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            long frames = Math.Max(Scan(p.Value.Mix).Frames, Scan(p.Value.Stereo).Frames);
            DateTime written = new[] { p.Value.Mix, p.Value.Stereo }.Where(f => f is not null)
                .Max(f => File.GetLastWriteTime(f!));
            return new IncompleteRecording(folder, p.Key, p.Value.Mix, p.Value.Stereo,
                TimeSpan.FromSeconds(frames * FrameSamples / 48000.0), written);
        }).ToList();
    }

    /// <summary>Отрезает недописанный последний кадр и переименовывает части в обычные файлы. Возвращает итоговые пути.</summary>
    public static IReadOnlyList<string> Recover(IncompleteRecording recording)
    {
        var saved = new List<string>();
        foreach (var (part, suffix) in new[] { (recording.MixPart, ""), (recording.StereoPart, StereoSuffix) })
        {
            if (part is null || !File.Exists(part)) continue;
            long length = Scan(part).Bytes;
            if (length == 0) { File.Delete(part); continue; }
            using (var file = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                file.SetLength(length);
                file.Flush(flushToDisk: true);
            }
            string target = Unique(Path.Combine(recording.Folder, recording.Name + suffix), ".mp3");
            File.Move(part, target, overwrite: false);
            saved.Add(target);
        }
        return saved;
    }

    public static void Discard(IncompleteRecording recording)
    {
        foreach (string? part in new[] { recording.MixPart, recording.StereoPart })
            if (part is not null) File.Delete(part);
    }

    private static string Unique(string basePath, string extension)
    {
        string path = basePath + extension;
        for (int i = 2; File.Exists(path); i++) path = $"{basePath}-{i}{extension}";
        return path;
    }

    /// <summary>Длина целых кадров MPEG-1 Layer III 48 кГц от начала файла.</summary>
    internal static (long Bytes, long Frames) Scan(string? path)
    {
        if (path is null || !File.Exists(path)) return (0, 0);
        int[] rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> header = stackalloc byte[4];
        long bytes = 0, frames = 0, length = input.Length;
        while (bytes + 4 <= length)
        {
            input.Position = bytes;
            input.ReadExactly(header);
            uint h = BinaryPrimitives.ReadUInt32BigEndian(header);
            int bitrate = rates[(h >> 12) & 15];
            if ((h & 0xfffe0000) != 0xfffa0000 || ((h >> 10) & 3) != 1 || bitrate == 0) break;
            long size = 144000 * bitrate / 48000 + ((h >> 9) & 1);
            if (bytes + size > length) break;
            bytes += size; frames++;
        }
        return (bytes, frames);
    }
}
