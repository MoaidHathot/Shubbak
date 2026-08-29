using Dalil.Core;

namespace Dalil.Core.Tests;

/// <summary>
/// The palette's state machine: prefixes, ranking, selection and scrolling.
/// </summary>
/// <remarks>
/// All of it decided without a window, because these are the questions that are
/// easiest to get subtly wrong and hardest to notice on screen - what happens to the
/// selection when the list shrinks under it, whether a row moves out from under the
/// user's finger between keystrokes, whether the scroll window can disagree with the
/// selection.
/// </remarks>
public sealed class PaletteModelTests
{
    private static PaletteEntry Entry(string primary, string secondary = "", long rank = 0) =>
        new(primary, secondary, [], $"focus-window {primary}", rank);

    private static PaletteModel WithWindows(params string[] titles)
    {
        var model = new PaletteModel();
        model.SetEntries(titles.Select(t => Entry(t)));
        return model;
    }

    // ---- modes ---------------------------------------------------------------

    [Fact]
    public void NoPrefixMeansWindows()
    {
        Assert.Equal(PaletteMode.Windows, PalettePrefixes.Default.ModeOf(""));
        Assert.Equal(PaletteMode.Windows, PalettePrefixes.Default.ModeOf("chrome"));
    }

    [Theory]
    [InlineData(">layout", PaletteMode.Commands)]
    [InlineData("#3", PaletteMode.Workspaces)]
    [InlineData("~fib", PaletteMode.Layouts)]
    public void APrefixSelectsAMode(string query, PaletteMode expected)
    {
        Assert.Equal(expected, PalettePrefixes.Default.ModeOf(query));
    }

    [Fact]
    public void ThePrefixIsNotPartOfWhatIsSearchedFor()
    {
        var model = new PaletteModel();
        model.SetQuery(">pause");

        Assert.Equal("pause", model.Term);
        Assert.Equal(PaletteMode.Commands, model.Mode);
    }

    [Fact]
    public void SwitchingModeKeepsWhatWasTyped()
    {
        var model = new PaletteModel();
        model.SetQuery("chrome");

        model.SetMode(PaletteMode.Commands);

        // Tab should change what is being searched, not throw away the search.
        Assert.Equal(">chrome", model.Query);
        Assert.Equal("chrome", model.Term);
    }

    [Fact]
    public void SwitchingBackToWindowsDropsThePrefix()
    {
        var model = new PaletteModel();
        model.SetQuery(">chrome");

        model.SetMode(PaletteMode.Windows);

        Assert.Equal("chrome", model.Query);
        Assert.Equal(PaletteMode.Windows, model.Mode);
    }

    // ---- filtering and ranking ------------------------------------------------

    [Fact]
    public void AnEmptyQueryShowsEverything()
    {
        PaletteModel model = WithWindows("Chrome", "Terminal", "Slack");

        Assert.Equal(3, model.Rows.Count);
    }

    [Fact]
    public void AnEmptyQueryOrdersByRecency()
    {
        var model = new PaletteModel();
        model.SetEntries(
        [
            Entry("oldest", rank: 1),
            Entry("newest", rank: 9),
            Entry("middle", rank: 5),
        ]);

        // The only ordering worth having before anything is typed. The z-order is
        // meaningless for a concealed window and nobody remembers titles
        // alphabetically.
        Assert.Equal(["newest", "middle", "oldest"], model.Rows.Select(r => r.Entry.Primary));
    }

    [Fact]
    public void TypingNarrowsTheList()
    {
        PaletteModel model = WithWindows("Chrome", "Terminal", "Slack");
        model.SetQuery("chr");

        PaletteRow only = Assert.Single(model.Rows);
        Assert.Equal("Chrome", only.Entry.Primary);
    }

    [Fact]
    public void SomethingFoundOnlyByProcessNameIsStillOffered()
    {
        var model = new PaletteModel();
        model.SetEntries([Entry("Untitled document", "notepad")]);

        model.SetQuery("notepad");

        // A window whose title says nothing about which application it belongs to is
        // exactly the sort that gets lost.
        Assert.Single(model.Rows);
    }

    [Fact]
    public void ATitleMatchOutranksAProcessMatch()
    {
        var model = new PaletteModel();
        model.SetEntries(
        [
            Entry("Something else entirely", "chrome"),
            Entry("Chrome", "chrome"),
        ]);

        model.SetQuery("chrome");

        // The title is what the user is picturing when they type.
        Assert.Equal("Chrome", model.Rows[0].Entry.Primary);
    }

