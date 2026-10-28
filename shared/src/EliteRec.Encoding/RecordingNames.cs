using System.Globalization;
using System.Text;
using EliteRec.Core.Recording;

namespace EliteRec.Encoding;

/// <summary>
/// Имена файлов записи: <c>2026-10-26_14-30_&lt;название&gt;.mp3</c> (стерео) и, если включён, <c>…_mono.mp3</c>.
/// Запись начинается без названия; название, введённое после «Стоп», дописывается переименованием.
/// </summary>
public static class RecordingNames
{
    public const int MaxTitleLength = 80;

    /// <summary>Свободное базовое имя (без расширения) для записи, начатой в <paramref name="start"/>.</summary>
    public static string NewBase(string folder, DateTime start, RecordingFormat format)
    {
        string stamp = start.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture);
        for (int n = 1; ; n++)
        {
            string name = n == 1 ? stamp : stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
            if (!Taken(folder, name, PartFormat.Extension(format))) return Path.Combine(folder, name);
        }
    }

    /// <summary>
    /// Название для имени файла: без символов, недопустимых в Linux и Windows (файлы уходят в Битрикс),
    /// без управляющих, пробелы схлопнуты, не длиннее <see cref="MaxTitleLength"/>. Пустое — null.
    /// </summary>
    public static string? CleanTitle(string? title)
    {
        if (title is null) return null;
        var text = new StringBuilder(title.Length);
        foreach (char c in title.Normalize(NormalizationForm.FormC))
            text.Append(char.IsControl(c) || "/\\:*?\"<>|".Contains(c) ? ' ' : c);
        string clean = string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        if (clean.Length > MaxTitleLength) clean = clean[..MaxTitleLength].TrimEnd('.', ' ');
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>
    /// Дописывает название к сохранённой записи: стерео, моно (если есть) и файл событий (если есть).
    /// Занятое имя получает «-2», «-3»… Без названия файлы не трогаются.
    /// </summary>
    public static RecordingFiles AddTitle(RecordingFiles files, string? title)
    {
        if (CleanTitle(title) is not { } clean) return files;
        string extension = Path.GetExtension(files.StereoPath);
        string folder = Path.GetDirectoryName(files.StereoPath)!;
        string oldName = Path.GetFileNameWithoutExtension(files.StereoPath);
        string name = oldName + "_" + clean;
        for (int n = 2; Taken(folder, name, extension); n++) name = oldName + "_" + clean + "-" + n.ToString(CultureInfo.InvariantCulture);

        string events = Path.Combine(folder, oldName + RecordingSink.EventsSuffix);
        var renamed = new RecordingFiles(Path.Combine(folder, name + extension),
            files.MonoPath is null ? null : Path.Combine(folder, name + RecordingRecovery.MonoSuffix + extension));
        File.Move(files.StereoPath, renamed.StereoPath);
        if (files.MonoPath is { } mono)
        {
            try { File.Move(mono, renamed.MonoPath!); }
            catch { File.Move(renamed.StereoPath, files.StereoPath); throw; } // пара не разъезжается
        }
        if (File.Exists(events)) File.Move(events, Path.Combine(folder, name + RecordingSink.EventsSuffix));
        return renamed;
    }

    // Занято, если есть готовый файл, файл событий или незавершённая часть (.имя.mp3.part) с этим именем.
    private static bool Taken(string folder, string name, string extension) =>
        new[] { name, name + RecordingRecovery.MonoSuffix }.Any(file =>
            File.Exists(Path.Combine(folder, file + extension)) || File.Exists(RecordingSink.PartPath(folder, file, extension)))
        || File.Exists(Path.Combine(folder, name + RecordingSink.EventsSuffix));
}
