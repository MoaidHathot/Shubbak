using Shubbak.Core.Diagnostics;

namespace Shubbak.Core.Tests;

public sealed class PercentileAllocationTests
{
    [Fact]
    public void RepeatedReportsReuseScratchWithoutChangingTheRollingSamples()
    {
        var stats = new LatencyStats(4096, "probe");
        for (int i = 0; i < 4096; i++) stats.Record(i);
        for (int i = 0; i < 100; i++) { stats.Percentile(.5); stats.Percentile(.99); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        double total = 0;
        for (int i = 0; i < 100; i++) total += stats.Percentile(.5) + stats.Percentile(.99);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(610200, total);
        stats.Record(8192);
        Assert.Equal(2048, stats.Percentile(.5));
        Assert.Equal(8192, stats.Max);
    }
}
