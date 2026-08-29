using Dalil.Core;

namespace Dalil.Core.Tests;

/// <summary>
/// Editing the search box, marking rows, and moving between modes.
/// </summary>
/// <remarks>
/// The palette used to be append-only and mode-blind: there was no caret, no way to
/// act on more than one window, and Tab walked a C# enum. All three are decisions
/// rather than plumbing, so all three are settled here rather than on screen.
/// </remarks>
public sealed class PaletteEditingTests
{
    private static PaletteEntry Entry(string primary, string? target = null) =>
        new(primary, string.Empty, [], $"focus-window {primary}", Target: target);

    private static PaletteModel WithQuery(string query)
    {
        var model = new PaletteModel();
        model.SetQuery(query);
        return model;
    }

    // ---- the caret -----------------------------------------------------------------

    [Fact]
    public void TheCaretStartsAtTheEndOfWhateverWasHandedOver()
    {
        Assert.Equal(6, WithQuery(">focus").Caret);
    }

    [Fact]
    public void TheCaretIsReportedRelativeToWhatIsActuallyDrawn()
    {
        // The prefix is a character of the query and is not drawn, so the renderer's
        // idea of where the caret is differs from the model's by exactly its length.
        // Getting this wrong puts the caret one character out in every mode but the
        // window list.
        PaletteModel model = WithQuery(">focus");

        Assert.Equal(6, model.Caret);
        Assert.Equal(5, model.TermCaret);
    }

    [Fact]
    public void TheCaretWillNotClimbOntoThePrefix()
    {
        // There is nowhere to draw it there, and a caret that disappears when you press
        // Left one time too many looks like the field has stopped responding.
        PaletteModel model = WithQuery(">a");

        model.MoveCaret(-10);

        Assert.Equal(1, model.Caret);
        Assert.Equal(0, model.TermCaret);
    }

    [Fact]
    public void HomeAndEndReachBothEndsOfWhatWasTyped()
    {
        PaletteModel model = WithQuery(">focus --workspace 2");

        model.CaretToEdge(end: false);
        Assert.Equal(1, model.Caret);

        model.CaretToEdge(end: true);
        Assert.Equal(20, model.Caret);
    }

    [Fact]
    public void ACharacterGoesInWhereTheCaretIs()
    {
        // Which is the whole point. A typo in the middle of "resize --width +5%" used
        // to cost the rest of the line, because deleting back to it was the only way to
        // reach it.
        PaletteModel model = WithQuery(">fcus");

        model.MoveCaret(-3);
        model.Insert('o');

        Assert.Equal(">focus", model.Query);
        Assert.Equal(3, model.Caret);
    }

    [Fact]
    public void DeleteRemovesTheCharacterInFrontOfTheCaret()
    {
        PaletteModel model = WithQuery(">foocus");

        model.MoveCaret(-4);
        model.DeleteForward();

        Assert.Equal(">focus", model.Query);
    }

    [Fact]
    public void DeleteAtTheEndDoesNothing()
    {
        PaletteModel model = WithQuery(">focus");

        model.DeleteForward();

        Assert.Equal(">focus", model.Query);
    }

    [Fact]
    public void BackspaceRemovesTheCharacterBehindTheCaret()
    {
        PaletteModel model = WithQuery(">focus");

        model.MoveCaret(-2);
        model.DeleteBack(wholeWord: false);

        Assert.Equal(">fous", model.Query);
    }

    // ---- staying in the mode you asked for -------------------------------------------

    [Fact]
    public void ClearingWhatWasTypedKeepsTheMode()
    {
        // The bug this exists for. Ctrl+U cleared the whole query, which drops the
        // prefix, which silently moves the palette back to the window list - so a key
        // documented as "clear what you typed" also changed what Enter was going to do,
        // and the user had asked for neither.
        PaletteModel model = WithQuery(">focus --workspace 2");

        model.ClearTerm();

        Assert.Equal(">", model.Query);
        Assert.Equal(PaletteMode.Commands, model.Mode);
        Assert.Equal(string.Empty, model.Term);
    }

