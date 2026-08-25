using Dalil.Core;

namespace Dalil.Core.Tests;

/// <summary>
/// The matcher behind the palette's filter box.
/// </summary>
/// <remarks>
/// <para>
/// These are behaviour tests, not scoring tests. Absolute scores are tuning and will
/// move; what must not move is the <em>ordering</em>, because ordering is the whole
/// user experience - a palette that finds the right window and puts it third is a
/// palette nobody uses.
/// </para>
/// <para>
/// So almost every case here asserts "this ranks above that", which stays true
/// through retuning and fails loudly if the model is ever changed for a worse one.
/// </para>
/// </remarks>
public sealed class FuzzyMatcherTests
{
    private static int Score(string query, string candidate) =>
        FuzzyMatcher.Match(query, candidate).Score;

    private static bool Matches(string query, string candidate) =>
        FuzzyMatcher.Match(query, candidate).IsMatch;

    // ---- what counts as a match --------------------------------------------

    [Fact]
    public void AnEmptyQueryMatchesEverything()
    {
        // "No filter" falls out of the same path rather than being special-cased at
        // every call site.
        Assert.True(Matches("", "anything at all"));
        Assert.True(Matches("", ""));
    }

    [Fact]
    public void CharactersMustAppearInOrder()
    {
        Assert.True(Matches("abc", "a b c"));
        Assert.False(Matches("cba", "a b c"));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.True(Matches("DISCORD", "discord"));
        Assert.True(Matches("discord", "DISCORD"));
    }

    [Fact]
    public void AQueryLongerThanTheCandidateCannotMatch()
    {
        Assert.False(Matches("aaaa", "aa"));
    }

    [Fact]
    public void AMissingCharacterFailsTheWholeMatch()
    {
        Assert.False(Matches("dscx", "Discord"));
    }

    // ---- the point of the exercise -----------------------------------------

    [Fact]
    public void AnAbbreviationFindsItsWord()
    {
        // The case Levenshtein gets exactly wrong: as an edit distance this is three
        // insertions and nearly maximally bad, and as an abbreviation it is obvious.
        Assert.True(Matches("dsc", "Discord"));
    }

    [Fact]
    public void InitialsFindACamelCaseName()
    {
        Assert.True(Matches("vsc", "VisualStudioCode"));

        // Beats the same letters found in the middle of words, because that is what
        // makes initials worth typing.
        Assert.True(Score("vsc", "VisualStudioCode") > Score("vsc", "voluminous scaffolding"));
    }

    [Fact]
    public void InitialsFindASpacedName()
    {
        Assert.True(Score("vsc", "Visual Studio Code") > Score("vsc", "vichyssoise"));
    }

    [Fact]
    public void APrefixBeatsAnAbbreviation()
    {
        Assert.True(Score("disc", "Discord") > Score("disc", "D. I. Smith Consulting"));
    }

    [Fact]
    public void AdjacentCharactersBeatScatteredOnes()
    {
        Assert.True(Score("abc", "abcdefgh") > Score("abc", "axbxcxdx"));
    }

    [Fact]
    public void AMatchAtTheStartBeatsOneInTheMiddle()
    {
        Assert.True(Score("term", "Terminal") > Score("term", "Windows Terminal Preview"));
    }

    [Fact]
    public void AWordBoundaryBeatsTheMiddleOfAWord()
    {
        // "sc" against a real second word, versus "sc" buried inside one.
        Assert.True(Score("sc", "file scanner") > Score("sc", "miscellaneous"));
    }

    [Fact]
    public void TheShorterOfTwoMatchesWins()
    {
        Assert.True(Score("code", "Code") > Score("code", "Visual Studio Code Insiders Preview Edition"));
    }

    [Fact]
    public void APathSeparatorStartsAWord()
    {
        Assert.True(Score("bin", @"C:\tools\bin") > Score("bin", "combinatorics"));
    }

    // ---- highlighting -------------------------------------------------------

    [Fact]
    public void ThePositionsOfEveryMatchedCharacterAreReported()
    {
        Span<int> positions = stackalloc int[8];
        MatchResult result = FuzzyMatcher.Match("dsc", "Discord", positions);

        Assert.Equal(3, result.Matched);

        // D-i-s-c-o-r-d: D at 0, s at 2, c at 3. The UI underlines exactly these, so a
        // wrong index is a visibly wrong highlight rather than a silent scoring bug.
        Assert.Equal([0, 2, 3], positions[..result.Matched].ToArray());
    }

    [Fact]
    public void PositionsAreAscending()
    {
        Span<int> positions = stackalloc int[16];
        MatchResult result = FuzzyMatcher.Match("vsc", "Visual Studio Code", positions);

        for (int i = 1; i < result.Matched; i++)
            Assert.True(positions[i] > positions[i - 1], "positions must be strictly increasing");
    }

    [Fact]
    public void AShortSpanTruncatesPositionsWithoutBreakingTheMatch()
    {
        Span<int> tooSmall = stackalloc int[2];
        MatchResult result = FuzzyMatcher.Match("dsc", "Discord", tooSmall);

        // The caller may not want positions at all. Matching must not depend on
        // having somewhere to put them, or a scoring pass would have to allocate.
        Assert.True(result.IsMatch);
        Assert.Equal(3, result.Matched);
    }

    [Fact]
    public void PositionsCanBeDeclinedEntirely()
    {
        Assert.True(FuzzyMatcher.Match("dsc", "Discord", []).IsMatch);
    }

    // ---- ranking a realistic list -------------------------------------------

    [Theory]
    [InlineData("chr", "Chrome")]
    [InlineData("term", "Windows Terminal")]
    [InlineData("slack", "Slack")]
    [InlineData("vsc", "Visual Studio Code")]
    public void TheObviousAnswerWinsAmongRealWindowTitles(string query, string expected)
    {
        string[] titles =
        [
            "Chrome",
            "Windows Terminal",
            "Slack",
            "Visual Studio Code",
            "Shubbak - Microsoft Visual Studio",
            "chrome_widget_internal",
            "Character Map",
            "Task Manager",
        ];

        string best = titles
            .Select(t => (Title: t, Score: Score(query, t)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .First().Title;

        Assert.Equal(expected, best);
    }

    [Fact]
    public void NoMatchScoresZeroSoItCanBeFilteredOut()
    {
        MatchResult result = FuzzyMatcher.Match("zzz", "Discord");

        Assert.Equal(0, result.Score);
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void AMatchNeverScoresZero()
    {
        // Otherwise a genuine but heavily penalised match would be indistinguishable
        // from no match, and would vanish from the list.
        Assert.True(Score("t", new string('x', 200) + "t") > 0);
    }
}
