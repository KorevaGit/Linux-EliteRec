using Avalonia;
using EliteRec.Audio.Pulse;
using System.Text.Json;

namespace EliteRec.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Диагностический вход для приёмки R1 без графической сессии и pactl.
        if (args.Contains("--devices", StringComparer.Ordinal))
        {
            var snapshot = new PulseDeviceCatalog().ReadSnapshot();
            Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            return snapshot.ServerAvailable ? 0 : 2;
        }
        if (args.Contains("--watch-devices", StringComparer.Ordinal)) return WatchDevices();
        if (args.Contains("--check-codecs", StringComparer.Ordinal)) return CheckCodecs();
        // Автозапуск при входе в систему (пакет: /etc/xdg/autostart): сразу в трей, без окна.
        StartInTray = args.Contains("--tray", StringComparer.Ordinal);
        // Уже запущен (ярлык при работающем автозапуске) — показать его окно и выйти.
        if (!SingleInstance.ClaimAsync(() => Avalonia.Threading.Dispatcher.UIThread.Post(App.ShowMainWindow)).GetAwaiter().GetResult())
            return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static bool StartInTray { get; private set; }

    /// <summary>
    /// Диагностика установки: секунда тишины в каждом формате во временную папку. Проверяет, что
    /// системные кодеки (libmp3lame, libFLAC, libopus) найдены и работают; код 0 — все форматы пишутся.
    /// </summary>
    private static int CheckCodecs()
    {
        string folder = Directory.CreateTempSubdirectory("eliterec-codecs-").FullName;
        int failed = 0;
        try
        {
            foreach (var format in Enum.GetValues<EliteRec.Encoding.RecordingFormat>())
            {
                try
                {
                    using (var sink = new EliteRec.Encoding.RecordingSink(Path.Combine(folder, format.ToString()), format, mono: true))
                    {
                        float[] block = new float[480];
                        for (int i = 0; i < 100; i++) sink.Write(block, block, block);
                    }
                    Console.WriteLine($"{format}: ok");
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or InvalidOperationException)
                {
                    failed++;
                    Console.WriteLine($"{format}: ошибка — {ex.Message}");
                }
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
        return failed == 0 ? 0 : 1;
    }

    private static int WatchDevices()
    {
        using var stopped = new ManualResetEventSlim();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stopped.Set(); };
        Console.CancelKeyPress += handler;
        var watch = new PulseDeviceWatch((snapshot, changes) => Console.WriteLine(JsonSerializer.Serialize(new { Snapshot = snapshot, Changes = changes })));
        try { stopped.Wait(); }
        finally
        {
            watch.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.CancelKeyPress -= handler;
        }
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