    [Fact]
    public void ClearingInTheWindowListLeavesNothingBehind()
    {
        // The window list has no prefix to keep, so this is the one mode where clearing
        // really does empty the box.
        PaletteModel model = WithQuery("chrome");

        model.ClearTerm();

        Assert.Equal(string.Empty, model.Query);
        Assert.Equal(PaletteMode.Windows, model.Mode);
    }

    [Fact]
    public void DeletingAWordStopsAtThePrefix()
    {
        // The same bug by another route. Ctrl+Backspace on a single-word term found no
        // space to stop at and cleared everything, prefix included.
        PaletteModel model = WithQuery(">focus");

        model.DeleteBack(wholeWord: true);

        Assert.Equal(">", model.Query);
        Assert.Equal(PaletteMode.Commands, model.Mode);
    }

    [Fact]
    public void DeletingAWordCrossesTheSpaceItEndsWith()
    {
        // What every other text field on the machine does, and what makes the key worth
        // pressing twice: deleting a word from "focus --workspace " should take the
        // flag, not only the space after it.
        PaletteModel model = WithQuery(">focus --workspace ");

        model.DeleteBack(wholeWord: true);

        Assert.Equal(">focus ", model.Query);
    }

    [Fact]
    public void BackspaceOnAnEmptyTermStillLeavesTheMode()
    {
        // Because that is how a mode is left, and it has to keep working now that
        // clearing does not do it by accident. Guarding the prefix against a word
        // delete very nearly took this with it, which would have left the command list
        // reachable only by Tab.
        PaletteModel model = WithQuery(">");

        model.DeleteBack(wholeWord: false);

        Assert.Equal(string.Empty, model.Query);
        Assert.Equal(PaletteMode.Windows, model.Mode);
    }

    [Fact]
    public void BackspacingOverAPrefixKeepsWhatWasBeingSearchedFor()
    {
        // The same thing changing mode any other way does. Somebody backing out of the
        // command list has not changed their mind about the word they were typing.
        PaletteModel model = WithQuery(">focus");

        model.CaretToEdge(end: false);
        model.DeleteBack(wholeWord: false);

        Assert.Equal("focus", model.Query);
        Assert.Equal(PaletteMode.Windows, model.Mode);
        Assert.Equal(0, model.Caret);
    }

    // ---- moving between modes --------------------------------------------------------

    [Fact]
    public void TabWalksTheRingRatherThanTheEnum()
    {
        // The ring is ordered by how often a mode is wanted. It used to be the
        // declaration order of a C# enum, which put monitors between scratchpad and
        // inspect for no reason anybody chose.
        var model = new PaletteModel();

        Assert.Equal(PaletteMode.Commands, model.NextMode(forward: true));
    }

    [Fact]
    public void HelpIsNotSomewhereYouPassThrough()
    {
        // It has a prefix, it has a jump key, and it is one Escape from anywhere. It
        // does not also need to be in everybody's way.
        Assert.DoesNotContain(PaletteMode.Help, PaletteModel.TabRing);
    }

    [Fact]
    public void TabbingOutOfHelpLandsBackInTheRing()
    {
        // Reached by its prefix or its jump key, help is outside the ring - so Tab has
        // to mean something there rather than leaving somebody stuck in it.
        PaletteModel model = WithQuery("?");

        Assert.Equal(PaletteMode.Windows, model.NextMode(forward: true));
        Assert.Equal(PaletteMode.Monitors, model.NextMode(forward: false));
    }

    [Fact]
    public void TheRingWrapsBothWays()
    {
        PaletteModel model = WithQuery("%");

        Assert.Equal(PaletteMode.Windows, model.NextMode(forward: true));

        model.SetQuery(string.Empty);
        Assert.Equal(PaletteMode.Monitors, model.NextMode(forward: false));
    }

    [Fact]
    public void EveryModeIsReachableByADigit()
    {
        // Which is what makes it safe for a mode to have no prefix at all.
        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
            Assert.Contains(mode, PaletteModel.JumpOrder);
    }

    [Fact]
    public void TheDigitsAgreeWithTheOrderTheHintBarDrawsThem()
    {
        // So the number is read off the screen rather than remembered.
        Assert.Equal(PaletteMode.Windows, PaletteModel.ModeAtJump(1));
        Assert.Equal(PaletteMode.Help, PaletteModel.ModeAtJump(PaletteModel.JumpOrder.Count));
        Assert.Null(PaletteModel.ModeAtJump(0));
        Assert.Null(PaletteModel.ModeAtJump(PaletteModel.JumpOrder.Count + 1));
    }

