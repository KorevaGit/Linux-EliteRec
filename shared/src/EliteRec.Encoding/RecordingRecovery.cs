namespace EliteRec.Encoding;

/// <summary>Незавершённая запись: скрытые части, оставшиеся после падения программы или ПК.</summary>
public sealed record IncompleteRecording(string Folder, string Name, RecordingFormat Format, string? StereoPart,
    string? MonoPart, TimeSpan Duration, DateTime LastWriteTime);

/// <summary>Поиск и восстановление частей <see cref="RecordingSink"/>.</summary>
public static class RecordingRecovery
{
    internal const string MonoSuffix = "_mono";
    // Части активной записи сбрасываются каждые 5 с; свежие файлы могут принадлежать идущей записи.
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(15);

    public static IReadOnlyList<IncompleteRecording> Find(string folder, DateTime? nowUtc = null)
    {
        if (!Directory.Exists(folder)) return [];
        DateTime now = nowUtc ?? DateTime.UtcNow;
        var parts = new Dictionary<(string, RecordingFormat), (string? Stereo, string? Mono)>();
        foreach (string path in Directory.EnumerateFiles(folder, ".*" + PartFormat.PartSuffix))
        {
            if (now - File.GetLastWriteTimeUtc(path) < ActiveWindow) continue;
            string file = Path.GetFileName(path)[1..^PartFormat.PartSuffix.Length];
            if (PartFormat.FromExtension(Path.GetExtension(file)) is not { } format) continue;
            string name = Path.GetFileNameWithoutExtension(file);
            bool mono = name.EndsWith(MonoSuffix, StringComparison.Ordinal);
            if (mono) name = name[..^MonoSuffix.Length];
            parts.TryGetValue((name, format), out var pair);
            parts[(name, format)] = mono ? pair with { Mono = path } : pair with { Stereo = path };
        }
        return parts.OrderBy(p => p.Key.Item1, StringComparer.Ordinal).Select(p =>
        {
            var (name, format) = p.Key;
            string[] files = new[] { p.Value.Stereo, p.Value.Mono }.OfType<string>().ToArray();
            TimeSpan duration = files.Max(f => PartFormat.Inspect(format, f).Duration);
            return new IncompleteRecording(folder, name, format, p.Value.Stereo, p.Value.Mono, duration,
                files.Max(File.GetLastWriteTime));
        }).ToList();
    }

    /// <summary>Отрезает недописанный хвост, чинит заголовок и переименовывает части в обычные файлы. Возвращает итоговые пути.</summary>
    public static IReadOnlyList<string> Recover(IncompleteRecording recording)
    {
        var saved = new List<string>();
        string extension = PartFormat.Extension(recording.Format);
        foreach (var (part, suffix) in new[] { (recording.StereoPart, ""), (recording.MonoPart, MonoSuffix) })
        {
            if (part is null || !File.Exists(part)) continue;
            long length = PartFormat.Inspect(recording.Format, part).Bytes;
            if (length == 0) { File.Delete(part); continue; }
            using (var file = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                PartFormat.Repair(recording.Format, file, length);
                file.Flush(flushToDisk: true);
            }
            string target = Unique(Path.Combine(recording.Folder, recording.Name + suffix), extension);
            File.Move(part, target, overwrite: false);
            saved.Add(target);
        }
        return saved;
    }

    public static void Discard(IncompleteRecording recording)
    {
        foreach (string? part in new[] { recording.StereoPart, recording.MonoPart })
            if (part is not null) File.Delete(part);
    }

    private static string Unique(string basePath, string extension)
    {
        string path = basePath + extension;
        for (int i = 2; File.Exists(path); i++) path = $"{basePath}-{i}{extension}";
        return path;
    }
}
