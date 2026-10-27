using System.Buffers.Binary;
using EliteRec.Core.Recording;
using EliteRec.Encoding;
using Xunit;

namespace EliteRec.Core.Tests;

public class FormatTests
{
    [Fact]
    public void FlacPairHasStreamInfoWithLengthAndChannels()
    {
        string root = Root();
        try
        {
            var files = Write(Path.Combine(root, "call"), RecordingFormat.Flac, 2);
            Assert.EndsWith("call_stereo.flac", files.StereoPath);
            Assert.Equal((1, 96000L), StreamInfo(files.MixPath));
            Assert.Equal((2, 96000L), StreamInfo(files.StereoPath));
            Assert.Empty(Directory.GetFiles(root, ".*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FlacRecoveryKeepsWholeFramesOnly()
    {
        string root = Root();
        try
        {
            var files = Write(Path.Combine(root, "done"), RecordingFormat.Flac, 2);
            byte[] whole = File.ReadAllBytes(files.StereoPath);
            File.WriteAllBytes(Path.Combine(root, ".torn_stereo.flac.part"), whole[..^100]); // оборван посреди кадра
            File.WriteAllBytes(Path.Combine(root, ".torn.flac.part"), File.ReadAllBytes(files.MixPath)); // целый

            var found = Assert.Single(RecordingRecovery.Find(root, DateTime.UtcNow.AddMinutes(1)));
            Assert.Equal(RecordingFormat.Flac, found.Format);
            Assert.InRange(found.Duration.TotalSeconds, 1.95, 2.05);

            var saved = RecordingRecovery.Recover(found);
            Assert.Equal(File.ReadAllBytes(files.MixPath), File.ReadAllBytes(saved[0]));
            byte[] cut = File.ReadAllBytes(saved[1]);
            Assert.InRange(cut.Length, whole.Length - 20000, whole.Length - 101);
            Assert.True(cut.AsSpan().SequenceEqual(whole.AsSpan(0, cut.Length)));
            Assert.Equal(0xff, whole[cut.Length]); // обрезано ровно по началу последнего кадра
            Assert.Equal(0xf8, whole[cut.Length + 1]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void WavRecoveryRewritesSizesAndDropsHalfFrame()
    {
        string root = Root();
        try
        {
            var files = Write(Path.Combine(root, "done"), RecordingFormat.Wav, 1);
            byte[] torn = File.ReadAllBytes(files.StereoPath);
            BinaryPrimitives.WriteUInt32LittleEndian(torn.AsSpan(4), 36); // как после сбоя: размеры ещё нулевые
            BinaryPrimitives.WriteUInt32LittleEndian(torn.AsSpan(40), 0);
            File.WriteAllBytes(Path.Combine(root, ".torn_stereo.wav.part"), [.. torn, 1, 2, 3]);

            var found = Assert.Single(RecordingRecovery.Find(root, DateTime.UtcNow.AddMinutes(1)));
            Assert.Null(found.MixPart);
            Assert.Equal(1.0, found.Duration.TotalSeconds, 3);
            byte[] fixedWav = File.ReadAllBytes(Assert.Single(RecordingRecovery.Recover(found)));
            Assert.Equal(File.ReadAllBytes(files.StereoPath), fixedWav);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MarksGoToVisibleEventsFileOnlyWhenSomethingHappened()
    {
        string root = Root();
        try
        {
            using (var sink = new RecordingSink(Path.Combine(root, "quiet"), RecordingFormat.Wav)) { }
            Assert.False(File.Exists(Path.Combine(root, "quiet" + RecordingSink.EventsSuffix)));
            using (var sink = new RecordingSink(Path.Combine(root, "call"), RecordingFormat.Wav))
            {
                IRecordingSink marks = sink;
                marks.Mark(TimeSpan.FromSeconds(754), "Микрофон: источник пропал");
                marks.Mark(TimeSpan.FromSeconds(755), "Микрофон: Встроенный (выбранное устройство отключено)");
            }
            Assert.Equal(["00:12:34  Микрофон: источник пропал", "00:12:35  Микрофон: Встроенный (выбранное устройство отключено)"],
                File.ReadAllLines(Path.Combine(root, "call" + RecordingSink.EventsSuffix)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static (int Channels, long Samples) StreamInfo(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        Assert.True(b.AsSpan(0, 4).SequenceEqual("fLaC"u8));
        // STREAMINFO: https://www.rfc-editor.org/rfc/rfc9639#name-streaminfo
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(8 + 10));
        Assert.Equal(48000ul, packed >> 44);
        Assert.Equal(15ul, (packed >> 36) & 31); // 16 бит
        return ((int)((packed >> 41) & 7) + 1, (long)(packed & 0xfffffffff));
    }

    private static RecordingFiles Write(string basePath, RecordingFormat format, int seconds)
    {
        using var sink = new RecordingSink(basePath, format);
        float[] mic = new float[SynchronizedAudioMixer.BlockFrames], sys = new float[mic.Length], mix = new float[mic.Length];
        for (int block = 0; block < seconds * 100; block++)
        {
            for (int i = 0; i < mic.Length; i++)
            {
                double t = (block * mic.Length + i) / 48000.0;
                mic[i] = (float)(.3 * Math.Sin(2 * Math.PI * 440 * t));
                sys[i] = (float)(.3 * Math.Sin(2 * Math.PI * 660 * t));
                mix[i] = mic[i] + sys[i];
            }
            sink.Write(mic, sys, mix);
        }
        return sink.Files;
    }

    private static string Root() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eliterec-fmt-" + Guid.NewGuid())).FullName;
}
