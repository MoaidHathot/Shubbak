using System.Text;

namespace Taj.Core.Sources;

/// <summary>
/// The last so many readings of another source, as a list of numbers.
/// </summary>
/// <remarks>
/// <para>
/// A sparkline is a graph of a value over time, and the question is who keeps the
/// time. Not the widget: it is rebuilt from a snapshot of values on every change to
/// any of them, and a snapshot has no way of saying whether the value it carries is
/// a new reading or the one from before - and <see cref="SourceBase.Publish"/> drops
/// a reading equal to its predecessor on purpose, so a value that held steady would
/// never reach the widget at all. A graph kept there would stop moving exactly when
/// the thing it graphs went flat.
/// </para>
/// <para>
/// So the history is kept beside the source, from <see cref="SourceBase.Sampled"/>,
/// which fires for every reading taken including the repeats. It is a source in its
/// own right, named <c>&lt;source&gt;.history</c>, whose value is the readings
/// oldest first separated by spaces - <c>12 15 50 50 48</c> - so a template may show
/// it, a <c>when</c> may test it, and a sparkline draws it without knowing where the
/// list came from. A script that already has a history prints the same shape itself
/// and skips this.
/// </para>
/// <para>
/// Readings that hold no number are skipped rather than recorded as gaps: a script
/// that printed a heading has not measured anything. Publishing goes through the
/// same equality check every source has, so a full buffer of identical readings
/// repaints nothing - which is right, since nothing on screen would move.
/// </para>
/// </remarks>
public sealed class HistorySource : SourceBase
{
    /// <summary>What is appended to a source's name to name its history.</summary>
    public const string Suffix = ".history";

    private readonly SourceBase _inner;
    private readonly double[] _readings;
    private readonly Lock _gate = new();

    /// <summary>
    /// Where the value is written on each sample. Reused under the lock: a sample is a
    /// source tick, and the list it writes is the same length every time once the
    /// buffer is full, so the builder settles at that size and allocates nothing more.
    /// </summary>
    private readonly StringBuilder _text = new();

    private int _count;
    private int _next;

    /// <param name="inner">The source whose readings are kept.</param>
    /// <param name="capacity">How many readings to keep; older ones fall off the front.</param>
    public HistorySource(SourceBase inner, int capacity)
        : base(NameFor((inner ?? throw new ArgumentNullException(nameof(inner))).Name))
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);

        _inner = inner;
        _readings = new double[capacity];

        // Subscribed at birth rather than at Start: the hub starts sources in the order
        // it was given them, and the inner one takes its first reading the moment it
        // starts, which a history that was not yet listening would have missed.
        _inner.Sampled += OnSampled;
    }

    /// <summary>The name a source's history goes by.</summary>
    public static string NameFor(string sourceName) => sourceName + Suffix;

    /// <summary>How many readings are kept.</summary>
    public int Capacity => _readings.Length;

    /// <summary>The readings so far, oldest first.</summary>
    public IReadOnlyList<double> Readings
    {
        get
        {
            lock (_gate) return Snapshot();
        }
    }

    /// <summary>Nothing to start: the readings arrive from the source this listens to.</summary>
    public override void Start() { }

    private void OnSampled(ISource _, string? value)
    {
        if (!Numbers.TryParseFirst(value, out double reading)) return;

        string encoded;

        lock (_gate)
        {
            _readings[_next] = reading;
            _next = (_next + 1) % _readings.Length;
            if (_count < _readings.Length) _count++;

            // Straight from the ring into the builder: the one string the value has
            // to be is the one allocation this makes.
            _text.Clear();
            int start = _count < _readings.Length ? 0 : _next;
            Span<char> digits = stackalloc char[32];

            for (int i = 0; i < _count; i++)
            {
                if (i > 0) _text.Append(' ');

                if (Numbers.TryFormat(_readings[(start + i) % _readings.Length], digits, out int written))
                    _text.Append(digits[..written]);
            }

            encoded = _text.ToString();
        }

        Publish(encoded);
    }

    /// <summary>The readings as the source's value: oldest first, a space between.</summary>
    public static string Encode(IReadOnlyList<double> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var text = new StringBuilder(readings.Count * 4);
        Span<char> digits = stackalloc char[32];

        for (int i = 0; i < readings.Count; i++)
        {
            if (i > 0) text.Append(' ');

            if (Numbers.TryFormat(readings[i], digits, out int written)) text.Append(digits[..written]);
        }

        return text.ToString();
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    private double[] Snapshot()
    {
        var snapshot = new double[_count];
        int start = _count < _readings.Length ? 0 : _next;

        for (int i = 0; i < _count; i++)
            snapshot[i] = _readings[(start + i) % _readings.Length];

        return snapshot;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Sampled -= OnSampled;
        base.Dispose(disposing);
    }
}
