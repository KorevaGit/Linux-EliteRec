namespace EliteRec.Updates;

/// <summary>
/// «Не обновлять во время записи»: договорённость приложения и службы обновления через две отметки.
/// Приложение перед записью сначала ставит свою отметку «пишу» (<c>recording-&lt;pid&gt;</c> в общем каталоге),
/// потом смотрит, не идёт ли установка; служба сначала ставит «ставлю» (<see cref="InstallingMarker"/>), потом
/// смотрит отметки «пишу». При таком порядке хотя бы одна сторона увидит другую: либо служба отложит
/// установку до следующего такта, либо приложение не начнёт запись, пока ставится обновление.
/// Механизм обновлений не должен мешать записывать: нет каталога (запуск не из пакета) или не удалось
/// поставить отметку — запись идёт.
/// </summary>
public sealed class UpdateCoordination(string recordingDirectory = UpdateCoordination.RecordingDirectory,
    string installingMarker = UpdateCoordination.InstallingMarker)
{
    /// <summary>Общий каталог (1777, создаёт postinst), как <c>/var/lib/elitesip/updates</c> у EliteSIP.</summary>
    public const string RecordingDirectory = "/var/lib/eliterec/updates";
    /// <summary>RuntimeDirectory службы (0755): исчезает, когда служба кончилась, — даже после сбоя.</summary>
    public const string InstallingMarker = "/run/eliterec-updater/installing";
    /// <summary>Установка не длится дольше (RuntimeMaxSec); старше — забытая отметка, не помеха записи.</summary>
    public static readonly TimeSpan InstallingTimeout = TimeSpan.FromMinutes(35);
    private const string Prefix = "recording-";
    public string InstallingMarkerPath => installingMarker;
    private string? _marker;

    /// <summary>Перед записью. false — сейчас ставится обновление, запись начинать нельзя.</summary>
    public bool TryBeginRecording(int processId)
    {
        EndRecording();
        if (Directory.Exists(recordingDirectory))
        {
            string marker = Path.Combine(recordingDirectory, Prefix + processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                using (new FileStream(marker, FileMode.Create, FileAccess.Write, FileShare.Read)) { }
                _marker = marker;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Отметка записи: " + ex.Message); }
        }
        if (!Installing()) return true;
        EndRecording();
        return false;
    }

    public void EndRecording()
    {
        if (_marker is null) return;
        try { File.Delete(_marker); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Отметка записи: " + ex.Message); }
        _marker = null;
    }

    public bool Installing() =>
        File.Exists(installingMarker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(installingMarker) < InstallingTimeout;

    /// <summary>
    /// Для службы: процессы, отметившие запись. Читаются только имена (содержимое общего каталога root не
    /// открывает); живой ли это EliteRec, решает <paramref name="isEliteRec"/> — так подделанная или забытая
    /// после падения отметка обновления не блокирует.
    /// </summary>
    public IReadOnlyList<int> RecordingProcesses(Func<int, bool> isEliteRec)
    {
        if (!Directory.Exists(recordingDirectory)) return [];
        var processes = new List<int>();
        foreach (string path in Directory.EnumerateFiles(recordingDirectory, Prefix + "*"))
            if (int.TryParse(Path.GetFileName(path)[Prefix.Length..], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int pid) && isEliteRec(pid))
                processes.Add(pid);
        return processes;
    }
}
