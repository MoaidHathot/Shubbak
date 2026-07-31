namespace Shubbak.Spike;

/// <summary>
/// Collects latency samples and reports percentiles. Samples are stored into a
/// pre-allocated array so recording never allocates; sorting happens only at
/// report time, off the hot path.
/// </summary>
internal sealed class LatencyStats
{
    private readonly double[] _samples;
    private int _count;

    public LatencyStats(int capacity) => _samples = new double[capacity];

    public string Name { get; init; } = "";

    /// <summary>Allocation-free. Silently drops samples past capacity.</summary>
    public void Record(double milliseconds)
    {
        int i = _count;
        if ((uint)i < (uint)_samples.Length)
        {
            _samples[i] = milliseconds;
            _count = i + 1;
        }
    }

    public int Count => _count;

    public Report Compute()
    {
        if (_count == 0) return new Report();

        var sorted = new double[_count];
        Array.Copy(_samples, sorted, _count);
        Array.Sort(sorted);

        double sum = 0;
        for (int i = 0; i < sorted.Length; i++) sum += sorted[i];

        return new Report
        {
            Count = _count,
            Min = sorted[0],
            Max = sorted[^1],
            Mean = sum / _count,
            P50 = Percentile(sorted, 0.50),
            P90 = Percentile(sorted, 0.90),
            P99 = Percentile(sorted, 0.99),
            P999 = Percentile(sorted, 0.999),
            P9999 = Percentile(sorted, 0.9999),
        };
    }

    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 1) return sorted[0];
        double pos = q * (sorted.Length - 1);
        int lo = (int)pos;
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        double frac = pos - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }

    /// <summary>Count of samples at or above a threshold - used for pass/fail gates.</summary>
    public int CountAtOrAbove(double thresholdMs)
    {
        int n = 0;
        for (int i = 0; i < _count; i++) if (_samples[i] >= thresholdMs) n++;
        return n;
    }

    public struct Report
    {
        public int Count;
        public double Min, Max, Mean, P50, P90, P99, P999, P9999;

        public override string ToString() =>
            $"n={Count,-9} min={Min,7:F4} p50={P50,7:F4} p90={P90,7:F4} " +
            $"p99={P99,7:F4} p99.9={P999,7:F4} p99.99={P9999,7:F4} max={Max,8:F4}  (ms)";
    }
}
