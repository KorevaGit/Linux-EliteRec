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
        // Уже запущен (ярлык при работающем автозапуске) — показать его окно и выйти.
        if (!SingleInstance.ClaimAsync(() => Avalonia.Threading.Dispatcher.UIThread.Post(App.ShowMainWindow)).GetAwaiter().GetResult())
            return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
