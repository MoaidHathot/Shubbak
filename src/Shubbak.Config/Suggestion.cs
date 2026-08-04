namespace Shubbak.Config;

/// <summary>
/// Finds the closest match to something a user probably misspelled.
/// </summary>
/// <remarks>
/// <para>
/// A configuration is written by hand, so most of what is wrong with one is a typo.
/// Naming the thing that was probably meant turns "unknown section 'genral'" into a
/// fix rather than a search.
/// </para>
/// <para>
/// Lifted out of the command parser, which had this to itself. The config loader
/// needed the same thing for section and setting names, and a second copy of an
/// edit-distance routine is a second place for it to be subtly different.
/// </para>
/// </remarks>
public static class Suggestion
{
    /// <summary>
    /// The candidate closest to <paramref name="word"/>, if one is close enough.
    /// </summary>
    /// <remarks>
    /// The threshold scales with length, so a short word tolerates one mistake and a
    /// long one several - and nothing is suggested when the nearest candidate is not
    /// plausibly what was meant, because a confident wrong guess is worse than none.
    /// </remarks>
    public static string? Closest(string word, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (string.IsNullOrEmpty(word)) return null;

        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in candidates)
        {
            int distance = Levenshtein(word, candidate);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        int allowed = Math.Max(2, word.Length / 3);

        return bestDistance <= allowed ? best : null;
    }

    /// <summary>Edit distance between two words.</summary>
    public static int Levenshtein(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        Span<int> previous = new int[b.Length + 1];
        Span<int> current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            current.CopyTo(previous);
        }

        return previous[b.Length];
    }
}
