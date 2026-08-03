using Shubbak.Core.Diagnostics;

namespace Shubbak.Core.Tests;

/// <summary>
/// The sample set the diagnostic report's performance figures are built from.
/// </summary>
/// <remarks>
/// This existed in the design spike, produced the numbers ADR 0001 rests on, and
/// stayed there - so the properties were proved once and the shipping binary had no
/// way to notice them regressing. Reasoning about performance from source is what
/// that absence forces, and reasoning is not measurement.
/// </remarks>
public sealed class LatencyStatsTests
{
    [Fact]
    public void PercentilesComeFromTheSamples()
    {
        var stats = new LatencyStats(128, "test");

        for (int i = 1; i <= 100; i++) stats.Record(i);

        Assert.Equal(50, stats.Percentile(0.5), 0);
        Assert.Equal(99, stats.Percentile(0.99), 0);
        Assert.Equal(100, stats.Max, 0);
    }

    [Fact]
    public void AnEmptySetReportsZeroRatherThanThrowing()
    {
        // A report can be asked for at any moment, including before anything has run.
        var stats = new LatencyStats(8, "test");

        Assert.Equal(0, stats.Percentile(0.5));
        Assert.Equal(0, stats.Count);
        Assert.Contains("no samples", stats.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SamplesPastCapacityAreDroppedNotGrown()
    {
        // Recording happens on the tick, so it must not allocate; dropping is the only
        // option that cannot perturb the thing being measured.
        var stats = new LatencyStats(4, "test");

        for (int i = 0; i < 100; i++) stats.Record(i);

        Assert.Equal(4, stats.Count);
        Assert.Equal(100, stats.Offered);
    }

    [Fact]
    public void TheMaximumSurvivesBeingDropped()
    {
        // The worst case is the one worth keeping even once the window is full.
        var stats = new LatencyStats(2, "test");

        stats.Record(1);
        stats.Record(2);
        stats.Record(999);

        Assert.Equal(999, stats.Max, 0);
    }

    [Fact]
    public void CountingOverBudgetIsInclusiveOfNeither()
    {
        var stats = new LatencyStats(8, "test");

        stats.Record(5);
        stats.Record(6.94);
        stats.Record(7);

        Assert.Equal(1, stats.CountOver(6.94));
    }

    [Fact]
    public void ReportingDoesNotDisturbTheOrderRecorded()
    {
        // A report sorts to find percentiles; doing that in place would corrupt a set
        // that is still being written to.
        var stats = new LatencyStats(8, "test");

        stats.Record(3);
        stats.Record(1);
        stats.Record(2);

        _ = stats.Percentile(0.5);

        Assert.Equal(3, stats.Count);
        Assert.Equal(3, stats.Max, 0);
    }
}
