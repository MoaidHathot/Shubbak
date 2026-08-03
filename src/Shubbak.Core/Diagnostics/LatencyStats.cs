namespace Shubbak.Core.Diagnostics;

/// <summary>
/// A fixed-capacity sample set that reports percentiles.
/// </summary>
/// <remarks>
/// <para>
/// Lifted from the design spike, where it produced the numbers ADR 0001 rests on -
/// hook latency, frame time, wake jitter - and then stayed there. The properties were
/// proved once, at design time, and the shipping binary had no way to notice them
/// regressing. Reasoning about performance from the source is exactly what that
/// absence forces, and reasoning is not measurement.
/// </para>
/// <para>
/// Recording is allocation-free and is a bounds check, a store and an increment, so
/// it is safe on the tick. Samples past capacity are dropped rather than growing the
/// array or evicting: a window of recent behaviour is what a diagnostic report wants,
/// and dropping is the only option that cannot itself perturb the thing being
/// measured.
/// </para>
/// </remarks>
public sealed class LatencyStats
{
    private readonly double[] _samples;
    private int _count;
    private long _total;

    public LatencyStats(int capacity, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _samples = new double[capacity];
        Name = name;
    }

    public string Name { get; }

    /// <summary>How many samples are held.</summary>
    public int Count => _count;

    /// <summary>How many were offered, including those dropped once full.</summary>
    public long Offered => Interlocked.Read(ref _total);

    public double Max { get; private set; }

    /// <summary>Records one sample. Allocation-free.</summary>
    public void Record(double milliseconds)
    {
        Interlocked.Increment(ref _total);

        if (milliseconds > Max) Max = milliseconds;

        int i = _count;

        if ((uint)i < (uint)_samples.Length)
        {
            _samples[i] = milliseconds;
            _count = i + 1;
        }
    }

    /// <summary>Empties the set, keeping the running totals.</summary>
    public void Reset() => _count = 0;

    /// <summary>
    /// The value below which the given fraction of samples fall.
    /// </summary>
    /// <param name="fraction">Between 0 and 1, so 0.99 is the 99th percentile.</param>
    /// <remarks>
    /// Nearest-rank, so the p50 of one to a hundred is fifty rather than fifty-one.
    /// Sorts a copy, because reporting must not disturb the order the samples were
    /// recorded in - a report can be asked for at any moment, including while the
    /// thing being measured is still running.
    /// </remarks>
    public double Percentile(double fraction)
    {
        if (_count == 0) return 0;

        double[] sorted = new double[_count];
        Array.Copy(_samples, sorted, _count);
        Array.Sort(sorted);

        int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;

        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>How many samples exceeded a budget.</summary>
    public int CountOver(double milliseconds)
    {
        int over = 0;

        for (int i = 0; i < _count; i++)
            if (_samples[i] > milliseconds) over++;

        return over;
    }

    public override string ToString() =>
        _count == 0
            ? $"{Name}: no samples"
            : $"{Name}: p50 {Percentile(0.5):F2} ms, p99 {Percentile(0.99):F2} ms, " +
              $"max {Max:F2} ms, {_count} sample(s)";
}
