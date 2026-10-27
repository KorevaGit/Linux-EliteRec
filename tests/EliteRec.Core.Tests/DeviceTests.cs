using System.Text.Json;
using EliteRec.Core.Audio;
using EliteRec.Core.Settings;
using Xunit;

namespace EliteRec.Core.Tests;

public class DeviceTests
{
    private static readonly AudioDevice BuiltIn = new("builtin", "Микрофон", AudioDeviceDirection.Capture);
    private static readonly AudioDevice Usb = new("usb", "Микрофон", AudioDeviceDirection.Capture);
    private static readonly AudioDeviceSnapshot Desk = new([BuiltIn, Usb], [], "builtin", null);

    [Fact]
    public void ExplicitSelectionIgnoresDefaultChanges()
    {
        Assert.Equal(Usb, Desk.Resolve(AudioDeviceDirection.Capture, "usb"));
        Assert.Equal(Usb, (Desk with { DefaultInputId = "usb" }).Resolve(AudioDeviceDirection.Capture, "usb"));
        Assert.Equal(BuiltIn, Desk.Resolve(AudioDeviceDirection.Capture, null));
        Assert.Equal(Usb, (Desk with { DefaultInputId = "usb" }).Resolve(AudioDeviceDirection.Capture, null));
    }

    [Fact]
    public void MissingSelectionIsNotReplacedAndCanReturn()
    {
        var absent = Desk with { Inputs = [BuiltIn] };
        Assert.Null(absent.Resolve(AudioDeviceDirection.Capture, "usb"));
        var missing = Assert.Single(DeviceChoices.For(absent, AudioDeviceDirection.Capture, "usb"), c => c.Id == "usb");
        Assert.True(missing.IsMissing);
        Assert.False(Assert.Single(DeviceChoices.For(Desk, AudioDeviceDirection.Capture, "usb"), c => c.Id == "usb").IsMissing);
    }

    [Fact]
    public void EqualDisplayNamesDoNotMergeDifferentDevices() =>
        Assert.Equal(3, DeviceChoices.For(Desk, AudioDeviceDirection.Capture, "usb").Count);

    [Fact]
    public void UnpluggedPortDoesNotResolveAsUsable()
    {
        var unplugged = Desk with { Inputs = [BuiltIn, Usb with { IsAvailable = false }] };
        Assert.Null(unplugged.Resolve(AudioDeviceDirection.Capture, "usb"));
        Assert.Contains(AudioDeviceChanges.Between(Desk, unplugged), e => e.Kind == AudioDeviceChangeKind.StateChanged && e.DeviceId == "usb");
    }

    [Fact]
    public void DisconnectAndReturnUseTheSamePersistentId()
    {
        var absent = Desk with { Inputs = [BuiltIn] };
        Assert.Equal(new(AudioDeviceChangeKind.Removed, "usb", AudioDeviceDirection.Capture), Assert.Single(AudioDeviceChanges.Between(Desk, absent)));
        Assert.Equal(new(AudioDeviceChangeKind.Added, "usb", AudioDeviceDirection.Capture), Assert.Single(AudioDeviceChanges.Between(absent, Desk)));
    }

    [Fact]
    public void DefaultChangeDoesNotInventDeviceRemoval()
    {
        var change = Assert.Single(AudioDeviceChanges.Between(Desk, Desk with { DefaultInputId = "usb" }));
        Assert.Equal(AudioDeviceChangeKind.DefaultChanged, change.Kind);
    }

    [Fact]
    public void PreferencesSurviveRestartIncludingMissingIds()
    {
        string directory = Path.Combine(Path.GetTempPath(), "eliterec-test-" + Guid.NewGuid());
        try
        {
            var store = new DevicePreferencesStore(Path.Combine(directory, "devices.json"));
            Assert.Equal(new(), store.Load());
            store.Save(new("missing-usb", null));
            Assert.Equal(new("missing-usb", null), store.Load());
            store.Save(new(null, "output"));
            Assert.Equal(new(null, "output"), store.Load());
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MalformedSettingsAreNotSilentlyDiscarded()
    {
        string file = Path.Combine(Path.GetTempPath(), "eliterec-test-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(file, "broken");
            Assert.Throws<JsonException>(() => new DevicePreferencesStore(file).Load());
            Assert.Equal("broken", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }
}
