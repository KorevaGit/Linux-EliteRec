using EliteRec.Probe;
using Xunit;

namespace EliteRec.Probe.Tests;

public class EncodingTests
{
    [Theory]
    [Trait("Category", "Native")]
    [InlineData(false)]
    [InlineData(true)]
    public void LameProducesRequestedFormatInEveryFrame(bool stereo)
    {
        using var encoder = new ProbeMp3Encoder(stereo);
        using var output = new MemoryStream();
        var left = Enumerable.Range(0, 1024).Select(i => (float)(.3 * Math.Sin(i * .08))).ToArray();
        float[] right = new float[1024];
        for (int i = 0; i < 47; i++) encoder.Write(left, right, output);
        encoder.Finish(output);
        output.Position = 0;
        var result = Mp3Inspection.Read(output, stereo);
        Assert.InRange(result.Frames, 40, 45);
        Assert.Equal(output.Length, result.Bytes);
        Assert.Equal(stereo ? "STEREO" : "MONO", result.Mode);
        Assert.Throws<InvalidOperationException>(() => encoder.Write(left, right, output));
    }

    [Fact]
    public void EmptyAndTruncatedMp3AreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Mp3Inspection.Read(new MemoryStream(), true));
        Assert.Throws<EndOfStreamException>(() => Mp3Inspection.Read(new MemoryStream([0xff, 0xfb]), true));
    }

    [Fact]
    public void JointStereoFrameIsRejected()
    {
        // MPEG-1 Layer III, 192k, 48k, JOINT_STEREO (mode bits 01).
        Assert.Throws<InvalidDataException>(() => Mp3Inspection.Read(new MemoryStream([0xff, 0xfb, 0xb4, 0x40]), true));
    }
}
