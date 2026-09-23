using System.Buffers;
using System.Text;

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
/// The alignment is the best one, not the first one. The matcher used to take each
/// query character at its earliest occurrence, so <c>st</c> against
/// <c>Visual Studio</c> landed on the <c>s</c> of <c>Visual</c> and the <c>t</c> of
/// <c>Studio</c> - two scattered letters scored as a coincidence, and the row was
/// highlighted that way too - when the <c>St</c> that starts a word was there to be
/// had. Every placement is now weighed and the highest-scoring kept, which is a
/// dynamic programme over query and candidate rather than a walk.
/// </para>
/// <para>
/// Letters are compared folded: case, then the marks that distinguish <c>é</c> from
/// <c>e</c>, and the Arabic letters that are one sound spelt several ways - the three
/// hamza-carrying alifs and the bare one, <c>ة</c> and <c>ه</c>, <c>ى</c> and
/// <c>ي</c>, the Persian kaf and yeh and the Arabic ones. Somebody searching for a
/// window called <c>Café</c> types <c>cafe</c>; somebody searching for <c>أحمد</c>
/// may well type <c>احمد</c>, since the hamza is the first thing a quick typist
/// drops. Vowel marks in a candidate are stepped over, so <c>مُحَمَّد</c> is found by
/// <c>محمد</c> and a match across them still counts as adjacent.
/// </para>
/// <para>
/// A query with spaces in it is several searches that must all succeed:
/// <c>code proj</c> finds <c>My Project - Visual Studio Code</c> whichever order the
/// words come in. Within one word the rules above apply; the words' scores add.
/// </para>
/// <para>
/// Allocation-free on the hot path, or as near as makes no difference: it runs over
/// every candidate on every keystroke, and the positions are written into a span the
/// caller owns so that highlighting a match costs nothing extra. The alignment's
/// tables are stack buffers; the one that remembers the path back is rented from the
/// shared pool and only when positions are wanted.
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

    /// <summary>
    /// The longest candidate the full alignment is run over; beyond it the walk is used.
    /// </summary>
    /// <remarks>
    /// The alignment costs query length times candidate length per candidate. A
    /// window title is a line; a title this long is a browser tab that has put a whole
    /// article in it, and the earliest-occurrence walk is good enough for those.
    /// </remarks>
    public const int MaxAlignedLength = 512;

    // Tuned against the behaviour that annoys rather than against a benchmark.
    // Chosen so that a prefix beats an abbreviation, an abbreviation beats a scatter,
    // and a short candidate beats a long one when both otherwise tie.
    private const int StartOfStringBonus = 24;
    private const int WordStartBonus = 16;
    private const int CamelBoundaryBonus = 16;
    private const int AdjacentBonus = 10;
    private const int MaximumRun = 4;
    private const int BaseCharacterScore = 4;
    private const int LeadingGapPenalty = 2;
    private const int MaximumLeadingGapPenalty = 12;
    private const int GapPenalty = 1;
    private const int MaximumGap = 4;

    /// <summary>
    /// Scores <paramref name="query"/> against <paramref name="candidate"/>.
    /// </summary>
    /// <param name="query">What the user typed.</param>
    /// <param name="candidate">The text being searched.</param>
    /// <param name="positions">
    /// Receives the index in <paramref name="candidate"/> of each matched character,
    /// ascending. May be empty if the caller does not want them; matching still works.
    /// </param>
    /// <remarks>
    /// An empty query matches everything with a score of one, so that "no filter"
    /// falls out of the same path rather than being a special case at every call
    /// site. One rather than zero because zero means "did not match". Spaces around
    /// the query are not part of it: the space somebody has just typed before their
    /// next word must not empty the list until the word arrives.
    /// </remarks>
    public static MatchResult Match(
        ReadOnlySpan<char> query, ReadOnlySpan<char> candidate, Span<int> positions)
    {
        query = query.Trim(' ');

        if (query.IsEmpty) return new MatchResult(1, 0);
        if (candidate.IsEmpty) return MatchResult.None;

        return query.Contains(' ')
            ? MatchEveryWord(query, candidate, positions)
            : MatchWord(query, candidate, positions);
    }

    /// <summary>Scores a query against a candidate without recording positions.</summary>
    public static MatchResult Match(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate) =>
        Match(query, candidate, []);

    /// <summary>
    /// Every space-separated word must match; the scores add and the positions merge.
    /// </summary>
    private static MatchResult MatchEveryWord(
        ReadOnlySpan<char> query, ReadOnlySpan<char> candidate, Span<int> positions)
    {
        Span<int> wordPositions = stackalloc int[MaxPositions];

        int score = 0;
        int matched = 0;
        int written = 0;

        foreach (Range range in query.Split(' '))
        {
            ReadOnlySpan<char> word = query[range];
            if (word.IsEmpty) continue;

            MatchResult result = MatchWord(word, candidate, wordPositions);
            if (!result.IsMatch) return MatchResult.None;

            // The length bonus is a property of the candidate and is added once.
            score += result.Score - LengthBonus(candidate);
            matched += result.Matched;

            written = MergeInto(positions, written, wordPositions[..Math.Min(result.Matched, wordPositions.Length)]);
        }

        return new MatchResult(Math.Max(score + LengthBonus(candidate), 1), matched);
    }

    /// <summary>
    /// Adds positions to an ascending set, dropping duplicates. Returns the new count.
    /// </summary>
    /// <remarks>
    /// Two words may light the same letter - <c>co co</c> - and the renderer walks the
    /// positions as ascending runs, so the union is kept sorted and distinct.
    /// </remarks>
    private static int MergeInto(Span<int> into, int count, ReadOnlySpan<int> more)
    {
        foreach (int position in more)
        {
            if (count >= into.Length) break;

            int at = into[..count].BinarySearch(position);
            if (at >= 0) continue;

            at = ~at;
            into[at..count].CopyTo(into[(at + 1)..(count + 1)]);
            into[at] = position;
            count++;
        }

        return count;
    }

    private static MatchResult MatchWord(
        ReadOnlySpan<char> query, ReadOnlySpan<char> candidate, Span<int> positions)
    {
        // A vowel mark typed into the query is stepped over as one in the candidate
        // is, so the two spellings find each other whichever side the marks are on.
        foreach (char ch in query)
        {
            if (IsSignificant(ch)) continue;

            Span<char> significant = stackalloc char[query.Length];
            int count = 0;

            foreach (char keep in query)
                if (IsSignificant(keep)) significant[count++] = keep;

            return count == 0 ? new MatchResult(1, 0) : MatchWord(significant[..count], candidate, positions);
        }

        if (query.Length > candidate.Length) return MatchResult.None;

        return query.Length <= MaxPositions && candidate.Length <= MaxAlignedLength
            ? Align(query, candidate, positions)
            : Walk(query, candidate, positions);
    }

    /// <summary>
    /// The best alignment of the query over the candidate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Row <c>i</c> holds, for every candidate position <c>j</c>, the best score of
    /// any alignment that puts query character <c>i</c> at <c>j</c>, and the length of
    /// the run it arrived with - so the next row can compound an unbroken run the way
    /// the walk did. Each cell looks at the row above: the previous significant
    /// position for an adjacent continuation, the few positions before that for a
    /// short gap, and the running best of everything earlier for a long one, since the
    /// gap penalty stops growing after four. Linear per cell, so the whole is the
    /// product of the two lengths.
    /// </para>
    /// <para>
    /// A character landing on a word start pays no gap penalty for the word it
    /// skipped, and no leading penalty for the words before it: that is what an
    /// abbreviation is, and charging for it made <c>st</c> prefer two scattered
    /// letters near the front to the <c>St</c> of <c>Studio</c>.
    /// </para>
    /// </remarks>
    private static MatchResult Align(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate, Span<int> positions)
    {
        int n = query.Length;
        int m = candidate.Length;

        Span<int> previousScore = stackalloc int[m];
        Span<int> previousRun = stackalloc int[m];
        Span<int> currentScore = stackalloc int[m];
        Span<int> currentRun = stackalloc int[m];

        // The path back, only when somebody will read it.
        bool recordPath = positions.Length > 0;
        int[]? parents = recordPath ? ArrayPool<int>.Shared.Rent(n * m) : null;

        try
        {
            const int Unreachable = int.MinValue / 2;

            for (int i = 0; i < n; i++)
            {
                char wanted = Fold(query[i]);
                bool any = false;

                // Running best of the row above over everything far enough back to
                // pay the full gap penalty: the positions before the ones the inner
                // loop below looks at one by one.
                int farBest = Unreachable;
                int farBestAt = -1;
                int farCount = 0;

                for (int j = 0; j < m; j++)
                {
                    currentScore[j] = Unreachable;
                    currentRun[j] = 0;

                    if (!IsSignificant(candidate[j]) || Fold(candidate[j]) != wanted) continue;

                    int previous = PreviousSignificant(candidate, j);
                    int placement = PlacementBonus(candidate, j, previous);
                    bool atBoundary = placement > 0;

                    if (i == 0)
                    {
                        // Distance from the start before the first match lands.
                        // Bounded, because a match deep inside a long title is worse
                        // than one near the front but should not be ruled out by
                        // length alone; and waived at a word start, which is where an
                        // abbreviation's first letter is meant to land.
                        int leading = atBoundary ? 0 : Math.Min(j * LeadingGapPenalty, MaximumLeadingGapPenalty);

                        currentScore[j] = BaseCharacterScore + placement - leading;
                        any = true;

                        if (parents is not null) parents[j] = -1;
                        continue;
                    }

                    // Everything before j - MaximumGap - 1 pays the same penalty, so
                    // the best of it is enough.
                    for (; farCount < j - MaximumGap - 1; farCount++)
                    {
                        if (previousScore[farCount] > farBest)
                        {
                            farBest = previousScore[farCount];
                            farBestAt = farCount;
                        }
                    }

                    int best = Unreachable;
                    int bestFrom = -1;
                    int bestRun = 0;

                    if (farBestAt >= 0)
                    {
                        best = farBest + BaseCharacterScore + placement - (atBoundary ? 0 : MaximumGap * GapPenalty);
                        bestFrom = farBestAt;
                    }

                    for (int k = Math.Max(0, j - MaximumGap - 1); k < j; k++)
                    {
                        if (previousScore[k] <= Unreachable) continue;

                        int score;
                        int run;

                        if (k == previous)
                        {
                            // Compounding, not flat. A flat bonus let four scattered
                            // word-starts outscore four contiguous characters, so
                            // "disc" preferred "D. I. Smith Consulting" to "Discord" -
                            // the exact inversion this matcher exists to avoid.
                            run = previousRun[k] + 1;
                            score = previousScore[k] + BaseCharacterScore + placement + (AdjacentBonus * Math.Min(run, MaximumRun));
                        }
                        else
                        {
                            // A gap between matches is weak evidence, but not
                            // disqualifying - it is what distinguishes a genuine
                            // abbreviation from a prefix.
                            run = 0;
                            score = previousScore[k] + BaseCharacterScore + placement -
                                (atBoundary ? 0 : Math.Min(j - k - 1, MaximumGap) * GapPenalty);
                        }

                        // Ties go to the later start, which is the adjacent one.
                        if (score >= best)
                        {
                            best = score;
                            bestFrom = k;
                            bestRun = run;
                        }
                    }

                    if (bestFrom < 0) continue;

                    currentScore[j] = best;
                    currentRun[j] = bestRun;
                    any = true;

                    if (parents is not null) parents[(i * m) + j] = bestFrom;
                }

                if (!any) return MatchResult.None;

                Span<int> swap = previousScore;
                previousScore = currentScore;
                currentScore = swap;

                swap = previousRun;
                previousRun = currentRun;
                currentRun = swap;
            }

            int end = IndexOfMax(previousScore);
            int total = previousScore[end] + LengthBonus(candidate);

            if (parents is not null)
            {
                // Back along the path, then out in order.
                Span<int> path = stackalloc int[n];
                int at = end;

                for (int i = n - 1; i >= 0; i--)
                {
                    path[i] = at;
                    at = parents[(i * m) + at];
                }

                path[..Math.Min(n, positions.Length)].CopyTo(positions);
            }

            return new MatchResult(Math.Max(total, 1), n);
        }
        finally
        {
            if (parents is not null) ArrayPool<int>.Shared.Return(parents);
        }
    }

    /// <summary>
    /// The earliest-occurrence walk, for inputs too long to align.
    /// </summary>
    private static MatchResult Walk(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate, Span<int> positions)
    {
        int score = 0;
        int matched = 0;
        int at = 0;
        int previousMatch = -1;
        int run = 0;

        foreach (char wanted in query)
        {
            int found = IndexOfFolded(candidate, wanted, at);
            if (found < 0) return MatchResult.None;

            int previous = PreviousSignificant(candidate, found);
            int placement = PlacementBonus(candidate, found, previous);

            score += BaseCharacterScore + placement;

            if (previousMatch >= 0 && previous == previousMatch)
            {
                run++;
                score += AdjacentBonus * Math.Min(run, MaximumRun);
            }
            else if (previousMatch >= 0)
            {
                run = 0;
                if (placement == 0) score -= Math.Min(found - previousMatch - 1, MaximumGap) * GapPenalty;
            }
            else if (placement == 0)
            {
                score -= Math.Min(found * LeadingGapPenalty, MaximumLeadingGapPenalty);
            }

            if (matched < positions.Length) positions[matched] = found;

            matched++;
            previousMatch = found;
            at = found + 1;
        }

        return new MatchResult(Math.Max(score + LengthBonus(candidate), 1), matched);
    }

    /// <summary>
    /// A shorter candidate containing the same match is the better answer: "Code"
    /// beats "Visual Studio Code Insiders Preview" for the query "code".
    /// </summary>
    private static int LengthBonus(ReadOnlySpan<char> candidate) => Math.Max(0, 16 - (candidate.Length / 8));

    /// <summary>What landing at <paramref name="j"/> is worth for where it is.</summary>
    private static int PlacementBonus(ReadOnlySpan<char> candidate, int j, int previous)
    {
        if (previous < 0) return StartOfStringBonus;
        if (IsSeparator(candidate[previous])) return WordStartBonus;

        // The other half of how abbreviations are formed. Typing "vsc" should find
        // "VisualStudioCode" even though it has no separators in it.
        if (char.IsLower(candidate[previous]) && char.IsUpper(candidate[j])) return CamelBoundaryBonus;

        return 0;
    }

    /// <summary>The index of the significant character before <paramref name="j"/>, or -1.</summary>
    private static int PreviousSignificant(ReadOnlySpan<char> candidate, int j)
    {
        for (int k = j - 1; k >= 0; k--)
            if (IsSignificant(candidate[k])) return k;

        return -1;
    }

    private static int IndexOfMax(ReadOnlySpan<int> values)
    {
        int best = 0;

        for (int i = 1; i < values.Length; i++)
            if (values[i] > values[best]) best = i;

        return best;
    }

    /// <summary>
    /// Whether a character ends a word for the purposes of a start-of-word bonus.
    /// </summary>
    /// <remarks>
    /// Path separators and punctuation are included because so much of what gets
    /// searched here is a path, a class name or a window title with a document name
    /// and an application name joined by a dash.
    /// </remarks>
    private static bool IsSeparator(char value) =>
        value is ' ' or '-' or '_' or '.' or '/' or '\\' or ':' or '[' or '(' or '\t' or '\u2013' or '\u2014' or '|';

    /// <summary>
    /// Whether a character is one the query can land on: everything but a combining
    /// mark, which is stepped over so <c>محمد</c> finds <c>مُحَمَّد</c>.
    /// </summary>
    private static bool IsSignificant(char value) =>
        value is not (>= '\u0300' and <= '\u036F') and not (>= '\u064B' and <= '\u065F') and not '\u0670';

    private static int IndexOfFolded(ReadOnlySpan<char> text, char wanted, int from)
    {
        char folded = Fold(wanted);

        for (int i = from; i < text.Length; i++)
            if (IsSignificant(text[i]) && Fold(text[i]) == folded)
                return i;

        return -1;
    }

    // ---- folding ------------------------------------------------------------------

    /// <summary>
    /// The Latin range whose letters decompose to a base letter and a mark, folded once
    /// at startup by the runtime's own decomposition rather than by a table typed in
    /// by hand.
    /// </summary>
    private static readonly char[] s_latinFold = BuildLatinFold();

    private const int LatinFoldEnd = 0x0250;

    private static char[] BuildLatinFold()
    {
        char[] table = new char[LatinFoldEnd];

        for (int c = 0; c < LatinFoldEnd; c++)
        {
            char lower = char.ToLowerInvariant((char)c);
            table[c] = lower;

            if (c < 0x00C0) continue;

            // The first character of the canonical decomposition is the base letter
            // for every precomposed Latin letter in this range; ligatures and letters
            // with no decomposition - ø, ł, ß - stay as they are.
            string decomposed = lower.ToString().Normalize(NormalizationForm.FormD);

            if (decomposed.Length > 1 && decomposed[0] < 0x00C0)
                table[c] = decomposed[0];
        }

        return table;
    }

    /// <summary>
    /// The character as compared: lower-cased, unaccented, and with the Arabic
    /// spellings of one sound made one.
    /// </summary>
    public static char Fold(char value)
    {
        if (value < LatinFoldEnd) return s_latinFold[value];

        return value switch
        {
            // The alifs: with hamza above, below, madda, and the wasla form.
            '\u0623' or '\u0625' or '\u0622' or '\u0671' => '\u0627',

            // Ta marbuta reads as ha at the end of a word, and is typed as either.
            '\u0629' => '\u0647',

            // Alif maqsura and the Persian yeh are both typed for ya.
            '\u0649' or '\u06CC' => '\u064A',

            // The Persian kaf for the Arabic one.
            '\u06A9' => '\u0643',

            // Ligature-shaped keyboards put this in where two letters were meant.
            '\uFEFB' => '\u0644',

            _ => char.ToLowerInvariant(value),
        };
    }
}
