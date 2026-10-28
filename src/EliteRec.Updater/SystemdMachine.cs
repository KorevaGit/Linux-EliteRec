using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace EliteRec.Updater;

/// <summary>Настоящая машина: dpkg, /proc, systemd-run, apt-get.</summary>
internal sealed partial class SystemdMachine(Action<string> log) : IMachine
{
    public const string AppPath = "/opt/eliterec/EliteRec";
    // Что нужно, чтобы поднять окно в графическом сеансе пользователя (берём у закрытого процесса).
    private static readonly string[] SessionKeys = ["DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS", "XDG_RUNTIME_DIR",
        "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE", "HOME", "LANG", "LANGUAGE", "LC_ALL"];
    private static bool Systemd => Directory.Exists("/run/systemd/system");

    public Version? InstalledVersion()
    {
        var (code, output) = Run("dpkg-query", ["-W", "-f=${Version}", "eliterec"]);
        return code == 0 && Version.TryParse(output.Trim().Split('-', '+', '~')[0], out var version) ? version : null;
    }

    public bool IsEliteRec(int processId)
    {
        try
        {
            string? target = new FileInfo($"/proc/{processId}/exe").LinkTarget;
            return target is not null && (target == AppPath || target == AppPath + " (deleted)");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public IReadOnlyList<RunningApp> RunningApps()
    {
        var apps = new List<RunningApp>();
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || !IsEliteRec(pid)) continue;
            try
            {
                string uid = File.ReadLines($"/proc/{pid}/status").First(l => l.StartsWith("Uid:", StringComparison.Ordinal)).Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];
                var (code, user) = Run("id", ["-nu", uid]);
                if (code != 0) continue;
                var session = File.ReadAllText($"/proc/{pid}/environ").Split('\0')
                    .Select(pair => pair.Split('=', 2)).Where(p => p.Length == 2 && SessionKeys.Contains(p[0]))
                    .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
                apps.Add(new RunningApp(pid, user.Trim(), session));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return apps;
    }

    public void Stop(IReadOnlyList<RunningApp> apps)
    {
        foreach (var app in apps) _ = Kill(app.ProcessId, 15); // SIGTERM: приложение закрывается штатно
        var deadline = Stopwatch.StartNew();
        while (apps.Any(a => IsEliteRec(a.ProcessId)) && deadline.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(200);
        foreach (var app in apps.Where(a => IsEliteRec(a.ProcessId))) { log($"процесс {app.ProcessId} не закрылся — SIGKILL"); _ = Kill(app.ProcessId, 9); }
    }

    public bool Install(string package, out string output)
    {
        string[] apt = ["/usr/bin/apt-get", "install", "--yes", "-o", "DPkg::Lock::Timeout=600",
            "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold", package];
        // Служба сама в песочнице (ProtectSystem=strict) и в /opt писать не может — установка отдельным
        // юнитом, как у EliteSIP; --wait: отметка «ставлю» держится, пока установка не кончится.
        int code;
        (code, output) = Systemd
            ? Run("systemd-run", ["--wait", "--pipe", "--collect", "--quiet", "--unit=eliterec-install", "--service-type=exec",
                "--property=RuntimeMaxSec=30min", "--setenv=DEBIAN_FRONTEND=noninteractive", .. apt])
            : Run(apt[0], apt[1..], ("DEBIAN_FRONTEND", "noninteractive"));
        return code == 0;
    }

    public void Relaunch(IReadOnlyList<RunningApp> apps)
    {
        foreach (var app in apps.DistinctBy(a => a.User))
        {
            string[] env = [.. app.Session.Select(p => $"{p.Key}={p.Value}")];
            var (code, output) = Systemd
                ? Run("systemd-run", ["--user", "-M", app.User + "@", "--collect", "--quiet", .. env.Select(e => "--setenv=" + e), AppPath, "--tray"])
                : (1, "нет systemd");
            if (code != 0)
                (code, output) = Run("runuser", ["-u", app.User, "--", "env", .. env, "setsid", "-f", AppPath, "--tray"]);
            log(code == 0 ? $"EliteRec снова запущен у {app.User}" : $"EliteRec не поднялся у {app.User}: {output.Trim()}");
        }
    }

    private static (int Code, string Output) Run(string file, IEnumerable<string> arguments, params (string Key, string Value)[] environment)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) start.Environment[key] = value;
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, stdout.Result + stderr);
        }
        catch (System.ComponentModel.Win32Exception error) { return (127, error.Message); }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int processId, int signal);
}
