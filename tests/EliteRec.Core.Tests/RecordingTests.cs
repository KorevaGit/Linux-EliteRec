using System.Buffers.Binary;
using EliteRec.Core.Recording;
using EliteRec.Encoding;
using Xunit;

namespace EliteRec.Core.Tests;

public class RecordingTests
{
    [Fact]
    public void SourcesShareTimelineDespiteDifferentStartTimes()
    {
        var mixer = new SynchronizedAudioMixer();
        mixer.Push(AudioChannel.Microphone, 0, Enumerable.Repeat(.25f, 4800).ToArray());
        mixer.Push(AudioChannel.System, .02, Enumerable.Repeat(-.5f, 4800).ToArray());
        float[] a = new float[480], b = new float[480];
        mixer.Render(0, a, b);
        Assert.All(a, x => Assert.Equal(.25f, x));
        Assert.All(b, x => Assert.Equal(0, x));
        mixer.Render(1440, a, b);
        Assert.All(b, x => Assert.Equal(-.5f, x));
    }

    [Fact]
    public void MissingOrOverwrittenSamplesBecomeReportedSilence()
    {
        var mixer = new SynchronizedAudioMixer();
        mixer.Push(AudioChannel.Microphone, 0, new float[150000]);
        float[] a = new float[480], b = new float[480];
        mixer.Render(0, a, b);
        Assert.Equal(480, mixer.Statistics.MissingMicrophoneFrames);
        Assert.Equal(480, mixer.Statistics.MissingSystemFrames);
    }

    [Fact]
    public void SoftLimiterPreservesQuietSumAndBoundsLoudSum()
    {
        float[] output = new float[3];
        SynchronizedAudioMixer.Mix([.1f, 1, -1], [.2f, 1, -1], output);
        Assert.Equal(.3f, output[0], 6);
        Assert.InRange(output[1], .8f, 1);
        Assert.InRange(output[2], -1, -.8f);
    }

    [Theory]
    [InlineData(300, -250)]
    [InlineData(-500, 400)]
    public void SimulatedHourOfIndependentClocksStaysWithinFiftyMilliseconds(double micPpm, double sysPpm)
    {
        // Час именно моделируемого времени. Физическую часовую выдержку этот тест не заменяет.
        var mixer = new SynchronizedAudioMixer();
        double micRate = 48000 * (1 + micPpm / 1e6), sysRate = 48000 * (1 + sysPpm / 1e6);
        // Как в приложении, перед записью даём часам 1 с живого предпрослушивания.
        long micCount = 0, sysCount = 0, outputFrame = 48000;
        float[] micInput = new float[1000], sysInput = new float[1000], mic = new float[480], system = new float[480];
        double worst = 0;
        for (int tick = 1; tick <= 180_065; tick++)
        {
            double now = tick * .02;
            Submit(AudioChannel.Microphone, micRate, ref micCount, micInput, .003 * Math.Sin(now * 3));
            Submit(AudioChannel.System, sysRate, ref sysCount, sysInput, .003 * Math.Cos(now * 4));
            while (outputFrame + 480 <= (now - .25) * 48000 && outputFrame < 3601L * 48000)
            {
                mixer.Render(outputFrame, mic, system);
                double time = outputFrame / 48000.0;
                if (time > 10 && time % 100 is > 1 and < 99)
                {
                    // Пилообразный сигнал с известным наклоном .001/с измеряет ошибку времени напрямую.
                    double expected = time % 100 * .001;
                    worst = Math.Max(worst, Math.Max(Math.Abs(mic[0] - expected), Math.Abs(system[0] - expected)) / .001);
                }
                outputFrame += 480;
            }

            void Submit(AudioChannel channel, double rate, ref long counter, float[] buffer, double jitter)
            {
                long target = (long)(now * rate);
                int count = (int)(target - counter);
                for (int i = 0; i < count; i++) buffer[i] = (float)((counter + i) / rate % 100 * .001);
                mixer.Push(channel, counter / rate + jitter, buffer.AsSpan(0, count));
                counter = target;
            }
        }
        Assert.Equal(3601L * 48000, outputFrame);
        Assert.True(worst < .05, $"Ошибка таймлайна {worst * 1000:F2} мс");
        Assert.Equal(0, mixer.Statistics.MissingMicrophoneFrames);
        Assert.Equal(0, mixer.Statistics.MissingSystemFrames);
    }

    [Fact]
    public void WavPairHasCorrectHeadersAndIndependentChannels()
    {
        string root = Path.Combine(Path.GetTempPath(), "eliterec-wav-" + Guid.NewGuid());
        try
        {
            var sink = new WavRecordingSink(Path.Combine(root, "test"));
            var files = sink.Files;
            sink.Write([.25f, -.25f], [-.5f, .5f], [-.25f, .25f]);
            sink.Dispose(); sink.Dispose();
            byte[] mono = File.ReadAllBytes(files.MixPath), stereo = File.ReadAllBytes(files.StereoPath);
            Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(mono.AsSpan(22)));
            Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(stereo.AsSpan(22)));
            Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(stereo.AsSpan(24)));
            Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(stereo.AsSpan(40)));
            Assert.Equal(8192, BinaryPrimitives.ReadInt16LittleEndian(stereo.AsSpan(44)));
            Assert.Equal(-16384, BinaryPrimitives.ReadInt16LittleEndian(stereo.AsSpan(46)));
            Assert.Equal(stereo.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(stereo.AsSpan(4)));
            Assert.Throws<ObjectDisposedException>(() => sink.Write([0], [0], [0]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void WavSaturatesExtremeFiniteSamplesWithoutChangingSign()
    {
        string root = Path.Combine(Path.GetTempPath(), "eliterec-wav-" + Guid.NewGuid());
        try
        {
            RecordingFiles files;
            using (var sink = new WavRecordingSink(Path.Combine(root, "extreme")))
            {
                files = sink.Files;
                sink.Write([float.MaxValue, -float.MaxValue], [float.NaN, float.PositiveInfinity], [1, -1]);
            }
            var bytes = File.ReadAllBytes(files.StereoPath);
            Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44)));
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(46)));
            Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(48)));
            Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(50)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
