using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using EliteRec.App;
using EliteRec.Core.Audio;
using EliteRec.Core.Settings;
using EliteRec.Core.Recording;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(EliteRec.App.Tests.HeadlessSetup))]

namespace EliteRec.App.Tests;

public static class HeadlessSetup
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class PanelTests
{
    private static readonly AudioDeviceSnapshot Desk = new(
        [new("mic", "USB Headset", AudioDeviceDirection.Capture)],
        [new("out", "USB Headset", AudioDeviceDirection.Render, CaptureSourceId: "monitor")], "mic", "out");

    [Fact]
    public void RefreshDoesNotSaveOrReplaceMissingSelection()
    {
        List<DevicePreferences> saved = [];
        var model = new MainPanelModel(new("usb-missing", null), saved.Add);
        model.Apply(Desk);
        Assert.Equal("usb-missing", model.Microphone!.Id);
        Assert.Contains("недоступно", model.Microphone.Title);
        Assert.Empty(saved);
        model.Microphone = model.Microphones.Single(d => d.Id == "mic");
        Assert.Equal(new("mic", null), Assert.Single(saved));
    }

    [Fact]
    public void DefaultDeviceCanChangeWithoutLosingSystemSelection()
    {
        var model = new MainPanelModel(new(), _ => throw new InvalidOperationException("Refresh must not save"));
        model.Apply(Desk);
        Assert.Null(model.Output!.Id);
        Assert.Contains("USB Headset", model.Output.Title);
        model.Apply(Desk with { DefaultOutputId = null });
        Assert.Null(model.Output!.Id);
        Assert.Contains("недоступен", model.Output.Title);
    }

    [Fact]
    public void SettingsWriteFailureIsVisible()
    {
        var model = new MainPanelModel(new(), _ => throw new IOException("disk full"));
        model.Apply(Desk);
        model.Microphone = model.Microphones.Single(d => d.Id == "mic");
        Assert.NotNull(model.SettingsError);
        Assert.Equal("mic", model.Microphone!.Id);
    }

    [Fact]
    public void SuccessfulSaveClearsTransientError()
    {
        bool fail = true;
        var model = new MainPanelModel(new(), _ => { if (fail) throw new IOException("disk full"); });
        model.Apply(Desk);
        model.Microphone = model.Microphones.Single(d => d.Id == "mic");
        Assert.NotNull(model.SettingsError);
        fail = false;
        model.Microphone = model.Microphones.Single(d => d.Id is null);
        Assert.Null(model.SettingsError);
    }

    [AvaloniaFact]
    public void ShellRendersRealLayoutAndCannotPretendToRecord()
    {
        var model = new MainPanelModel(new(), _ => { });
        model.Apply(Desk);
        var window = new MainWindow(model);
        try
        {
            window.Show();
            Assert.Equal(2, window.GetVisualDescendants().OfType<ComboBox>().Count());
            Assert.False(window.FindControl<Button>("StartRecordingButton")!.IsEnabled);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            string folder = Path.Combine(AppContext.BaseDirectory, "snapshots");
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, "r1-panel.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void RecordingControlsFollowBackendAndLockDeviceSelection()
    {
        var model = new MainPanelModel(new(), _ => { });
        model.Apply(Desk);
        var snapshot = new RecordingSnapshot(RecordingState.Preview, 0, .25f, .5f, new(0, 0, 0, 0, 0, 0));
        model.UpdateRecording(snapshot);
        Assert.True(model.CanStartRecording);
        model.UpdateRecording(snapshot with { State = RecordingState.Recording, RecordedFrames = 480000 });
        Assert.Equal("00:00:10", model.TimerLabel);
        Assert.False(model.CanChooseDevices);
        model.Microphone = model.Microphones.Single(d => d.Id == "mic");
        Assert.Null(model.Preferences.MicrophoneId);
        model.UpdateRecording(snapshot with { State = RecordingState.Paused, RecordedFrames = 480000 });
        Assert.Equal("00:00:10", model.TimerLabel);
        Assert.Equal("Продолжить", model.PauseLabel);
        Assert.True(model.CanStopRecording);
    }

    [Fact]
    public void CommandErrorSurvivesMeterUpdates()
    {
        var model = new MainPanelModel(new(), _ => { });
        model.SetCommandError("Нет места на диске");
        model.UpdateRecording(new(RecordingState.Preview, 0, 0, 0, new(0, 0, 0, 0, 0, 0)));
        Assert.Equal("Нет места на диске", model.RecordingHint);
    }

    [AvaloniaFact]
    public void RecordingPanelRendersLevelsAndPauseState()
    {
        var model = new MainPanelModel(new(), _ => { });
        model.Apply(Desk);
        var window = new MainWindow(model);
        try
        {
            window.Show();
            var snapshot = new RecordingSnapshot(RecordingState.Recording, 480000, .25f, .65f, new(0, 0, 0, 0, 0, 0));
            model.UpdateRecording(snapshot);
            Assert.False(window.FindControl<Button>("StartRecordingButton")!.IsVisible);
            Assert.All(window.GetVisualDescendants().OfType<ComboBox>(), box => Assert.False(box.IsEnabled));
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            string folder = Path.Combine(AppContext.BaseDirectory, "snapshots");
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, "r2-recording.png"), PngBitmapEncoderOptions.Default);
            model.UpdateRecording(snapshot with { State = RecordingState.Paused });
            Assert.Contains("не пишется", model.RecordingHint);
        }
        finally { window.Close(); }
    }
}
