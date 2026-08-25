using Dalil.Core;

namespace Dalil.Core.Tests;

/// <summary>
/// Making a typed command runnable.
/// </summary>
/// <remarks>
/// Twelve of the thirty verbs take an argument, and none of them could be run.
/// Choosing one put its name and a space in the box, the verb list then matched
/// nothing - the term had grown longer than any verb - and Enter had no row to act on.
/// These tests are about that gap, and about refusing well when the text is wrong.
/// </remarks>
public sealed class CommandComposerTests
{
    [Fact]
    public void NothingIsOfferedForAnEmptyTerm()
    {
        Assert.Empty(CommandComposer.Compose(""));
        Assert.Empty(CommandComposer.Compose("   "));
    }

    [Fact]
    public void NothingIsOfferedWhileAVerbIsStillBeingTyped()
    {
        // "resi" is the user narrowing the verb list, and the verb list is already
        // showing exactly what it matches. Offering to run a half-typed word would be
        // noise, and acting on it would be wrong.
        Assert.Empty(CommandComposer.Compose("resi"));
        Assert.Empty(CommandComposer.Compose("focus"));
    }

    [Theory]
    [InlineData("focus --direction left")]
    [InlineData("resize --width 10")]
    [InlineData("layout --set fibonacci")]
    [InlineData("focus-window 0x1D0076")]
    [InlineData("move --workspace 3")]
    [InlineData("signal palette")]
    public void ACompleteCommandBecomesARunnableRow(string term)
    {
        PaletteEntry only = Assert.Single(CommandComposer.Compose(term));

        // The command sent is the text as typed. Anything else would mean the palette
        // running something other than what is on screen.
        Assert.Equal(term, only.Command);
        Assert.Equal(term, only.Primary);
    }

    [Fact]
    public void TheRunRowOutranksEverythingElse()
    {
        PaletteEntry only = Assert.Single(CommandComposer.Compose("resize --width 10"));

        // A window focused seconds ago carries a large recency. The thing being typed
        // has to win regardless, or Enter acts on a row the user is not looking at.
        Assert.Equal(long.MaxValue, only.Rank);
    }