    // ---- marking ---------------------------------------------------------------------

    [Fact]
    public void OnlyARowThatNamesAWindowCanBeMarked()
    {
        // Marking a layout would be marking something there is no way to act on as a
        // set, and a key that silently does nothing on two rows in three is worse than
        // one that is honestly unavailable.
        var model = new PaletteModel();
        model.SetEntries([Entry("fibonacci")]);

        Assert.False(model.ToggleMark());
        Assert.Equal(0, model.MarkedCount);
    }

    [Fact]
    public void MarkingAndUnmarkingAreTheSameKey()
    {
        var model = new PaletteModel();
        model.SetEntries([Entry("a", target: "focus-window 1")]);

        Assert.True(model.ToggleMark());
        Assert.Equal(1, model.MarkedCount);

        Assert.True(model.ToggleMark());
        Assert.Equal(0, model.MarkedCount);
    }

    [Fact]
    public void MarksSurviveTheListBeingRebuiltUnderneathThem()
    {
        // Entries are rebuilt from the wire every time anything happens on the desktop,
        // so anything holding them by reference loses its marks the moment a window
        // opens somewhere else. Marking six windows and having five of them forgotten
        // because a notification appeared is the worst possible way to meet this
        // feature.
        var model = new PaletteModel();
        model.SetEntries([Entry("a", target: "focus-window 1"), Entry("b", target: "focus-window 2")]);

        model.ToggleMark();
        Assert.Equal(1, model.MarkedCount);

        model.SetEntries([Entry("a", target: "focus-window 1"), Entry("c", target: "focus-window 3")]);

        Assert.Equal(1, model.MarkedCount);
        Assert.Equal("focus-window 1", model.Marked.Single().Target);
    }

    [Fact]
    public void MarksSurviveTheRowsBeingFilteredAway()
    {
        // Acting on a marked set must not depend on the set still being on screen.
        // Marking six windows and then typing something that matches none of them is
        // an ordinary way to reach the action list.
        var model = new PaletteModel();
        model.SetEntries([Entry("alpha", target: "focus-window 1")]);

        model.ToggleMark();
        model.SetQuery("zzzz");

        Assert.Empty(model.Rows);
        Assert.Equal(1, model.MarkedCount);
    }

    [Fact]
    public void MarksAreKeptInTheOrderTheyWereMade()
    {
        // Because that is the order the windows will be tiled in when they arrive.
        var model = new PaletteModel();

        model.SetEntries(
        [
            Entry("a", target: "focus-window 1"),
            Entry("b", target: "focus-window 2"),
            Entry("c", target: "focus-window 3"),
        ]);

        model.SelectAt(2);
        model.ToggleMark();

        model.SelectAt(0);
        model.ToggleMark();

        Assert.Equal(
            ["focus-window 3", "focus-window 1"],
            model.Marked.Select(e => e.Target ?? string.Empty).ToArray());
    }

    // ---- selection and scrolling -------------------------------------------------------

    [Fact]
    public void TheSelectionSurvivesARefreshThatRebuildsEveryRow()
    {
        // It never used to. The selection was preserved by reference identity alone,
        // and a refresh replaces every entry with an equal one built from the wire - so
        // the selection went back to the top on every window event, which on a busy
        // desktop is several times a second.
        var model = new PaletteModel();
        model.SetEntries([Entry("a"), Entry("b"), Entry("c")]);

        model.SelectAt(2);

        model.SetEntries([Entry("a"), Entry("b"), Entry("c")]);

        Assert.Equal("c", model.Selected!.Entry.Primary);
    }

    [Fact]
    public void AStepLargerThanTheListLandsAtTheEndRatherThanLappingIt()
    {
        // PageDown on five rows is somebody asking for the bottom, not for two and a
        // half laps of a list they can see all of.
        var model = new PaletteModel();
        model.SetEntries([Entry("a"), Entry("b"), Entry("c")]);

        model.MoveSelection(12);
        Assert.Equal(2, model.SelectedIndex);

        model.MoveSelection(-12);
        Assert.Equal(0, model.SelectedIndex);
    }

