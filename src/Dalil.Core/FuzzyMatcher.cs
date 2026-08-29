namespace Dalil.Core;

/// <summary>
/// How well a candidate matched, and where.
/// </summary>
/// <param name="Score">Higher is better. Zero means no match at all.</param>
/// <param name="Matched">
/// How many query characters were matched, and therefore how many positions were
/// written into the caller's span.
/// </param>
public readonly record struct MatchResult(int Score, int Matched)
{
    /// <summary>Whether the candidate matched at all.</summary>
    public bool IsMatch => Matched > 0 || Score > 0;

    public static MatchResult None => default;
}

/// <summary>
/// Subsequence matching with the bonuses that make a palette feel right.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not Levenshtein, which <c>Shubbak.Config.Suggestion</c> uses for a
/// different job. Edit distance answers "did you misspell this whole word", which is
/// the right question for a config typo and the wrong one here: typing <c>dsc</c> is
/// not a misspelling of <c>Discord</c>, it is an abbreviation of it, and edit
/// distance scores it as almost maximally wrong.
/// </para>
/// <para>
/// So: every query character must appear in order, and the score comes from
/// <em>where</em> they appear. Characters at the start of a word, straight after a
/// separator, or at a camel-case boundary are worth far more than characters in the
/// middle of one, because that is what an abbreviation is made of. Runs of adjacent
/// characters are worth more than the same characters scattered, because a prefix is
/// a better match than a coincidence.
/// </para>
/// <para>
/// Allocation-free by construction. It runs over every candidate on every keystroke,
/// and the positions are written into a span the caller owns so that highlighting a
/// match costs nothing extra.
/// </para>
/// </remarks>
public static class FuzzyMatcher
{
    /// <summary>
    /// How many matched positions a caller should make room for.
    /// </summary>
    /// <remarks>
    /// A query longer than this still matches and still scores; only the highlighting
    /// stops being recorded past the limit, because <see cref="Match(ReadOnlySpan{char}, ReadOnlySpan{char}, Span{int})"/>
    /// writes a position only while there is somewhere to put it. Callers slicing the
    /// span by <see cref="MatchResult.Matched"/> must clamp to the span they supplied -
    /// the count is how many characters matched, not how many were written down.
    /// <para>
    /// Sized for a search box rather than for a document. Nobody types sixty-four
    /// characters to find a window, and a stack buffer large enough for somebody who
    /// pasted a paragraph would be paid for on every candidate on every keystroke.
    /// </para>
    /// </remarks>
    public const int MaxPositions = 64;

    // Tuned against the behaviour that annoys rather than against a benchmark.
    // Chosen so that a prefix beats an abbreviation, an abbreviation beats a scatter,
    // and a short candidate beats a long one when both otherwise tie.
    private const int StartOfStringBonus = 24;
    private const int WordStartBonus = 16;
    private const int CamelBoundaryBonus = 12;
    private const int AdjacentBonus = 10;
    private const int MaximumRun = 4;
    private const int BaseCharacterScore = 4;
    private const int LeadingGapPenalty = 2;
    private const int MaximumLeadingGapPenalty = 12;
    private const int GapPenalty = 1;

    /// <summary>
    /// Scores <paramref name="query"/> against <paramref name="candidate"/>.
    /// </summary>
    /// <param name="query">What the user typed.</param>
    /// <param name="candidate">The text being searched.</param>
    /// <param name="positions">
    /// Receives the index in <paramref name="candidate"/> of each matched character.
    /// May be empty if the caller does not want them; matching still works.
    /// </param>
    /// <remarks>
    /// An empty query matches everything with a score of one, so that "no filter"
    /// falls out of the same path rather than being a special case at every call
    /// site. One rather than zero because zero means "did not match".
    /// </remarks>
    public static MatchResult Match(
        ReadOnlySpan<char> query, ReadOnlySpan<char> candidate, Span<int> positions)
    {
        if (query.IsEmpty) return new MatchResult(1, 0);
        if (candidate.IsEmpty || query.Length > candidate.Length) return MatchResult.None;

        int score = 0;
        int matched = 0;
        int at = 0;
        int previousMatch = -1;
        int run = 0;

        foreach (char wanted in query)
        {
            int found = IndexOfIgnoringCase(candidate, wanted, at);
            if (found < 0) return MatchResult.None;

            score += BaseCharacterScore;

            if (found == 0)
            {
                score += StartOfStringBonus;
            }
            else if (IsSeparator(candidate[found - 1]))
            {
                score += WordStartBonus;
            }
            else if (char.IsLower(candidate[found - 1]) && char.IsUpper(candidate[found]))
            {
                // The other half of how abbreviations are formed. Typing "vsc" should
                // find "VisualStudioCode" even though it has no separators in it.
                score += CamelBoundaryBonus;
            }

            if (found == previousMatch + 1 && previousMatch >= 0)
            {
                // Compounding, not flat. A flat bonus let four scattered word-starts
                // outscore four contiguous characters, so "disc" preferred
                // "D. I. Smith Consulting" to "Discord" - the exact inversion this
                // matcher exists to avoid. Each further character of an unbroken run
                // is worth more than the last, so a prefix pulls away from an
                // abbreviation rather than merely keeping pace with it.
                run++;
                score += AdjacentBonus * Math.Min(run, MaximumRun);
            }
            else if (previousMatch >= 0)
            {
                run = 0;

                // A gap between matches is weak evidence, but not disqualifying - it
                // is what distinguishes a genuine abbreviation from a prefix.
                score -= Math.Min(found - previousMatch - 1, 4) * GapPenalty;
            }
            else
            {
                // Distance from the start before the first match lands. Bounded,
                // because a match deep inside a long title is worse than one near the
                // front but should not be ruled out by length alone.
                score -= Math.Min(found * LeadingGapPenalty, MaximumLeadingGapPenalty);
            }

            if (matched < positions.Length) positions[matched] = found;

            matched++;
            previousMatch = found;
            at = found + 1;
        }

        // A shorter candidate containing the same match is the better answer: "Code"
        // beats "Visual Studio Code Insiders Preview" for the query "code".
        score += Math.Max(0, 16 - (candidate.Length / 8));

        return new MatchResult(Math.Max(score, 1), matched);
    }

    /// <summary>Scores a query against a candidate without recording positions.</summary>
    public static MatchResult Match(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate) =>
        Match(query, candidate, []);

    /// <summary>
    /// Whether a character ends a word for the purposes of a start-of-word bonus.
    /// </summary>
    /// <remarks>
    /// Path separators and punctuation are included because so much of what gets
    /// searched here is a path, a class name or a window title with a document name
    /// and an application name joined by a dash.
    /// </remarks>
    private static bool IsSeparator(char value) =>
        value is ' ' or '-' or '_' or '.' or '/' or '\\' or ':' or '[' or '(' or '\t';

    private static int IndexOfIgnoringCase(ReadOnlySpan<char> text, char wanted, int from)
    {
        char lower = char.ToLowerInvariant(wanted);

        for (int i = from; i < text.Length; i++)
            if (char.ToLowerInvariant(text[i]) == lower)
                return i;

        return -1;
    }
}