    [Fact]
    public void TextThatCannotRunSaysWhyInTheParsersOwnWords()
    {
        PaletteEntry only = Assert.Single(CommandComposer.Compose("focus --direction sideways"));

        // Not a friendlier message written here. Two vocabularies for one mistake -
        // one when it is typed, another when the same text goes in a config file - is
        // worse than one blunt sentence used in both places.
        Assert.Contains("sideways", only.Secondary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TextThatCannotRunHasNothingToRun()
    {
        PaletteEntry only = Assert.Single(CommandComposer.Compose("resize --width sideways"));

        // Enter does nothing rather than sending something the window manager will
        // only refuse again, one round trip later, with nowhere to show the refusal.
        Assert.Equal(string.Empty, only.Command);
    }

    [Fact]
    public void AnArgumentTheParserDoesNotJudgeIsStillOffered()
    {
        // Layout names are resolved when the command runs, not when it parses, so the
        // parser accepts one it has never heard of. The composer must not invent a
        // stricter rule than the thing it is standing in for - it would refuse
        // commands that a keybinding with identical text would run happily.
        PaletteEntry only = Assert.Single(CommandComposer.Compose("layout --set nonsense-layout"));

        Assert.Equal("layout --set nonsense-layout", only.Command);
    }

    [Fact]
    public void AnUnknownVerbIsRefusedRatherThanSent()
    {
        PaletteEntry only = Assert.Single(CommandComposer.Compose("flurb --direction left"));

        Assert.Equal(string.Empty, only.Command);
        Assert.NotEmpty(only.Secondary);
    }

    [Fact]
    public void TheHintIsIncludedWhenTheParserOffersOne()
    {
        // The parser suggests a correction for a near-miss. Dropping it here would
        // throw away the most useful half of the diagnostic.
        PaletteEntry only = Assert.Single(CommandComposer.Compose("layout --set fibonaci"));

        Assert.NotEmpty(only.Secondary);
    }

    [Fact]
    public void SurroundingSpaceDoesNotChangeTheOutcome()
    {
        // The term always has a trailing space the moment a verb is chosen, because
        // that is what the palette puts there to invite an argument.
        PaletteEntry padded = Assert.Single(CommandComposer.Compose("  resize --width 10  "));

        Assert.Equal("resize --width 10", padded.Command);
    }

    [Fact]
    public void TheModelPutsAugmentedRowsFirst()
    {
        var model = new PaletteModel { Augmenter = (_, term) => CommandComposer.Compose(term) };

        model.SetEntries(
        [
            new PaletteEntry("resize", "Grow or shrink the focused window", [], "resize", Rank: long.MaxValue),
        ]);

        model.SetQuery(">resize --width 10");

        // Even against an entry carrying the same maximum rank, which is what a very
        // recently focused window looks like.
        Assert.Equal("resize --width 10", model.Rows[0].Entry.Command);
    }

    [Fact]
    public void AugmentedRowsOnlyAppearInCommandsMode()
    {
        var model = new PaletteModel
        {
            Augmenter = (mode, term) => mode is PaletteMode.Commands ? CommandComposer.Compose(term) : [],
        };

        model.SetEntries([new PaletteEntry("a window", "firefox", [], "focus-window 1")]);
        model.SetQuery("resize --width 10");

        // In windows mode this is a search for a window whose title contains that
        // text, not a command. Offering to run it would be startling.
        Assert.DoesNotContain(model.Rows, r => r.Entry.Command == "resize --width 10");
    }

    // ---- argument completion -----------------------------------------------

    private static CompletionSources Sources() => new(
        Workspaces: ["1", "2", "code", "mail"],
        Layouts: ["splith", "splitv", "fibonacci", "grid"],
        BindingModes: ["resize", "pause"],
        ScratchpadSlots: ["notes", "term"]);

    [Fact]
    public void ArgumentsAreCompletedFromTheRightSource()
    {
        IReadOnlyList<PaletteEntry> rows = CommandComposer.Compose("layout --set ", Sources());

        // Layout names, not workspace names. The kind comes from the catalogue, which
        // carried it all along and was read by nothing.
        Assert.Contains(rows, r => r.Command == "layout --set fibonacci");
        Assert.DoesNotContain(rows, r => r.Command == "layout --set code");
    }

    [Fact]
    public void CompletionsNarrowAsMoreIsTyped()
    {
        IReadOnlyList<PaletteEntry> rows = CommandComposer.Compose("layout --set split", Sources());

        Assert.Contains(rows, r => r.Command == "layout --set splith");
        Assert.Contains(rows, r => r.Command == "layout --set splitv");
        Assert.DoesNotContain(rows, r => r.Command == "layout --set grid");
    }

    [Fact]
    public void DirectionsNeedNoSource()
    {
        IReadOnlyList<PaletteEntry> rows = CommandComposer.Compose("focus --direction ", CompletionSources.None);

        // The same four everywhere, so asking the window manager for them would be a
        // round trip to be told what is already known.
        Assert.Contains(rows, r => r.Command == "focus --direction left");
        Assert.Contains(rows, r => r.Command == "focus --direction down");
    }

    [Fact]
    public void WorkspacesAreOfferedByName()
    {
        IReadOnlyList<PaletteEntry> rows = CommandComposer.Compose("move --workspace ", Sources());

        Assert.Contains(rows, r => r.Command == "move --workspace code");
    }

    [Fact]
    public void ScratchpadSlotsAreOfferedFromWhatIsActuallyStashed()
    {
        IReadOnlyList<PaletteEntry> rows = CommandComposer.Compose("scratchpad ", Sources());

        // Offering every name a slot could have would be infinite; offering the ones
        // holding a window is the only useful answer.
        Assert.Contains(rows, r => r.Command == "scratchpad notes");
    }

    [Fact]
    public void AFlagIsNotCompletedAsAValue()
    {
        IReadOnlyList<PaletteEntry> rows = CommandComposer.Compose("focus --", Sources());

        // "--" is choosing which argument to give, not typing one. Completing
        // workspace names into it would be nonsense.
        Assert.DoesNotContain(rows, r => r.Secondary.StartsWith("complete", StringComparison.Ordinal));
    }

    [Fact]
    public void NothingFiniteMeansNoCompletions()
    {
        // A window handle cannot be guessed, and a wrong guess pasted into the box is
        // worse than an empty list.
        IReadOnlyList<PaletteEntry> rows = CommandComposer.Compose("focus-window ", Sources());

        Assert.DoesNotContain(rows, r => r.Secondary.StartsWith("complete", StringComparison.Ordinal));
    }

    [Fact]
    public void ACompletionOnlyAppendsToWhatWasTyped()
    {
        PaletteEntry completion = CommandComposer
            .Compose("layout --set fib", Sources())
            .First(r => r.Secondary.StartsWith("complete", StringComparison.Ordinal));

        // Never rewritten. The user is mid-sentence, and a completion that reorders
        // the rest of the line is one nobody can predict.
        Assert.StartsWith("layout --set ", completion.Command, StringComparison.Ordinal);
        Assert.Equal("layout --set fibonacci", completion.Command);
    }

    [Fact]
    public void TheRunRowStaysAboveEveryCompletion()
    {
        IReadOnlyList<PaletteEntry> rows = CommandComposer.Compose("layout --set fibonacci", Sources());

        // What was typed is what Enter must act on, even when a completion happens to
        // spell the same thing.
        Assert.Equal(long.MaxValue, rows[0].Rank);
        Assert.All(rows.Skip(1), r => Assert.True(r.Rank < rows[0].Rank));
    }
}