    [Fact]
    public void AShortListAsksForAShortWindow()
    {
        // A search that matched two things used to be drawn as two rows of text above
        // ten rows of empty background, which reads as the window having failed to
        // finish drawing itself.
        var model = new PaletteModel();
        model.SetEntries([Entry("a"), Entry("b")]);

        Assert.Equal(2, model.RowsToShow(12));
    }

    [Fact]
    public void ALongListAsksForNoMoreThanFits()
    {
        var model = new PaletteModel();
        model.SetEntries(Enumerable.Range(0, 50).Select(i => Entry($"w{i}")));

        Assert.Equal(12, model.RowsToShow(12));
    }

    [Fact]
    public void AnEmptyListStillLeavesRoomForTheSentenceExplainingItself()
    {
        // "No matches" and the line under it are themselves worth showing properly.
        var model = new PaletteModel();
        model.SetEntries([]);

        Assert.Equal(2, model.RowsToShow(12));
    }

    // ---- matching --------------------------------------------------------------------

    [Fact]
    public void ARowMatchedOnItsApplicationSaysSo()
    {
        // Finding a window by its process when the title says nothing about it -
        // "Untitled document" - is most of what the dim half of a row is for, and a row
        // that appeared with nothing underlined anywhere read as the palette having
        // matched it by accident.
        var model = new PaletteModel();

        model.SetEntries([new PaletteEntry("Untitled document", "msedge", [], "focus-window 1")]);
        model.SetQuery("edge");

        PaletteRow row = Assert.Single(model.Rows);

        Assert.Empty(row.Positions);
        Assert.NotEmpty(row.SecondaryPositions!);
    }

    [Fact]
    public void AQueryLongerThanTheHighlightBufferDoesNotThrow()
    {
        // The matcher reports how many characters matched and writes a position only
        // while the caller's span has room, so slicing by the count alone reads past
        // the end - a latent crash waiting for somebody to paste a sentence into the
        // search box.
        string long_ = new('a', FuzzyMatcher.MaxPositions * 2);

        var model = new PaletteModel();
        model.SetEntries([new PaletteEntry(long_, string.Empty, [], "x")]);

        model.SetQuery(long_);

        Assert.Single(model.Rows);
    }
    // ---- rows derived from the query itself ----------------------------------------

    [Fact]
    public void ARowThatCanRunWhatWasTypedGoesAboveTheMatches()
    {
        // It is the thing being composed, so it is what Enter should be aimed at.
        var model = new PaletteModel
        {
            Augmenter = (_, term) => [new PaletteEntry(term, "run it", [], term)],
        };

        model.SetEntries([Entry("something")]);
        model.SetQuery("equalise");

        Assert.Equal("equalise", model.Rows[0].Entry.Primary);
    }

    [Fact]
    public void ARowThatCannotRunGoesBelowThem()
    {
        // The bug this exists for. Every macro with a space in its name - "Code
        // layout" - put an "unknown command 'Code'" row above itself, because the
        // composer emits one for any term containing a space. Enter therefore landed
        // on a row that does nothing, and the feature looked broken while working
        // perfectly one row further down.
        var model = new PaletteModel
        {
            Augmenter = (_, _) => [new PaletteEntry("Code lay", "unknown command", [], string.Empty)],
        };

        model.SetEntries([Entry("Code layout")]);
        model.SetQuery("Code lay");

        Assert.Equal("Code layout", model.Rows[0].Entry.Primary);
        Assert.Equal("Code lay", model.Rows[^1].Entry.Primary);
    }

    [Fact]
    public void TheDiagnosticIsStillTheOnlyRowWhenNothingMatched()
    {
        // Which is the case it was written for: a mistyped verb matches no row in the
        // command list, and the parser's explanation is the only useful thing on
        // screen. Pushing it down must not push it away.
        var model = new PaletteModel
        {
            Augmenter = (_, _) => [new PaletteEntry("focuss --direction left", "unknown command", [], string.Empty)],
        };

        model.SetEntries([Entry("focus")]);
        model.SetQuery("focuss --direction left");

        Assert.Equal("focuss --direction left", Assert.Single(model.Rows).Entry.Primary);
    }
}
