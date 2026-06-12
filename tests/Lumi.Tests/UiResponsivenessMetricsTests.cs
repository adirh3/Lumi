using System;
using Lumi.UiPerf;
using Xunit;

namespace Lumi.Tests;

public class UiResponsivenessMetricsTests
{
    [Fact]
    public void Percentile_UsesNearestRank()
    {
        var sorted = new[] { 10d, 20d, 30d, 40d, 50d, 60d, 70d, 80d, 90d, 100d };

        Assert.Equal(50d, LatencyStats.Percentile(sorted, 50d));
        Assert.Equal(100d, LatencyStats.Percentile(sorted, 95d));
        Assert.Equal(100d, LatencyStats.Percentile(sorted, 99d));
        Assert.Equal(10d, LatencyStats.Percentile(sorted, 0d));
    }

    [Fact]
    public void Percentile_SingleValue_ReturnsThatValue()
    {
        Assert.Equal(42d, LatencyStats.Percentile(new[] { 42d }, 99d));
    }

    [Fact]
    public void FromLatencies_ComputesJankBuckets()
    {
        var stats = LatencyStats.FromLatencies(new[] { 10d, 60d, 120d, 300d });

        Assert.Equal(4, stats.Count);
        Assert.Equal(300d, stats.MaxMs);
        Assert.Equal(122.5d, stats.MeanMs, 3);
        Assert.Equal(3, stats.JankCount);   // > 50ms
        Assert.Equal(2, stats.BadJankCount); // > 100ms
        Assert.Equal(1, stats.FreezeCount);  // > 250ms
    }

    [Fact]
    public void FromLatencies_Empty_ReturnsEmpty()
    {
        var stats = LatencyStats.FromLatencies(Array.Empty<double>());

        Assert.Equal(0, stats.Count);
        Assert.Equal(0d, stats.P99Ms);
        Assert.Same(LatencyStats.Empty, stats);
    }

    [Theory]
    [InlineData(10d, 10d, ImpactLevel.Good)]
    [InlineData(60d, 60d, ImpactLevel.Moderate)]   // p99 >= 50
    [InlineData(120d, 120d, ImpactLevel.High)]     // p99 >= 100
    [InlineData(210d, 210d, ImpactLevel.Critical)] // p99 >= 200
    [InlineData(10d, 320d, ImpactLevel.Critical)]  // max stall >= 300 dominates
    [InlineData(10d, 160d, ImpactLevel.High)]      // max stall >= 150
    public void Classify_AppliesThresholds(double p99, double max, ImpactLevel expected)
    {
        Assert.Equal(expected, UiImpactClassifier.Classify(p99, max));
    }

    [Fact]
    public void Score_RanksWorseLatencyHigher()
    {
        var mild = UiImpactClassifier.Score(40d, 50d);
        var severe = UiImpactClassifier.Score(400d, 900d);

        Assert.True(severe > mild);
        Assert.Equal((100d * 0.6d) + (200d * 0.4d), UiImpactClassifier.Score(100d, 200d), 3);
    }

    [Fact]
    public void Label_MapsEnum()
    {
        Assert.Equal("CRITICAL", UiImpactClassifier.Label(ImpactLevel.Critical));
        Assert.Equal("HIGH", UiImpactClassifier.Label(ImpactLevel.High));
        Assert.Equal("MODERATE", UiImpactClassifier.Label(ImpactLevel.Moderate));
        Assert.Equal("GOOD", UiImpactClassifier.Label(ImpactLevel.Good));
    }
}
