using System.Buffers.Binary;
using EliteRec.Core.Recording;
using EliteRec.Encoding;
using EliteRec.Encoding.Native;
using Xunit;

namespace EliteRec.Core.Tests;

public unsafe class OpusTests
{
    [Fact]
    public void StereoChannelsStayIndependentAfterDecoding()
    {
        string root = Root();
        try
        {
            var files = Write(Path.Combine(root, "call"), 2, silentRight: true);
            byte[] data = File.ReadAllBytes(files.StereoPath);
            Assert.Equal(2, data[27 + 1 + 9]); // OpusHead: 2 канала
            Assert.Equal(1, data[27 + 1 + 18]); // семейство 1
            Assert.Equal(0, data[27 + 1 + 20]); // 0 связанных потоков

            var (left, right) = DecodeStereo(data);
            Assert.True(left > .1, $"RMS слева {left}");
            Assert.True(right < left / 1000, $"RMS справа {right} при {left} слева — каналы смешиваются");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FinishedStreamHasExactLengthAndEndOfStream()
    {
        string root = Root();
        try
        {
            var files = Write(Path.Combine(root, "call"), 2, silentRight: false);
            foreach (string path in new[] { files.MixPath, files.StereoPath })
            {
                var pages = OggPages.Read(File.ReadAllBytes(path));
                Assert.Equal(new FileInfo(path).Length, pages[^1].Start + pages[^1].Length); // все CRC верны
                Assert.Equal(0x02, pages[0].Flags);
                Assert.Equal(0x04, pages[^1].Flags & 0x04);
                Assert.Equal(2.0, PartFormatDuration(path), 6);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RecoveryCutsTornPageAndMarksEnd()
    {
        string root = Root();
        try
        {
            var files = Write(Path.Combine(root, "done"), 3, silentRight: false);
            byte[] whole = File.ReadAllBytes(files.StereoPath);
            File.WriteAllBytes(Path.Combine(root, ".torn_stereo.opus.part"), whole[..^50]);

            var found = Assert.Single(RecordingRecovery.Find(root, DateTime.UtcNow.AddMinutes(1)));
            Assert.Equal(RecordingFormat.Opus, found.Format);
            Assert.InRange(found.Duration.TotalSeconds, 2.0, 2.9999); // потеряна только оборванная последняя страница
            byte[] fixedOpus = File.ReadAllBytes(Assert.Single(RecordingRecovery.Recover(found)));
            var pages = OggPages.Read(fixedOpus);
            Assert.Equal(fixedOpus.Length, pages[^1].Start + pages[^1].Length);
            Assert.Equal(0x04, pages[^1].Flags & 0x04);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static double PartFormatDuration(string path) => OggPages.Scan(path).Duration.TotalSeconds;

    private static (double Left, double Right) DecodeStereo(byte[] data)
    {
        int error;
        byte* mapping = stackalloc byte[] { 0, 1 };
        nint decoder = Opus.DecoderCreate(48000, 2, 2, 0, mapping, &error);
        Assert.NotEqual(0, decoder);
        try
        {
            float[] pcm = new float[5760 * 2];
            double left = 0, right = 0; long count = 0;
            int skip = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(27 + 1 + 10)) + 4800; // pre-skip + 0,1 с на атаку
            foreach (byte[] packet in Packets(data).Skip(2))
            {
                int frames;
                fixed (byte* p = packet) fixed (float* o = pcm) frames = Opus.DecodeFloat(decoder, p, packet.Length, o, 5760, 0);
                Assert.True(frames > 0);
                for (int i = 0; i < frames; i++, skip--)
                {
                    if (skip > 0) continue;
                    left += pcm[2 * i] * pcm[2 * i]; right += pcm[2 * i + 1] * pcm[2 * i + 1]; count++;
                }
            }
            return (Math.Sqrt(left / count), Math.Sqrt(right / count));
        }
        finally { Opus.DecoderDestroy(decoder); }
    }

    private static IEnumerable<byte[]> Packets(byte[] data)
    {
        var current = new List<byte>();
        foreach (var page in OggPages.Read(data))
        {
            int segments = data[page.Start + 26], pos = (int)page.Start + 27 + segments;
            for (int i = 0; i < segments; i++)
            {
                int size = data[page.Start + 27 + i];
                current.AddRange(data.AsSpan(pos, size).ToArray()); pos += size;
                if (size < 255) { yield return current.ToArray(); current.Clear(); }
            }
        }
    }

    private static RecordingFiles Write(string basePath, int seconds, bool silentRight)
    {
        using var sink = new RecordingSink(basePath, RecordingFormat.Opus);
        float[] mic = new float[SynchronizedAudioMixer.BlockFrames], sys = new float[mic.Length], mix = new float[mic.Length];
        for (int block = 0; block < seconds * 100; block++)
        {
            for (int i = 0; i < mic.Length; i++)
            {
                double t = (block * mic.Length + i) / 48000.0;
                mic[i] = (float)(.3 * Math.Sin(2 * Math.PI * 440 * t));
                sys[i] = silentRight ? 0 : (float)(.3 * Math.Sin(2 * Math.PI * 660 * t));
                mix[i] = mic[i] + sys[i];
            }
            sink.Write(mic, sys, mix);
        }
        return sink.Files;
    }

    private static string Root() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eliterec-opus-" + Guid.NewGuid())).FullName;
}