    [Fact]
    public void MatchedCharactersAreReportedForHighlighting()
    {
        PaletteModel model = WithWindows("Discord");
        model.SetQuery("dsc");

        Assert.Equal([0, 2, 3], model.Rows[0].Positions);
    }

    [Fact]
    public void AProcessOnlyMatchHighlightsNothing()
    {
        var model = new PaletteModel();
        model.SetEntries([Entry("Untitled document", "notepad")]);
        model.SetQuery("notepad");

        // Underlining nothing is better than underlining characters of the title that
        // had nothing to do with why the row is here.
        Assert.Empty(model.Rows[0].Positions);
    }

    [Fact]
    public void TheOrderIsTotalSoRowsDoNotSwapBetweenKeystrokes()
    {
        var model = new PaletteModel();

        // Same score, same rank: without a final tie-break these could come back in
        // either order, and the row under the user's finger would change as they type.
        model.SetEntries([Entry("alpha"), Entry("bravo"), Entry("charlie")]);

        string[] first = [.. model.Rows.Select(r => r.Entry.Primary)];
        model.SetEntries([Entry("charlie"), Entry("alpha"), Entry("bravo")]);
        string[] second = [.. model.Rows.Select(r => r.Entry.Primary)];

        Assert.Equal(first, second);
    }

    // ---- selection -------------------------------------------------------------

    [Fact]
    public void TheFirstRowIsSelectedToBeginWith()
    {
        PaletteModel model = WithWindows("Chrome", "Terminal");

        Assert.Equal(0, model.SelectedIndex);
        Assert.Equal("Chrome", model.Selected!.Entry.Primary);
    }

    [Fact]
    public void AnEmptyListHasNoSelection()
    {
        PaletteModel model = WithWindows("Chrome");
        model.SetQuery("zzzzz");

        Assert.Empty(model.Rows);
        Assert.Equal(-1, model.SelectedIndex);
        Assert.Null(model.Selected);

        // Pressing Enter on nothing must be harmless rather than an exception on the
        // UI thread, which would take the process down.
        model.MoveSelection(1);
        Assert.Null(model.Selected);
    }

    [Fact]
    public void TheSelectionWrapsAtBothEnds()
    {
        PaletteModel model = WithWindows("a", "b", "c");

        model.MoveSelection(-1);
        Assert.Equal(2, model.SelectedIndex);

        model.MoveSelection(1);
        Assert.Equal(0, model.SelectedIndex);
    }

    [Fact]
    public void TypingResetsTheSelectionToTheBestMatch()
    {
        PaletteModel model = WithWindows("Chrome", "Terminal", "Slack");
        model.MoveSelection(2);

        model.SetQuery("t");

        // Narrowing exists to bring the answer to the top. Keeping the old index
        // would leave the user pressing Enter on whatever happened to land there.
        Assert.Equal(0, model.SelectedIndex);
    }

    [Fact]
    public void RefreshingTheListKeepsTheSelectedEntry()
    {
        var model = new PaletteModel();
        PaletteEntry chrome = Entry("Chrome");
        PaletteEntry terminal = Entry("Terminal");

        model.SetEntries([chrome, terminal]);
        model.MoveSelection(1);
        PaletteEntry chosen = model.Selected!.Entry;

        // The window manager reports a change and the host hands the list back. If
        // this jumped to the top, the list would be unusable whenever anything at all
        // was happening on screen - which is most of the time.
        model.SetEntries([chrome, terminal]);

        Assert.Same(chosen, model.Selected!.Entry);
    }

    [Fact]
    public void ASelectedEntryThatDisappearsFallsBackToTheTop()
    {
        var model = new PaletteModel();
        PaletteEntry chrome = Entry("Chrome");

        model.SetEntries([chrome, Entry("Terminal")]);
        model.MoveSelection(1);

        model.SetEntries([chrome]);

        Assert.Equal(0, model.SelectedIndex);
        Assert.Same(chrome, model.Selected!.Entry);
    }

    // ---- scrolling ---------------------------------------------------------------

    [Fact]
    public void AShortListIsShownWhole()
    {
        PaletteModel model = WithWindows("a", "b", "c");

        Assert.Equal((0, 3), model.VisibleWindow(10));
    }

