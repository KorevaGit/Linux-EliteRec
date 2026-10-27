using EliteRec.Core.Audio;
using Xunit;

namespace EliteRec.Audio.Pulse.Tests;

public class PulseRulesTests
{
    private static PulseNode Node(string id, AudioDeviceDirection direction, bool monitor = false,
        string? monitorName = null, Dictionary<string, string>? properties = null) =>
        new(id, "Гарнитура", direction, properties ?? [], monitor, null, null, false, monitorName);

    [Fact]
    public void MonitorIsExcludedFromMicrophonesButLinkedToOutput()
    {
        var monitor = Node("custom-monitor", AudioDeviceDirection.Capture, true);
        var input = Node("mic", AudioDeviceDirection.Capture);
        var sink = Node("output", AudioDeviceDirection.Render, monitorName: monitor.Name);
        var snapshot = PulseDeviceRules.Snapshot([input, monitor], [sink], "mic", "output");
        Assert.Equal("mic", Assert.Single(snapshot.Inputs).Id);
        Assert.Equal("custom-monitor", Assert.Single(snapshot.Outputs).CaptureSourceId);
    }

    [Fact]
    public void MonitorClassIsExcludedEvenWhenIndexFlagIsMissing()
    {
        var node = Node("filtered", AudioDeviceDirection.Capture, properties: new() { ["device.class"] = "monitor" });
        Assert.False(PulseDeviceRules.IsUsableInput(node));
    }

    [Fact]
    public void MissingMonitorIsNotGuessedFromSinkSuffix()
    {
        var sink = Node("output", AudioDeviceDirection.Render, monitorName: "missing-monitor");
        Assert.Null(Assert.Single(PulseDeviceRules.Snapshot([], [sink], null, "output").Outputs).CaptureSourceId);
    }

    [Fact]
    public void PortAndCardDescriptionMakeFriendlyNameWithoutChangingId()
    {
        var node = Node("stable-id", AudioDeviceDirection.Capture, properties: new() { ["device.description"] = "Jabra" })
            with { PortDescription = "Микрофон" };
        var device = Assert.Single(PulseDeviceRules.Snapshot([node], [], node.Name, null).Inputs);
        Assert.Equal("Микрофон (Jabra)", device.DisplayName);
        Assert.Equal("stable-id", device.Id);
    }

    [Fact]
    public void VolumePropertyIsNotADeviceChange()
    {
        var before = Node("mic", AudioDeviceDirection.Capture, properties: new() { ["volume"] = "100" });
        var after = before with { Properties = new Dictionary<string, string> { ["volume"] = "20" } };
        Assert.Empty(AudioDeviceChanges.Between(PulseDeviceRules.Snapshot([before], [], "mic", null),
            PulseDeviceRules.Snapshot([after], [], "mic", null)));
    }

    [Fact]
    public void ChangingJackPortIsAStateChange()
    {
        var before = Node("out", AudioDeviceDirection.Render) with { PortName = "speakers" };
        var after = before with { PortName = "headphones" };
        Assert.Equal(AudioDeviceChangeKind.StateChanged, Assert.Single(AudioDeviceChanges.Between(
            PulseDeviceRules.Snapshot([], [before], null, "out"),
            PulseDeviceRules.Snapshot([], [after], null, "out"))).Kind);
    }
}
