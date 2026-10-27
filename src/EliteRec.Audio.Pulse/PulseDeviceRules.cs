using EliteRec.Core.Audio;

namespace EliteRec.Audio.Pulse;

// Подмножество PulseDevice/PulseDeviceRules EliteSIP: только то, что нужно рекордеру.
// Телефонные правила эхоподавления, запасной гарнитуры и усиления сюда не переносятся.
internal sealed record PulseNode(string Name, string Description, AudioDeviceDirection Direction,
    IReadOnlyDictionary<string, string> Properties, bool IsMonitor, string? PortName,
    string? PortDescription, bool PortUnplugged, string? MonitorName = null);

internal static class PulseDeviceRules
{
    internal static bool IsUsableInput(PulseNode node) => node.Direction == AudioDeviceDirection.Capture
        && !node.IsMonitor && node.Properties.GetValueOrDefault("device.class") != "monitor";

    internal static string DisplayName(PulseNode node)
    {
        string? card = node.Properties.GetValueOrDefault("device.description");
        if (!string.IsNullOrWhiteSpace(card) && !string.IsNullOrWhiteSpace(node.PortDescription))
            return $"{node.PortDescription} ({card})";
        if (!string.IsNullOrWhiteSpace(card) && !string.IsNullOrWhiteSpace(node.Description)
            && !node.Description.Contains(card, StringComparison.Ordinal)) return $"{node.Description} ({card})";
        return string.IsNullOrWhiteSpace(node.Description) ? node.Name : node.Description;
    }

    internal static AudioDeviceSnapshot Snapshot(IReadOnlyList<PulseNode> sources, IReadOnlyList<PulseNode> sinks,
        string? defaultSource, string? defaultSink)
    {
        var inputs = sources.Where(IsUsableInput).Select(n => new AudioDevice(n.Name, DisplayName(n), n.Direction,
            !n.PortUnplugged, n.PortName, n.Name)).ToArray();
        var outputs = sinks.Select(n => new AudioDevice(n.Name, DisplayName(n), n.Direction, !n.PortUnplugged,
            n.PortName, sources.FirstOrDefault(s => s.Name == n.MonitorName && s.IsMonitor)?.Name)).ToArray();
        return new(Array.AsReadOnly(inputs), Array.AsReadOnly(outputs), defaultSource, defaultSink);
    }
}