    [Fact]
    public void ALongListScrollsToKeepTheSelectionInView()
    {
        PaletteModel model = WithWindows([.. Enumerable.Range(0, 100).Select(i => $"window {i:D3}")]);

        model.SelectEdge(last: true);
        (int first, int count) = model.VisibleWindow(10);

        Assert.Equal(10, count);
        Assert.InRange(model.SelectedIndex, first, first + count - 1);
    }

    [Fact]
    public void TheScrollWindowNeverRunsPastTheEnd()
    {
        PaletteModel model = WithWindows([.. Enumerable.Range(0, 20).Select(i => $"w{i}")]);

        for (int i = 0; i < 20; i++)
        {
            (int first, int count) = model.VisibleWindow(7);

            Assert.True(first >= 0);
            Assert.True(first + count <= model.Rows.Count, "the window must stay inside the list");

            model.MoveSelection(1);
        }
    }

    [Fact]
    public void NoCapacityMeansNothingIsDrawn()
    {
        PaletteModel model = WithWindows("a", "b");

        Assert.Equal((0, 0), model.VisibleWindow(0));
    }
    [Fact]
    public void PercentLooksAtTheMonitors()
    {
        PaletteModel model = new();
        model.SetQuery("%");

        Assert.Equal(PaletteMode.Monitors, model.Mode);
        Assert.Equal(string.Empty, model.Term);
    }

    [Fact]
    public void EveryModeIsReachableByAPrefixOrIsTheDefault()
    {
        // A mode with no way in is a mode nobody can use. Adding one to the enum and
        // forgetting the prefix is the easy mistake, and this is what catches it.
        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
        {
            if (mode == PaletteMode.Windows) continue;

            Assert.NotEqual('\0', PaletteModel.PrefixFor(mode));
        }
    }

    [Fact]
    public void NoTwoModesShareAPrefix()
    {
        // Two modes on one key would make the second unreachable, and silently.
        char[] prefixes =
        [
            .. Enum.GetValues<PaletteMode>()
                .Select(PaletteModel.PrefixFor)
                .Where(p => p != '\0'),
        ];

        Assert.Equal(prefixes.Length, prefixes.Distinct().Count());
    }

    [Fact]
    public void TheModelStartsWithNothingToReportAboutTheWindowManager()
    {
        // Before the first query has answered, "not paused" is a guess. It is the
        // safer one: claiming a pause that is not happening would send somebody
        // looking for a cause that does not exist.
        PaletteModel model = new();

        Assert.False(model.Status.Paused);
        Assert.Null(model.Status.BindingMode);
    }

    [Fact]
    public void TheModelRemembersWhatTheWindowManagerSaidAboutItself()
    {
        PaletteModel model = new();
        model.SetStatus(new WmStatus(Paused: true, BindingMode: "resize"));

        Assert.True(model.Status.Paused);
        Assert.Equal("resize", model.Status.BindingMode);
    }
    [Fact]
    public void EveryModeCanBeNamedBySignal()
    {
        // A keybinding names the mode it wants to open. The list this replaced was
        // written by hand and had fallen behind - scratchpad, help and monitors all
        // existed and all quietly opened the window list instead.
        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
            Assert.Equal(mode, PaletteModel.ModeNamed(PaletteModel.NameOf(mode)));
    }

    [Theory]
    [InlineData("workspace", PaletteMode.Workspaces)]
    [InlineData("Command", PaletteMode.Commands)]
    [InlineData("  monitors  ", PaletteMode.Monitors)]
    public void ModeNamesAreForgivingAboutCaseSpacingAndPlurals(string given, PaletteMode expected)
    {
        // Somebody writing `signal "palette" "workspace"` is not in any doubt about
        // what they meant, and refusing it would be pedantry with no upside.
        Assert.Equal(expected, PaletteModel.ModeNamed(given));
    }

    [Fact]
    public void AnUnknownModeNameIsRefusedRatherThanGuessed()
    {
        // The caller decides what to do about it. Guessing here would hide a typo in
        // a config file behind a palette that opens on the wrong thing.
        Assert.Null(PaletteModel.ModeNamed("elephants"));
        Assert.Null(PaletteModel.ModeNamed(""));
        Assert.Null(PaletteModel.ModeNamed(null));
    }
}