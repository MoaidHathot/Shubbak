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
    public void SamplesPastCapacityEvictTheOldest()
    {
        // Recording happens on the tick, so it must not allocate. It overwrites rather
        // than growing, and the set stays at capacity for the life of the process.
        var stats = new LatencyStats(4, "test");

        for (int i = 0; i < 100; i++) stats.Record(i);

        Assert.Equal(4, stats.Count);
        Assert.Equal(100, stats.Offered);
    }

    [Fact]
    public void ThePercentilesDescribeRecentBehaviourRatherThanTheFirstFewThousandTicks()
    {
        // The set used to fill once and drop everything afterwards, so a daemon that
        // had been up for an hour still reported its first few thousand ticks - which
        // for a window manager means startup, while it adopts windows and lays out for
        // the first time, and nothing since.
        //
        // Ninety-six then four: with a capacity of four, only the fours should be left.
        var stats = new LatencyStats(4, "test");

        for (int i = 0; i < 96; i++) stats.Record(1000);
        for (int i = 0; i < 4; i++) stats.Record(7);

        Assert.Equal(7, stats.Percentile(0.5), 0);
        Assert.Equal(7, stats.Percentile(0.99), 0);
        Assert.Equal(0, stats.CountOver(100));

        // The worst pause ever seen survives being overwritten, because it is usually
        // the only trace a stall leaves.
        Assert.Equal(1000, stats.Max, 0);
    }

    [Fact]
    public void WrappingSeveralTimesLeavesNoStaleSamples()
    {
        // Capacity three, nine samples: the ring has to come round exactly three times
        // and leave only the last three, with nothing from the earlier passes.
        var stats = new LatencyStats(3, "test");

        foreach (double sample in new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })
            stats.Record(sample);

        Assert.Equal(3, stats.Count);
        Assert.Equal(7, stats.Percentile(0.01), 0);
        Assert.Equal(9, stats.Percentile(0.99), 0);
    }

    [Fact]
    public void ResettingLetsTheNextSampleStartAtTheBeginning()
    {
        var stats = new LatencyStats(4, "test");

        for (int i = 0; i < 10; i++) stats.Record(500);

        stats.Reset();
        stats.Record(3);

        Assert.Equal(1, stats.Count);
        Assert.Equal(3, stats.Percentile(0.5), 0);
    }

    [Fact]
    public void TheMaximumSurvivesBeingOverwritten()
    {
        // The worst case is the one worth keeping even once the window has moved past it.
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

    [Fact]
    public void RecordingAllocatesNothing()
    {
        // The claim every other test here leans on, asserted rather than stated. The
        // daemon calls this up to four times per tick and the tick runs at 144 Hz
        // while anything is moving, so a single allocation per call is a gen0
        // collection every few seconds - and a collection suspends every thread in
        // the process, including the one holding a keystroke the user is waiting on.
        var stats = new LatencyStats(64, "test");

        // Warm up first: the very first call JITs the method, and tiered compilation
        // allocates on behalf of the runtime in a way that has nothing to do with
        // what Record does afterwards.
        for (int i = 0; i < 200; i++) stats.Record(i);

        long before = GC.GetAllocatedBytesForCurrentThread();

        // Past capacity on purpose, so the eviction path is measured too.
        for (int i = 0; i < 1000; i++) stats.Record(i * 1.5);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void SamplesNeedNotBeDurations()
    {
        // The type is named for latency and its first uses were all times, but the
        // daemon also records bytes allocated per tick and windows moved per frame.
        // Nothing here is time-specific, and the report formats the unit itself.
        var stats = new LatencyStats(8, "bytes");

        stats.Record(0);
        stats.Record(4096);
        stats.Record(1024);

        Assert.Equal(1024, stats.Percentile(0.5), 0);
        Assert.Equal(4096, stats.Max, 0);
        Assert.DoesNotContain(" ms", stats.ToString(), StringComparison.Ordinal);
    }
}
