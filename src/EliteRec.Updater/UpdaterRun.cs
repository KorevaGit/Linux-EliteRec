using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteRec.Updates;

namespace EliteRec.Updater;

/// <summary>Запущенный у пользователя EliteRec: чтобы закрыть перед установкой и поднять после.</summary>
internal sealed record RunningApp(int ProcessId, string User, IReadOnlyDictionary<string, string> Session);

/// <summary>То, что такт спрашивает у системы и просит у неё сделать (интерфейсом — ради проверок без root).</summary>
internal interface IMachine
{
    Version? InstalledVersion();
    bool IsEliteRec(int processId);
    IReadOnlyList<RunningApp> RunningApps();
    /// <summary>Штатно закрыть (SIGTERM — запись, если вдруг идёт, дописывается), подождать, потом добить.</summary>
    void Stop(IReadOnlyList<RunningApp> apps);
    bool Install(string package, out string output);
    void Relaunch(IReadOnlyList<RunningApp> apps);
}

/// <summary>Заводская настройка пакета (/opt/eliterec/provisioning.json): канал, пара, ключ линии.</summary>
internal sealed record Provisioning(Uri BaseUrl, string User, string Password, string ReleasesPublicKey)
{
    public static Provisioning Read(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = json.RootElement;
        var updates = root.GetProperty("updates");
        return new(new Uri(updates.GetProperty("baseURL").GetString()!.TrimEnd('/') + "/"), updates.GetProperty("user").GetString()!,
            updates.GetProperty("password").GetString()!, root.GetProperty("releasesPublicKey").GetString()!);
    }
}

internal enum Outcome { UpToDate, Deferred, Installed, Failed }

/// <summary>
/// Один такт обновляльщика (служба eliterec-updater, по таймеру раз в 10 минут). Порядок — docs/R6-PACKAGING.md:
/// манифест и его подпись → пакет и его отпечаток → отметка «ставлю» и проверка записи → закрыть EliteRec →
/// установить → поднять EliteRec обратно (и после неудачной установки — прежний).
/// </summary>
internal sealed class UpdaterRun(Provisioning provisioning, IMachine machine, HttpClient http, UpdateCoordination coordination,
    string stateDirectory, Action<string> log)
{
    private const long MaxManifest = 64 * 1024, MaxPackage = 1L << 30;

    public async Task<Outcome> RunAsync(CancellationToken cancellation)
    {
        var installed = machine.InstalledVersion();
        if (installed is null) { log("установленная версия не читается — такт пропущен"); return Outcome.Failed; }

        var manifestAddress = new Uri(provisioning.BaseUrl, ReleaseChannel.ManifestKey);
        ReleaseManifest manifest;
        try
        {
            byte[]? data = await GetAsync(manifestAddress, MaxManifest, cancellation);
            if (data is null) { log("на канале нет выпуска EliteRec"); return Outcome.UpToDate; }
            manifest = ReleaseManifest.Verified(data, provisioning.ReleasesPublicKey);
        }
        catch (ReleaseException error) { log("манифест отклонён: " + error.Message); return Outcome.Failed; }
        if (!manifest.IsServedFrom(manifestAddress)) { log($"пакет {manifest.Url} не в каталоге EliteRec этого канала — отклонён"); return Outcome.Failed; }
        if (!manifest.IsNewerThan(installed)) { log($"актуально: {installed.ToString(3)}"); return Outcome.UpToDate; }

        string package = Path.Combine(stateDirectory, ReleaseChannel.PackageName(manifest.Version));
        if (!await EnsurePackageAsync(manifest, package, cancellation)) return Outcome.Failed;

        // «Ставлю» — до проверки записи; приложение ставит «пишу» до проверки установки (UpdateCoordination).
        File.WriteAllText(coordination.InstallingMarkerPath, manifest.Version.ToString(3));
        try
        {
            var recording = coordination.RecordingProcesses(machine.IsEliteRec);
            if (recording.Count > 0) { log($"идёт запись (процесс {string.Join(", ", recording)}) — {manifest.Version.ToString(3)} поставится позже"); return Outcome.Deferred; }

            var apps = machine.RunningApps();
            if (apps.Count > 0) { log($"закрываю EliteRec: {string.Join(", ", apps.Select(a => a.User))}"); machine.Stop(apps); }
            bool installedOk = machine.Install(package, out string output);
            log((installedOk ? "поставлено " : "установка не удалась ") + manifest.Version.ToString(3) + "\n" + output.Trim());
            if (apps.Count > 0) machine.Relaunch(apps); // и после неудачи: прежняя версия на месте
            if (installedOk) Prune(package);
            return installedOk ? Outcome.Installed : Outcome.Failed;
        }
        finally { File.Delete(coordination.InstallingMarkerPath); }
    }

    private async Task<bool> EnsurePackageAsync(ReleaseManifest manifest, string package, CancellationToken cancellation)
    {
        if (File.Exists(package) && Matches(package, manifest)) return true;
        string part = package + ".part";
        try
        {
            using var request = Request(manifest.Url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode) { log($"пакет: ответ {(int)response.StatusCode}"); return false; }
            await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                for (int read; (read = await source.ReadAsync(buffer, cancellation)) > 0;)
                {
                    total += read;
                    if (total > MaxPackage || (manifest.Size > 0 && total > manifest.Size)) { log("пакет больше заявленного — отклонён"); return false; }
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
            }
            // Ставим только то, что подписано: отпечаток из подписанного манифеста.
            if (!Matches(part, manifest)) { log("отпечаток пакета не совпал с манифестом — отклонён"); return false; }
            File.Move(part, package, overwrite: true);
            return true;
        }
        catch (HttpRequestException error) { log("пакет не скачался: " + error.Message); return false; }
        finally { if (File.Exists(part)) File.Delete(part); }
    }

    private static bool Matches(string path, ReleaseManifest manifest)
    {
        using var file = File.OpenRead(path);
        return (manifest.Size <= 0 || file.Length == manifest.Size)
            && Convert.ToHexString(SHA256.HashData(file)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<byte[]?> GetAsync(Uri address, long limit, CancellationToken cancellation)
    {
        try
        {
            using var request = Request(address);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new ReleaseException($"канал ответил {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > limit) throw new ReleaseException("манифест слишком большой");
            byte[] data = await response.Content.ReadAsByteArrayAsync(cancellation);
            return data.Length > limit ? throw new ReleaseException("манифест слишком большой") : data;
        }
        catch (HttpRequestException error) { throw new ReleaseException("канал недоступен: " + error.Message); }
    }

    private HttpRequestMessage Request(Uri address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(provisioning.User + ":" + provisioning.Password)));
        return request;
    }

    private void Prune(string keep)
    {
        foreach (string old in Directory.EnumerateFiles(stateDirectory, "EliteRec-*.deb"))
            if (old != keep) File.Delete(old);
    }
}
