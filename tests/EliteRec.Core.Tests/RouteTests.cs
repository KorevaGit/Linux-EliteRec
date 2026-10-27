using EliteRec.Core.Audio;
using EliteRec.Core.Recording;
using Xunit;

namespace EliteRec.Core.Tests;

public class RouteTests
{
    private static AudioDeviceSnapshot Desk(bool headset, string defaultInput = "builtin-mic", string defaultOutput = "speakers") => new(
        [new("builtin-mic", "Встроенный", AudioDeviceDirection.Capture), .. headset ? [new AudioDevice("jabra-mic", "Jabra", AudioDeviceDirection.Capture)] : Array.Empty<AudioDevice>()],
        [new("speakers", "Динамики", AudioDeviceDirection.Render, CaptureSourceId: "speakers.monitor"),
         .. headset ? [new AudioDevice("jabra-out", "Jabra", AudioDeviceDirection.Render, CaptureSourceId: "jabra-out.monitor")] : Array.Empty<AudioDevice>()],
        defaultInput, defaultOutput);

    [Fact]
    public void ChosenDeviceIsNeverReplacedByDefaultWhilePresent()
    {
        var route = RecordingRoute.For(Desk(headset: true), "jabra-mic", "jabra-out");
        Assert.Equal(new RecordingRoute("jabra-mic", "jabra-out.monitor"), route);
    }

    [Fact]
    public void UnpluggedDeviceFallsBackToSystemAndReturns()
    {
        var lost = RecordingRoute.For(Desk(headset: false), "jabra-mic", "jabra-out");
        Assert.Equal(new RecordingRoute("builtin-mic", "speakers.monitor", true, true), lost);
        var back = RecordingRoute.For(Desk(headset: true), "jabra-mic", "jabra-out");
        Assert.Equal(new RecordingRoute("jabra-mic", "jabra-out.monitor"), back);
    }

    [Fact]
    public void SystemChoiceFollowsDefaultChangeWithoutWarning()
    {
        Assert.Equal(new RecordingRoute("builtin-mic", "speakers.monitor"), RecordingRoute.For(Desk(true), null, null));
        var moved = RecordingRoute.For(Desk(true, "jabra-mic", "jabra-out"), null, null);
        Assert.Equal(new RecordingRoute("jabra-mic", "jabra-out.monitor"), moved);
    }

    [Fact]
    public void SilentHeadsetIsBypassedLikeUnpluggedOne()
    {
        var route = RecordingRoute.For(Desk(true), "jabra-mic", null, silentMicrophone: "jabra-mic");
        Assert.Equal(new RecordingRoute("builtin-mic", "speakers.monitor", MicrophoneSilent: true), route);
        // «Системный», и умолчание — та же молчащая гарнитура: берём другой микрофон.
        var systemChoice = RecordingRoute.For(Desk(true, "jabra-mic"), null, null, silentMicrophone: "jabra-mic");
        Assert.Equal("builtin-mic", systemChoice.MicrophoneSource);
        Assert.True(systemChoice.MicrophoneSilent);
        // Других микрофонов нет — пишем молчащий, но предупреждаем.
        var only = new AudioDeviceSnapshot([new("jabra-mic", "Jabra", AudioDeviceDirection.Capture)], [], "jabra-mic", null);
        Assert.Equal(new RecordingRoute("jabra-mic", null, MicrophoneSilent: true), RecordingRoute.For(only, "jabra-mic", null, "jabra-mic"));
        // Молчит не выбранный, а другой — ничего не меняется.
        Assert.Equal(new RecordingRoute("jabra-mic", "speakers.monitor"), RecordingRoute.For(Desk(true), "jabra-mic", null, "builtin-mic"));
    }

    [Fact]
    public void NothingAvailableMeansSilenceNotCrash()
    {
        var empty = new AudioDeviceSnapshot([], [], null, null);
        Assert.Equal(new RecordingRoute(null, null, true, false), RecordingRoute.For(empty, "jabra-mic", null));
    }

    [Fact]
    public void RestartedChannelIsGapNotLossAndContinuesWithNewSource()
    {
        var mixer = new SynchronizedAudioMixer();
        mixer.Push(AudioChannel.Microphone, 0, Enumerable.Repeat(.25f, 48000).ToArray());
        mixer.Push(AudioChannel.System, 0, Enumerable.Repeat(-.5f, 48000).ToArray());
        float[] a = new float[480], b = new float[480];
        Assert.True(mixer.TryRender(0, a, b));
        Assert.All(a, x => Assert.Equal(.25f, x));

        mixer.Restart(AudioChannel.Microphone); // гарнитуру выдернули
        Assert.True(mixer.TryRender(4800, a, b)); // нового источника ещё нет — запись не стоит
        Assert.All(a, x => Assert.Equal(0, x));
        Assert.All(b, x => Assert.Equal(-.5f, x));

        mixer.Push(AudioChannel.Microphone, .2, Enumerable.Repeat(.75f, 24000).ToArray()); // системный микрофон
        Assert.True(mixer.TryRender(8640, a, b)); // 0,18 с — ещё до первого отсчёта нового источника
        Assert.All(a, x => Assert.Equal(0, x));
        Assert.True(mixer.TryRender(14400, a, b)); // 0,3 с — новый источник
        Assert.All(a, x => Assert.Equal(.75f, x));

        var stats = mixer.Statistics;
        Assert.Equal(0, stats.MissingMicrophoneFrames);
        Assert.Equal(960, stats.GapMicrophoneFrames);
        Assert.Equal(72000, stats.MicrophoneFrames);
    }
}
