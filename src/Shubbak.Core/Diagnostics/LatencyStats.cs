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
/// The name says latency because that is what it was first used for, but nothing here
/// is time-specific: samples are plain doubles and the unit is the caller's business.
/// The daemon also uses it for bytes allocated per tick and windows moved per frame.
/// </para>
/// <para>
/// Recording is allocation-free and is a bounds check, a store and an increment, so
/// it is safe on the tick. The set holds the most recent <c>capacity</c> samples,
/// overwriting the oldest, because a window of <i>recent</i> behaviour is what a
/// diagnostic report wants.
/// </para>
/// <para>
/// It did not always do that. Samples past capacity were dropped outright, so the
/// percentiles described the first few thousand ticks and then never moved again -
/// which for a window manager means they described startup, when the daemon is
/// adopting windows and laying out for the first time, and nothing afterwards. A
/// report pulled from a daemon that had been up seventy minutes and run a hundred
/// thousand ticks was still quoting the first four thousand of them.
/// </para>
/// </remarks>
public sealed class LatencyStats
{
    private readonly double[] _samples;
    private int _count;
    private int _next;
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

    /// <summary>How many were offered, including those since overwritten.</summary>
    public long Offered => Interlocked.Read(ref _total);

    /// <summary>
    /// The largest sample ever offered.
    /// </summary>
    /// <remarks>
    /// All-time, unlike the percentiles, which describe only the retained window. The
    /// worst pause the loop ever took is worth keeping long after the samples around
    /// it have been overwritten - it is usually the only trace of a stall.
    /// </remarks>
    public double Max { get; private set; }

    /// <summary>Records one sample, overwriting the oldest once full. Allocation-free.</summary>
    /// <param name="value">
    /// In whatever unit the caller is measuring. The name carries no unit because the
    /// set is also used for byte counts and window counts, where "milliseconds" made
    /// the call sites read as though they were recording a duration.
    /// </param>
    public void Record(double value)
    {
        Interlocked.Increment(ref _total);

        if (value > Max) Max = value;

        int i = _next;
        _samples[i] = value;

        // Wrapped with a comparison rather than a modulo so capacity need not be a
        // power of two, and so the tick pays a predictable branch instead of a divide.
        _next = i + 1 == _samples.Length ? 0 : i + 1;

        if (_count < _samples.Length) _count++;
    }

    /// <summary>Empties the set, keeping the running totals.</summary>
    public void Reset()
    {
        _count = 0;
        _next = 0;
    }

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
    public int CountOver(double budget)
    {
        int over = 0;

        for (int i = 0; i < _count; i++)
            if (_samples[i] > budget) over++;

        return over;
    }

    public override string ToString() =>
        _count == 0
            ? $"{Name}: no samples"
            : $"{Name}: p50 {Percentile(0.5):F2}, p99 {Percentile(0.99):F2}, " +
              $"max {Max:F2}, {_count} sample(s)";
}
