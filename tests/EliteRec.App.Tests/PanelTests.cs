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
using EliteRec.Encoding;
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
            Assert.Equal(2, window.GetVisualDescendants().OfType<ComboBox>().Count()); // микрофон, вывод; формат — в настройках
            Assert.False(window.FindControl<Button>("StartRecordingButton")!.IsEnabled); // звука ещё нет
            Conversation(model, RecordingState.Preview);
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
    public void FormatIsSavedAndLockedWhileRecording()
    {
        List<DevicePreferences> saved = [];
        var model = new MainPanelModel(new(Format: "unknown"), saved.Add);
        Assert.Equal(RecordingFormat.Mp3, model.Format);
        model.Format = RecordingFormat.Flac;
        Assert.Equal("flac", Assert.Single(saved).Format);
        model.UpdateRecording(new(RecordingState.Preview, 0, 0, 0, new(0, 0, 0, 0, 0, 0)));
        Assert.Equal("FLAC", model.FormatChoice.Title);
        model.UpdateRecording(new(RecordingState.Recording, 0, 0, 0, new(0, 0, 0, 0, 0, 0)));
        model.Format = RecordingFormat.Wav;
        Assert.Equal(RecordingFormat.Flac, model.Format);
        Assert.Single(saved);
    }

    [Fact]
    public void FallbackRouteShowsWarningInsteadOfRecordingNotice()
    {
        var model = new MainPanelModel(new(), _ => { });
        model.UpdateRecording(new(RecordingState.Recording, 0, 0, 0, new(0, 0, 0, 0, 0, 0)));
        model.SetRoute(new("builtin-mic", "speakers.monitor", MicrophoneFallback: true));
        Assert.Equal("Микрофон отключён, пишем с системного", model.RecordingHint);
        model.SetRoute(new("jabra-mic", "speakers.monitor"));
        Assert.Contains("предупредите клиента", model.RecordingHint);
        model.SetRoute(new("jabra-mic", null));
        Assert.Contains("«Система» пишется тишиной", model.RecordingHint);
        model.SetRoute(new("builtin-mic", "speakers.monitor", MicrophoneSilent: true), silentSource: "jabra-mic");
        Assert.Equal("Гарнитура молчит (выключена?), пишем с системного микрофона", model.RecordingHint);
        model.SetRoute(new("jabra-mic", "speakers.monitor", MicrophoneSilent: true), silentSource: "jabra-mic");
        Assert.Contains("включите её", model.RecordingHint);
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
            Conversation(model, RecordingState.Recording);
            Assert.False(window.FindControl<Button>("StartRecordingButton")!.IsVisible);
            Assert.All(window.GetVisualDescendants().OfType<ComboBox>(), box => Assert.False(box.IsEnabled));
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            string folder = Path.Combine(AppContext.BaseDirectory, "snapshots");
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, "r2-recording.png"), PngBitmapEncoderOptions.Default);
            model.UpdateRecording(snapshot with { State = RecordingState.Paused });
            Assert.Contains("не пишется", model.RecordingHint);
            Assert.True(model.IsWarning);
            using var paused = window.CaptureRenderedFrame();
            paused!.Save(Path.Combine(folder, "r5-paused.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DeviceChoiceSurvivesNewDeviceList()
    {
        // Живое окно: список устройств приходит уже после показа, ComboBox сбрасывает выбор.
        var model = new MainPanelModel(new(), _ => { });
        var window = new MainWindow(model);
        window.Show();
        model.Apply(Desk);
        var combo = window.GetVisualDescendants().OfType<ComboBox>().First();
        Assert.Equal(model.Microphone, combo.SelectedItem);
        combo.SelectedItem = model.Microphones[1];
        Assert.Equal("mic", model.Microphone!.Id);
        model.Apply(new AudioDeviceSnapshot([], [], null, null));
        Assert.Equal(model.Microphone, combo.SelectedItem);
        Assert.Contains("недоступно", ((DeviceOption)combo.SelectedItem!).Title);
    }

    [Fact]
    public void WaveUsesDbScaleStubAndGlowAndSettlesOnPause()
    {
        Assert.Equal(1, LevelWave.Level(1)); // 0 дБ
        Assert.InRange(LevelWave.Level(.01f), .39, .40); // −40 дБ на шкале от −66 дБ
        var wave = new LevelWave();
        for (int i = 0; i < 40; i++) wave.Add(i * .05, .5f, 0, false);
        Assert.True(wave.LeftGlow.Opacity > .8); // говорит менеджер — фон слева подсвечен
        Assert.Equal(LevelWave.GlowFloor, wave.RightGlow.Opacity, 2);
        Assert.True(wave.LeftBars[0].Opacity > wave.LeftBars[^1].Opacity); // к краям прозрачнее
        for (int i = 0; i < 100; i++) wave.Add(2 + i * .05, .9f, .9f, true); // пауза: звук есть, волна оседает
        Assert.All(wave.LeftBars.Concat(wave.RightBars), bar => Assert.Equal(LevelWave.Stub * LevelWave.BarHeight, bar.Height, 1));
    }

    [Fact]
    public void WaveRisesAtAxisFirstAndSpreadsOutward()
    {
        var wave = new LevelWave();
        var bars = wave.LeftBars;
        wave.Add(0, .5f, 0, false);
        Assert.True(bars[0].Height > 60); // у оси — сразу, быстрая атака
        Assert.Equal(LevelWave.Stub * LevelWave.BarHeight, bars[5].Height, 1); // дальше — ещё тишина
        for (int i = 1; i < 10; i++) wave.Add(i * .05, .5f, 0, false);
        Assert.True(bars[5].Height > 60); // волна дошла
        Assert.All(wave.RightBars, bar => Assert.True(bar.Height < 10)); // система молчит — своя половина не дёргается
        double loud = bars[0].Height;
        wave.Add(.6, 0, 0, false);
        Assert.InRange(bars[0].Height, loud * .5, loud * .95); // спад медленнее подъёма
    }

    [Fact]
    public void ChipShowsDeviceNotItsRole()
    {
        Assert.Equal("JBL Quantum 350", new DeviceOption("a", "t", "Микрофон (JBL Quantum 350)").Chip);
        Assert.Equal("USB Headset", new DeviceOption("a", "t", "USB Headset").Chip);
        Assert.Equal("Системный (недоступен)", new DeviceOption(null, "Системный (недоступен)").Chip);
    }

    [Fact]
    public void SelfTestJudgesEachSideAndOffersPlayback()
    {
        var settings = new SettingsModel(RecordingFormat.Flac, testAvailable: true);
        Assert.Equal(RecordingFormat.Flac, settings.SelectedFormat.Value);
        Assert.True(settings.CanTest);
        settings.Begin();
        Assert.False(settings.CanTest);
        Assert.False(settings.IsIdle); // «Сохранить» и формат заблокированы, пока идёт проба
        settings.Report(.3f, 0, TimeSpan.FromSeconds(2.4));
        Assert.Contains("3", settings.TestStatus);
        settings.Finish(.05f, .0004f, "/tmp/check_стерео.wav"); // речь −26 дБ; система −68 дБ — тишина
        Assert.True(settings.MicHeard);
        Assert.False(settings.SystemHeard);
        Assert.Contains("не слышно", settings.SystemResult);
        Assert.True(settings.CanListen);
        Assert.False(SettingsModel.Heard(.002f)); // шум выключенного микрофона, −54 дБ
    }

    [Fact]
    public void SelfTestUnavailableWithoutAudioExplainsWhy()
    {
        var settings = new SettingsModel(RecordingFormat.Mp3, testAvailable: false);
        Assert.False(settings.CanTest);
        Assert.Contains("доступна", settings.TestStatus);
    }

    [AvaloniaFact]
    public void SettingsWindowRenders()
    {
        var settings = new SettingsModel(RecordingFormat.Mp3, testAvailable: true);
        var window = new SettingsWindow(settings, null);
        try
        {
            window.Show();
            settings.Finish(.05f, .0004f, "/tmp/check.wav");
            using var frame = window.CaptureRenderedFrame();
            string folder = Path.Combine(AppContext.BaseDirectory, "snapshots");
            Directory.CreateDirectory(folder);
            frame!.Save(Path.Combine(folder, "r5-settings.png"), PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    // Разговор по очереди: сначала говорит клиент, потом менеджер — у оси слева речь, справа затихающий хвост.
    private static void Conversation(MainPanelModel model, RecordingState state)
    {
        for (int i = 0; i < 26; i++)
        {
            bool client = i < 16;
            float speech(double k) => (float)(.08 + .6 * Math.Abs(Math.Sin(i * k)));
            model.UpdateRecording(new(state, 480000, client ? .003f : speech(1.1), client ? speech(.7) : .004f, new(0, 0, 0, 0, 0, 0)));
        }
    }
}
