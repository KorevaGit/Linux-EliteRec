using EliteRec.Probe;
using Xunit;

namespace EliteRec.Probe.Tests;

public class MeasurementTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(-125)]
    public void DriftRecoversKnownSlopeDespiteStartupOffset(double ppm)
    {
        var points = Enumerable.Range(0, 100).Select(i => new ClockPoint(i * .25, 3 + i * .25 * (1 + ppm / 1e6)));
        Assert.Equal(ppm, Measurement.DriftPpm(points)!.Value, 5);
    }

    [Fact]
    public void StartupTransientIsExcluded()
    {
        var points = Enumerable.Range(0, 100).Select(i => new ClockPoint(i * .25, i < 12 ? -100 : i * .25));
        Assert.Equal(0, Measurement.DriftPpm(points)!.Value, 5);
    }

    [Fact]
    public void ShortRunDoesNotPretendToMeasureDrift() =>
        Assert.Null(Measurement.DriftPpm(Enumerable.Range(0, 20).Select(i => new ClockPoint(i * .25, i * .25))));

    [Fact]
    public void PercentileIgnoresInvalidValuesAndInterpolates()
    {
        Assert.Equal(2.5, Measurement.Percentile([4, 1, double.NaN, 3, 2], .5));
        Assert.Null(Measurement.Percentile([], .95));
    }

    [Theory]
    [InlineData("--seconds", "0")]
    [InlineData("--seconds", "86401")]
    [InlineData("--unknown", "1")]
    public void InvalidOptionsFailBeforeOpeningAudio(string key, string value) =>
        Assert.Throws<ArgumentException>(() => ProbeOptions.Parse(["capture", key, value]));
}
