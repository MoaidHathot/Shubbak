using System.Globalization;

namespace Dalil.Core;

/// <summary>
/// What has been run from the palette, weighted towards lately: the ranking that
/// puts the command you use every day above the hundred you never touch.
/// </summary>
/// <remarks>
/// <para>
/// The command list is alphabetical below the user's own actions, and alphabetical
/// is the order that helps nobody: <c>wm-suspend</c> is next to <c>wm-resume</c>, and
/// the one thing somebody runs ten times a day is wherever its first letter falls.
/// Every launcher worth using learns, and this is the smallest learning that works:
/// a count per command that halves with age, so what was run a lot a month ago gives
/// way to what was run a little this week.
/// </para>
/// <para>
/// Applied as rank rather than as score. Rank breaks ties, and an empty query ties
/// everything, so the order of the list before typing starts is the frecency order;
/// once a letter is typed, the match decides and frecency only settles what matches
/// equally well. A command somebody has never run is never demoted below where it
/// was - the unused keep their alphabet.
/// </para>
/// <para>
/// One line per command in a file the host owns, written whole on each change. The
/// format is a tab-separated triple so it can be read and, if it comes to that,
/// edited or deleted by hand.
/// </para>
/// </remarks>
public sealed class Frecency
{
    /// <summary>How long a use takes to count for half of what it did.</summary>
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(14);

    /// <summary>Ranks below this belong to entries that have never been run.</summary>
    /// <remarks>
    /// Comfortably above the layering the entries themselves carry - macros at ten,
    /// the palette's own verbs to twelve - so anything used sits above everything
    /// unused, whatever kind it is.
    /// </remarks>
    public const long UsedRankFloor = 1_000;

    /// <summary>The most entries kept, so a year of typos does not grow the file for ever.</summary>
    public const int Capacity = 200;

    private readonly Dictionary<string, (double Weight, long LastUsedTicks)> _uses = new(StringComparer.Ordinal);

    /// <summary>The keys recorded, for tests and for the file.</summary>
    public IReadOnlyCollection<string> Keys => _uses.Keys;

    /// <summary>Notes that <paramref name="key"/> was run at <paramref name="now"/>.</summary>
    public void Record(string key, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        double weight = _uses.TryGetValue(key, out (double Weight, long LastUsedTicks) had)
            ? Decayed(had.Weight, had.LastUsedTicks, now) + 1
            : 1;

        _uses[key] = (weight, now.UtcTicks);

        if (_uses.Count > Capacity) Trim(now);
    }

    /// <summary>
    /// The weight of <paramref name="key"/> now: its uses, each halved per half-life
    /// since. Zero for something never run.
    /// </summary>
    public double WeightOf(string key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _uses.TryGetValue(key, out (double Weight, long LastUsedTicks) had)
            ? Decayed(had.Weight, had.LastUsedTicks, now)
            : 0;
    }

    /// <summary>
    /// The entries with the ones that have been run lifted above the rest, most used
    /// first, everything else exactly as it was.
    /// </summary>
    /// <remarks>
    /// The key is the entry's primary text, which for a command is its verb and for a
    /// macro its name - what the user sees and would say they ran.
    /// </remarks>
    public IReadOnlyList<PaletteEntry> Applied(IReadOnlyList<PaletteEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (_uses.Count == 0) return entries;

        List<PaletteEntry> result = new(entries.Count);

        foreach (PaletteEntry entry in entries)
        {
            double weight = WeightOf(entry.Primary, now);

            // Below a hundredth of a use it has effectively been forgotten, and the
            // entry goes back to where the alphabet puts it.
            result.Add(weight < 0.01 || entry.Unavailable
                ? entry
                : entry with { Rank = UsedRankFloor + (long)Math.Round(weight * 100) });
        }

        return result;
    }

    private static double Decayed(double weight, long lastUsedTicks, DateTimeOffset now)
    {
        double ages = Math.Max(0, now.UtcTicks - lastUsedTicks) / (double)HalfLife.Ticks;
        return weight * Math.Pow(0.5, ages);
    }

    /// <summary>Drops the lightest until the store fits.</summary>
    private void Trim(DateTimeOffset now)
    {
        foreach (string key in _uses
                     .OrderBy(pair => Decayed(pair.Value.Weight, pair.Value.LastUsedTicks, now))
                     .Take(_uses.Count - Capacity)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _uses.Remove(key);
        }
    }

    // ---- the file -------------------------------------------------------------------

    /// <summary>One line per entry: weight, last-used ticks, key.</summary>
    public string Serialise()
    {
        var sb = new System.Text.StringBuilder();

        foreach ((string key, (double weight, long ticks)) in _uses.OrderByDescending(p => p.Value.Weight))
        {
            sb.Append(weight.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
              .Append(ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(key.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads what <see cref="Serialise"/> wrote. A line that does not parse is skipped,
    /// so a file edited by hand or cut short by a crash loses a line and not the lot.
    /// </summary>
    public static Frecency Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var store = new Frecency();

        foreach (string line in text.Split('\n'))
        {
            string[] fields = line.TrimEnd('\r').Split('\t', 3);

            if (fields.Length != 3 || fields[2].Length == 0) continue;
            if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double weight)) continue;
            if (!long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)) continue;
            if (weight <= 0 || double.IsNaN(weight) || double.IsInfinity(weight)) continue;

            store._uses[fields[2]] = (weight, ticks);
        }

        return store;
    }
}
