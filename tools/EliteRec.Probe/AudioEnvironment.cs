using System.Diagnostics;
using System.Text.Json;

namespace EliteRec.Probe;

internal sealed record ProbeDevice(string Name, string Description, string? MonitorSource);

internal sealed record AudioEnvironment(string Server, string DefaultSource, string DefaultSink,
    ProbeDevice[] Sources, ProbeDevice[] Sinks)
{
    internal static AudioEnvironment Read()
    {
        using var info = JsonDocument.Parse(Pactl("info"));
        using var sources = JsonDocument.Parse(Pactl("list", "sources"));
        using var sinks = JsonDocument.Parse(Pactl("list", "sinks"));
        var root = info.RootElement;
        return new(root.GetProperty("server_name").GetString()!,
            root.GetProperty("default_source_name").GetString()!,
            root.GetProperty("default_sink_name").GetString()!,
            Devices(sources), Devices(sinks));
    }

    private static ProbeDevice[] Devices(JsonDocument doc) => doc.RootElement.EnumerateArray().Select(d =>
        new ProbeDevice(d.GetProperty("name").GetString()!, d.GetProperty("description").GetString()!,
            d.TryGetProperty("monitor_source", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null)).ToArray();

    // pactl используется только в стенде. Продуктовый каталог будет на libpulse (R1).
    private static string Pactl(params string[] arguments)
    {
        var start = new ProcessStartInfo("pactl")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        start.ArgumentList.Add("--format=json");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить pactl.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("pactl не ответил за 5 с.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(error.GetAwaiter().GetResult().Trim());
        return output.GetAwaiter().GetResult();
    }
}
