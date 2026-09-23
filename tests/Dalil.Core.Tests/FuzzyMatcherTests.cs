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
    // ---- the best alignment, not the first -----------------------------------

    [Fact]
    public void TheWordStartIsPreferredToTheSameLettersScatteredEarlier()
    {
        // The case the walk got wrong: it took the s of Visual and the t of Studio,
        // two letters that happen to be in order, when the St that starts a word was
        // there to be had - and highlighted the scatter, which is how anyone noticed.
        Span<int> positions = stackalloc int[8];
        MatchResult result = FuzzyMatcher.Match("st", "Visual Studio", positions);

        Assert.Equal([7, 8], positions[..result.Matched].ToArray());
    }

    [Fact]
    public void TheHighlightFollowsTheBestAlignment()
    {
        Span<int> positions = stackalloc int[8];
        MatchResult result = FuzzyMatcher.Match("code", "Visual Studio Code - Decoder", positions);

        // "Code" the word, not C-o-d-e picked from wherever they first occur.
        Assert.Equal([14, 15, 16, 17], positions[..result.Matched].ToArray());
    }

    [Fact]
    public void AnAbbreviationIsNotChargedForTheWordsItSkips()
    {
        // "st" for Studio should not lose to "st" inside a shorter word near the
        // front. The letters an abbreviation leaves out are the point of it.
        Assert.True(Score("st", "Visual Studio") > Score("st", "Fastest"));
    }

    [Fact]
    public void ALongCandidateStillMatchesByTheWalk()
    {
        string title = new string('x', FuzzyMatcher.MaxAlignedLength + 10) + " Studio";

        Span<int> positions = stackalloc int[8];
        MatchResult result = FuzzyMatcher.Match("st", title, positions);

        Assert.True(result.IsMatch);
        Assert.Equal(2, result.Matched);
        Assert.Equal(FuzzyMatcher.MaxAlignedLength + 11, positions[0]);
    }

    // ---- folding ------------------------------------------------------------

    [Theory]
    [InlineData("cafe", "Café")]
    [InlineData("uber", "Über")]
    [InlineData("resume", "Résumé - Word")]
    [InlineData("naive", "naïve")]
    public void AccentsDoNotStandInTheWay(string query, string candidate) =>
        Assert.True(Matches(query, candidate));

    [Fact]
    public void AnAccentTypedStillMatchesThePlainLetter()
    {
        // Folding is both ways: somebody with the accent on their keyboard should not
        // be worse off than somebody without.
        Assert.True(Matches("café", "cafe"));
    }

    [Theory]
    [InlineData("احمد", "أحمد")]       // hamza above
    [InlineData("اسلام", "إسلام")]     // hamza below
    [InlineData("امال", "آمال")]       // madda
    [InlineData("مكتبه", "مكتبة")]     // ta marbuta as ha
    [InlineData("علي", "على")]         // alif maqsura as ya
    [InlineData("كتاب", "کتاب")]       // Persian kaf
    [InlineData("ايران", "ایران")]     // Persian yeh
    public void TheArabicSpellingsOfOneSoundFindEachOther(string query, string candidate) =>
        Assert.True(Matches(query, candidate));

    [Fact]
    public void VowelMarksInTheCandidateAreSteppedOver()
    {
        // مُحَمَّد with its marks is found by محمد without them, and the letters across
        // a mark still count as adjacent - a prefix, not a scatter.
        const string Marked = "\u0645\u064F\u062D\u064E\u0645\u0651\u064E\u062F";

        Assert.True(Matches("\u0645\u062D\u0645\u062F", Marked));
        Assert.True(Score("\u0645\u062D\u0645\u062F", Marked) > Score("\u0645\u062D\u0645\u062F", "\u0645 \u062D \u0645 \u062F"));
    }

    [Fact]
    public void VowelMarksInTheQueryAreSteppedOverToo()
    {
        Assert.True(Matches("\u0645\u064F\u062D\u0645\u062F", "\u0645\u062D\u0645\u062F"));
    }

    [Fact]
    public void FoldingIsWhatTheMatcherSaysItIs()
    {
        Assert.Equal('e', FuzzyMatcher.Fold('É'));
        Assert.Equal('a', FuzzyMatcher.Fold('A'));
        Assert.Equal('\u0627', FuzzyMatcher.Fold('\u0623'));
        Assert.Equal('\u0647', FuzzyMatcher.Fold('\u0629'));

        // Letters with no decomposition are themselves: ø is not o, ß is not s.
        Assert.Equal('ø', FuzzyMatcher.Fold('Ø'));
        Assert.Equal('ß', FuzzyMatcher.Fold('ß'));
    }

    // ---- several words ------------------------------------------------------

    [Fact]
    public void EveryWordMustMatchInAnyOrder()
    {
        Assert.True(Matches("code proj", "My Project - Visual Studio Code"));
        Assert.True(Matches("proj code", "My Project - Visual Studio Code"));
        Assert.False(Matches("code xyz", "My Project - Visual Studio Code"));
    }

    [Fact]
    public void TheWordsPositionsAreMergedInOrder()
    {
        Span<int> positions = stackalloc int[16];
        MatchResult result = FuzzyMatcher.Match("code proj", "My Project - Visual Studio Code", positions);

        Assert.Equal(8, result.Matched);

        int[] found = positions[..result.Matched].ToArray();

        Assert.Equal([3, 4, 5, 6, 27, 28, 29, 30], found);
    }

    [Fact]
    public void TwoWordsLightingTheSameLetterCountItOnce()
    {
        Span<int> positions = stackalloc int[16];
        MatchResult result = FuzzyMatcher.Match("co co", "Code", positions);

        Assert.True(result.IsMatch);
        Assert.Equal([0, 1], positions[..Math.Min(result.Matched, 2)].ToArray());
    }

    [Fact]
    public void ATrailingSpaceIsTheNextWordNotYetTyped()
    {
        // Somebody who has typed "code " is about to type another word. Requiring a
        // literal space emptied the list at exactly that moment.
        Assert.True(Matches("code ", "Code"));
        Assert.True(Matches("  code  ", "Code"));
        Assert.Equal(1, FuzzyMatcher.Match("   ", "anything").Score);
    }

    [Fact]
    public void TwoWordsBeatOneWhenBothLand()
    {
        Assert.True(Score("vis code", "Visual Studio Code") > Score("vis code", "Visual Basic Code Cleaner Edition"));
    }
}