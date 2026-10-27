using System.Globalization;
using System.Text.Json;
using EliteRec.Probe;

try
{
    var options = ProbeOptions.Parse(args);
    object? report = options.Mode switch
    {
        "devices" => AudioEnvironment.Read(),
        "capture" => CaptureProbe.Run(options),
        "encode" => EncodingProbe.Run(options),
        "help" => null,
        _ => throw new ArgumentException("Неизвестный режим. Используйте help."),
    };
    if (report is null)
    {
        Console.WriteLine("""
            EliteRec R0 — измерения, без записи речи в файлы.
            devices [--output report.json]
            capture [--seconds 20] [--source SOURCE_NAME] [--sink SINK_NAME] [--output report.json]
            encode [--seconds 120] [--output new-directory]
            capture сохраняет только статистику; encode пишет синтетические MP3.
            Для часового замера: capture --seconds 3600 --output artifacts/r0/hour.json
            """);
    }
    else
    {
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (options.Output is not null && options.Mode != "encode")
        {
            string path = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew));
            output.WriteLine(json);
        }
        Console.WriteLine(json);
    }
    return report is CaptureReport { Failure: not null } ? 3 : 0;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
    or DllNotFoundException or EntryPointNotFoundException or TimeoutException or JsonException
    or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine($"EliteRec.Probe: {ex.Message}");
    return 2;
}

namespace EliteRec.Probe
{
    internal sealed record ProbeOptions(string Mode, int Seconds, string? Source, string? Sink, string? Output)
    {
        internal static ProbeOptions Parse(string[] args)
        {
            string mode = args.Length == 0 ? "help" : args[0];
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
            {
                if (args[i] is not ("--seconds" or "--source" or "--sink" or "--output")
                    || i + 1 == args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    || !values.TryAdd(args[i], args[i + 1]))
                {
                    throw new ArgumentException($"Некорректный или повторный аргумент: {args[i]}");
                }
            }
            int seconds = mode == "encode" ? 120 : 20;
            if (values.TryGetValue("--seconds", out var raw)
                && !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                throw new ArgumentException("--seconds: требуется целое число.");
            if (seconds is < 1 or > 86400) throw new ArgumentException("--seconds: от 1 до 86400.");
            return new(mode, seconds, values.GetValueOrDefault("--source"),
                values.GetValueOrDefault("--sink"), values.GetValueOrDefault("--output"));
        }
    }
}
