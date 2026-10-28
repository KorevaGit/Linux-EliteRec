using Xunit;
using System.Buffers.Binary;
using EliteRec.Core.Recording;
using EliteRec.Encoding;

namespace EliteRec.Core.Tests;

public class Mp3Tests
{
    [Fact]
    public void Mp3PairIsCbrWithoutJointStereoAndHidesPartsUntilDone()
    {
        string root = Root();
        try
        {
            var files = WriteSeconds(Path.Combine(root, "call"), 2);
            Assert.Empty(Directory.GetFiles(root, ".*"));
            var mix = Frames(files.MonoPath!);
            var stereo = Frames(files.StereoPath);
            Assert.InRange(mix.Count, 82, 90); // 2 с = 83,3 кадра по 1152 отсчёта + задержка кодера.
            Assert.All(mix, h => { Assert.Equal(3u, (h >> 6) & 3); Assert.Equal(9u, (h >> 12) & 15); }); // MONO, 128.
            Assert.All(stereo, h => { Assert.Equal(0u, (h >> 6) & 3); Assert.Equal(11u, (h >> 12) & 15); }); // STEREO, 192.
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RecoveryCutsTornFrameAndRenamesParts()
    {
        string root = Root();
        try
        {
            var files = WriteSeconds(Path.Combine(root, "done"), 1);
            long whole = new FileInfo(files.StereoPath).Length;
            File.Copy(files.StereoPath, Path.Combine(root, ".crash.mp3.part"));
            File.Copy(files.MonoPath!, Path.Combine(root, ".crash_mono.mp3.part"));
            using (var torn = File.Open(Path.Combine(root, ".crash.mp3.part"), FileMode.Append))
                torn.Write([0xff, 0xfb, 0xb4, 0x00, 1, 2, 3]); // заголовок кадра без тела

            Assert.Empty(RecordingRecovery.Find(root)); // свежие файлы считаются активной записью
            var found = Assert.Single(RecordingRecovery.Find(root, DateTime.UtcNow.AddMinutes(1)));
            Assert.Equal("crash", found.Name);
            Assert.InRange(found.Duration.TotalSeconds, .9, 1.2);

            var saved = RecordingRecovery.Recover(found);
            Assert.Equal([Path.Combine(root, "crash.mp3"), Path.Combine(root, "crash_mono.mp3")], saved);
            Assert.Equal(whole, new FileInfo(saved[0]).Length);
            Assert.Empty(Directory.GetFiles(root, ".*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RecoveryNeverOverwritesAndDiscardDeletesParts()
    {
        string root = Root();
        try
        {
            var files = WriteSeconds(Path.Combine(root, "same"), 1);
            File.Copy(files.StereoPath, Path.Combine(root, ".same.mp3.part"));
            var found = Assert.Single(RecordingRecovery.Find(root, DateTime.UtcNow.AddMinutes(1)));
            Assert.Null(found.MonoPart);
            Assert.Equal([Path.Combine(root, "same-2.mp3")], RecordingRecovery.Recover(found));

            File.Copy(files.StereoPath, Path.Combine(root, ".gone.mp3.part"));
            RecordingRecovery.Discard(Assert.Single(RecordingRecovery.Find(root, DateTime.UtcNow.AddMinutes(1))));
            Assert.Empty(Directory.GetFiles(root, ".*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static RecordingFiles WriteSeconds(string basePath, int seconds)
    {
        using var sink = new RecordingSink(basePath, RecordingFormat.Mp3, mono: true);
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

    private static List<uint> Frames(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int[] rates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        var headers = new List<uint>();
        for (int pos = 0; pos < bytes.Length;)
        {
            uint h = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos));
            Assert.Equal(0xfffa0000u, h & 0xfffe0000);
            headers.Add(h);
            pos += 144000 * rates[(h >> 12) & 15] / 48000 + (int)((h >> 9) & 1);
        }
        return headers;
    }

    private static string Root() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eliterec-mp3-" + Guid.NewGuid())).FullName;
}
