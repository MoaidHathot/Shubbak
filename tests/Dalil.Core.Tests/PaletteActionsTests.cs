using Dalil.Core;
using Shubbak.Ipc;

namespace Dalil.Core.Tests;

/// <summary>
/// What the palette offers to do to a window.
/// </summary>
/// <remarks>
/// The actions are state-aware because a list that offers to minimise a window that is
/// already minimised is a list nobody reads twice. The verbs underneath are toggles;
/// only the wording changes, and getting the wording backwards is invisible until
/// somebody uses it.
/// </remarks>
public sealed class PaletteActionsTests
{
    private static WindowCandidate Window(
        long handle = 0x100,
        bool managed = true,
        string? state = "tiling",
        string concealment = "none",
        string? workspace = "1",
        bool sticky = false) =>
        new(handle, "a window", "TestClass", "test", 42, false, managed, null, state,
            concealment, workspace, true, "\\\\.\\DISPLAY1", sticky, false, 0);

    private static PaletteAction Find(IReadOnlyList<PaletteAction> actions, string startsWith) =>
        actions.First(a => a.Name.StartsWith(startsWith, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void EveryActionThatActsFocusesTheWindowFirst()
    {
        IReadOnlyList<PaletteAction> actions = PaletteActions.For(Window(handle: 0x1D0076), "2");

        // Sent as one newline-separated message so nothing can move focus between the
        // two halves. "Focus it, then close it" as two round trips is a race that
        // closes whatever happened to be focused instead.
        //
        // Actions that act, which is not all of them: explaining a window asks about
        // it and touches nothing, and focusing it first would be a side effect nobody
        // asked for - moving you to another workspace merely to read a report.
        foreach (PaletteAction action in actions.Where(a => a.Command.Length > 0))
            Assert.StartsWith("focus-window 1900662", action.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyExplainingLeavesTheWindowAlone()
    {
        // Guards the exemption above: if some future action quietly arrived with no
        // command, the loop would stop checking it and nobody would notice.
        //
        // Composing a rule is the other kind of row that sends nothing: it produces
        // text to read and paste rather than anything for the window manager to do, so
        // it carries no command either and must not be mistaken for one that lost it.
        Assert.All(
            PaletteActions.For(Window(), "2").Where(a => a.Command.Length == 0),
            a => Assert.True(
                a.Explains is not null || a.Expands is { Length: > 0 } || a.Children is { Count: > 0 },
                $"'{a.Name}' sends nothing and does nothing else either."));
    }

    [Fact]
    public void GoingToItIsAlwaysOffered()
    {
        Assert.Equal("focus-window 256", Find(PaletteActions.For(Window(), "2"), "Go to it").Command);
    }

    [Fact]
    public void BringingItHereNamesTheFocusedWorkspace()
    {
        PaletteAction bring = Find(PaletteActions.For(Window(workspace: "5"), "2"), "Bring");

        Assert.EndsWith("move --workspace 2", bring.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void BringingItHereIsNotOfferedForAWindowAlreadyHere()
    {
        IReadOnlyList<PaletteAction> actions = PaletteActions.For(Window(workspace: "2"), "2");

        // Offering to move a window to where it already is wastes the most valuable
        // line in the list.
        Assert.DoesNotContain(actions, a => a.Name.StartsWith("Bring", StringComparison.Ordinal));
    }

    [Fact]
    public void BringingItHereIsNotOfferedWhenNothingIsFocused()
    {
        // There is nowhere for "here" to mean. Offering it and failing would be worse
        // than not offering it.
        Assert.DoesNotContain(
            PaletteActions.For(Window(), focusedWorkspace: null),
            a => a.Name.StartsWith("Bring", StringComparison.Ordinal));
    }

    [Fact]
    public void AMinimisedWindowIsOfferedRestoreRatherThanMinimise()
    {
        IReadOnlyList<PaletteAction> actions =
            PaletteActions.For(Window(state: "minimised", concealment: "minimised"), "1");

        Assert.Contains(actions, a => a.Name == "Restore");
        Assert.DoesNotContain(actions, a => a.Name == "Minimise");
    }

    [Fact]
    public void AFloatingWindowIsOfferedTileRatherThanFloat()
    {
        IReadOnlyList<PaletteAction> actions = PaletteActions.For(Window(state: "floating"), "1");

        Assert.Contains(actions, a => a.Name == "Tile it");
        Assert.DoesNotContain(actions, a => a.Name == "Float it");
    }

    [Fact]
    public void FloatingAndTilingSendTheSameToggleWhicheverWayRoundTheRowIsWorded()
    {
        // The wording is a guess about a state that may be out of date; the command
        // must not be. Every route to this row can carry a stale snapshot - a reopened
        // palette seeded from the previous read, a drill-in frame frozen when it was
        // pushed, an action closure holding the window as it was when the row was
        // built - and a verb chosen from a stale state is the verb that is already
        // true. `float` on a window that is already floating returns from
        // SetWindowStateCore without an event or an error, so the key looks dead while
        // Enter on the same row, pressed a moment later once the refresh has landed,
        // works.
        //
        // Minimise and Make sticky beside it have always sent one command each and
        // varied only the label. This is that, and the help screen has always
        // described the chord as a toggle: "float the selected window, or tile it".
        PaletteAction tiled = PaletteActions.For(Window(state: "tiling"), "1")
            .Single(a => a.Name == "Float it");

        PaletteAction floating = PaletteActions.For(Window(state: "floating"), "1")
            .Single(a => a.Name == "Tile it");

        Assert.EndsWith("\ntoggle-floating", tiled.Command, StringComparison.Ordinal);
        Assert.EndsWith("\ntoggle-floating", floating.Command, StringComparison.Ordinal);

        // Same chord either way round, so the lookup cannot lose the row when the
        // wording flips.
        Assert.Equal(tiled.Chord, floating.Chord);
    }

    [Fact]
    public void TheToggleActsOnTheRowsOwnWindowRatherThanWhateverHasFocus()
    {
        // The command is a sequence: focus that window, then toggle. Losing the first
        // half would silently float whichever window happened to be in front.
        PaletteAction action = PaletteActions.For(Window(handle: 4242, state: "tiling"), "1")
            .Single(a => a.Name == "Float it");

        Assert.Equal("focus-window 4242\ntoggle-floating", action.Command);
    }

    [Fact]
    public void AStickyWindowIsOfferedUnstick()
    {
        Assert.Contains(PaletteActions.For(Window(sticky: true), "1"), a => a.Name == "Unstick");
    }

    [Fact]
    public void AnUnmanagedWindowIsNotOfferedTilingActions()
    {
        IReadOnlyList<PaletteAction> actions =
            PaletteActions.For(Window(managed: false, state: null), "1");

        // Floating and sticking are positions within a layout. A window nothing is
        // arranging has no layout to have a position in.
        Assert.DoesNotContain(actions, a => a.Name.Contains("loat", StringComparison.Ordinal));
        Assert.DoesNotContain(actions, a => a.Name.Contains("ticky", StringComparison.Ordinal));

        // But it can be taken on, which is the one thing worth doing to it.
        Assert.Contains(actions, a => a.Name == "Manage it");
    }

    [Fact]
    public void OnlyClosingIsMarkedDestructive()
    {
        IReadOnlyList<PaletteAction> actions = PaletteActions.For(Window(), "2");

        // Closing is the one thing here that cannot be undone, and it is now the only
        // thing marked. Releasing a window used to be marked alongside it and sorted to
        // the bottom with it, which was wrong twice over: `toggle-managed` is a toggle,
        // pressing it again puts the desktop back exactly as it was found, and the mark
        // now means "ask before doing this" rather than merely "sort this last".
        Assert.True(Find(actions, "Close").Destructive);
        Assert.False(Find(actions, "Stop managing").Destructive);
        Assert.False(Find(actions, "Go to").Destructive);
    }

    [Fact]
    public void DestructiveActionsSortLast()
    {
        IReadOnlyList<PaletteEntry> entries =
            PaletteActions.AsEntries(PaletteActions.For(Window(), "2"));

        int close = entries.ToList().FindIndex(e => e.Primary.StartsWith("Close", StringComparison.Ordinal));
        int goTo = entries.ToList().FindIndex(e => e.Primary.StartsWith("Go to", StringComparison.Ordinal));

        // A mistaken Enter lands at the top of a list, so what sits there matters.
        Assert.True(close > goTo);
        Assert.True(entries[close].Rank < entries[goTo].Rank);
    }

    [Fact]
    public void ChordsAreShownOnTheRowSoTheyCanBeLearned()
    {
        IReadOnlyList<PaletteEntry> entries =
            PaletteActions.AsEntries(PaletteActions.For(Window(), "2"));

        // The list is where the chords are written down. Turning the guard off is only
        // useful to somebody who knows what the chords are.
        Assert.Contains(entries, e => e.Badges.Contains("Ctrl+Shift+W"));
        Assert.Contains(entries, e => e.Badges.Contains("Alt+Enter"));
    }

    [Fact]
    public void NoTwoActionsShareAChord()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (WindowCandidate window in new[]
        {
            Window(),
            Window(state: "floating"),
            Window(state: "minimised", concealment: "minimised"),
            Window(sticky: true),
            Window(managed: false, state: null),
        })
        {
            seen.Clear();

            foreach (PaletteAction action in PaletteActions.For(window, "9"))
            {
                if (action.Chord is not { } chord) continue;

                Assert.True(seen.Add(chord), $"{chord} is claimed twice for the same window");
            }
        }
    }

    [Fact]
    public void WindowRowsCarryTheirActions()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window()], includeUnmanaged: true, focusedWorkspace: "2"));

        Assert.True(entry.HasActions);
        Assert.NotEmpty(entry.ResolveActions());
    }

    [Fact]
    public void RowsThatAreOnlyEverChosenHaveNoActions()
    {
        PaletteEntry layout = Assert.Single(PaletteEntries.ForLayouts(["fibonacci"]));

        // Nothing sensible can be done *to* a layout, and offering an empty action
        // list would advertise a key that then does nothing.
        Assert.True(!layout.HasActions);
    }

    // ---- the tag picker ----------------------------------------------------

    private static readonly string[] Spaces = ["1", "2", "3", "code"];

    private static IReadOnlyList<PaletteAction> Picker(WindowCandidate window) =>
        PaletteActions.For(window, "1", Spaces)
            .First(a => a.Name.StartsWith("Tags", StringComparison.Ordinal))
            .Children!;

    private static WindowCandidate Tagged(string workspace, params string[] tags) =>
        new(0x900, "a window", "TestClass", "test", 42, false, true, null, "tiling",
            "none", workspace, true, "\\\\.\\DISPLAY1", false, false, 0, null, tags);

    [Fact]
    public void TheTagPickerOpensRatherThanRunning()
    {
        PaletteAction tags = PaletteActions.For(Window(), "1", Spaces)
            .First(a => a.Name.StartsWith("Tags", StringComparison.Ordinal));

        Assert.Equal(string.Empty, tags.Command);
        Assert.NotNull(tags.Children);
        Assert.Equal(Spaces.Length, tags.Children!.Count);
    }

    [Fact]
    public void ItIsNotOfferedWithoutAWorkspaceList()
    {
        // A picker with nothing in it is worse than no picker: it advertises a way to
        // do something and then cannot.
        Assert.DoesNotContain(
            PaletteActions.For(Window(), "1"),
            a => a.Name.StartsWith("Tags", StringComparison.Ordinal));
    }

    [Fact]
    public void ItIsNotOfferedForAWindowNobodyManages()
    {
        // Tags are membership of workspaces, and a window nothing is arranging is not
        // a member of anything.
        Assert.DoesNotContain(
            PaletteActions.For(Window(managed: false, state: null), "1", Spaces),
            a => a.Name.StartsWith("Tags", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUntaggedWorkspaceOffersToTagIt()
    {
        PaletteAction choice = Picker(Tagged("3")).First(c => c.Name == "2");

        Assert.Equal("focus-window 2304\ntag --toggle 2", choice.Command);
        Assert.Equal("not tagged - Enter adds it", choice.Description);
    }

    [Fact]
    public void ATaggedWorkspaceOffersToRemoveIt()
    {
        PaletteAction choice = Picker(Tagged("3", "3", "2")).First(c => c.Name == "2");

        // The command is the same toggle either way; only the wording changes, so the
        // row says what Enter will do rather than leaving it to be worked out.
        Assert.Equal("focus-window 2304\ntag --toggle 2", choice.Command);
        Assert.Contains("removes it", choice.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkspaceItIsOnIsShownButCannotBeChosen()
    {
        PaletteAction choice = Picker(Tagged("3")).First(c => c.Name == "3");

        // The window manager refuses that tag outright - it would be a membership
        // relocation could never satisfy - so offering it would be offering something
        // certain to be rejected.
        Assert.Equal(string.Empty, choice.Command);
        Assert.Equal("it is here", choice.Description);
    }

    [Fact]
    public void EveryWorkspaceIsListedIncludingTheCurrentOne()
    {
        // Leaving it out of an otherwise complete list reads as a bug rather than as
        // a rule.
        Assert.Equal(Spaces, Picker(Tagged("3")).Select(c => c.Name));
    }

    [Fact]
    public void ChoicesSurviveBeingTurnedIntoRows()
    {
        IReadOnlyList<PaletteEntry> rows =
            PaletteActions.AsEntries(PaletteActions.For(Tagged("3"), "1", Spaces));

        PaletteEntry tags = rows.First(r => r.Primary.StartsWith("Tags", StringComparison.Ordinal));

        // Carried on the row so the window can push them as the next level, and
        // badged so Enter on it is visibly different from Enter on its neighbours.
        Assert.True(tags.HasActions);
        Assert.Equal(Spaces.Length, tags.ResolveActions().Count);
        Assert.Contains(tags.Badges, b => b.Contains('\u203A'));
    }

    [Fact]
    public void ClearingStaysAtTheTopLevel()
    {
        IReadOnlyList<PaletteAction> actions = PaletteActions.For(Tagged("3", "3", "2"), "1", Spaces);

        // The emergency exit for a window that is moving on its own. Burying it inside
        // the picker would undo the point of having it.
        Assert.Contains(actions, a => a.Name.StartsWith("Stop it", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTagPickerHasNoChord()
    {
        PaletteAction tags = PaletteActions.For(Window(), "1", Spaces)
            .First(a => a.Name.StartsWith("Tags", StringComparison.Ordinal));

        // A chord that produces another list to choose from is an odd pairing.
        Assert.Null(tags.Chord);
    }

    [Fact]
    public void TheWorkspaceListReachesTheRowsThroughForWindows()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows(
            [Window()], includeUnmanaged: true, focusedWorkspace: "1", workspaces: Spaces));

        // Tested through the path the palette actually uses rather than by calling
        // PaletteActions.For directly. It was not, and the wiring was wrong: ForWindows
        // grew the parameter and went on passing nothing, which compiled without a
        // murmur because the argument is optional with a null default. Every unit test
        // passed and the picker was simply absent on screen.
        Assert.Contains(entry.ResolveActions(), a => a.Name.StartsWith("Tags", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWorkspaceListReachesScratchpadRowsToo()
    {
        WindowCandidate stashed = new(0x901, "a window", "TestClass", "test", 42, false, true,
            null, "tiling", "cloaked", "3", false, "\\\\.\\DISPLAY1", false, false, 0, "notes", null);

        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForScratchpad([stashed], "1", Spaces));

        Assert.Contains(entry.ResolveActions(), a => a.Name.StartsWith("Tags", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing offered for a stashed window may reach it by focusing it.
    /// </summary>
    /// <remarks>
    /// A stashed window is cloaked. Focusing a cloaked window reveals it without
    /// taking it out of the scratchpad, so it conceals itself again at the next layout
    /// pass - which looks like the palette having done nothing. Summoning by slot is
    /// the only way in, and it focuses the window itself, so it replaces the focus
    /// prefix rather than preceding it.
    ///
    /// Every action is built on that prefix, so this was never only about "Go to it":
    /// closing, tagging and un-managing a stashed window were all aimed at a window
    /// about to vanish.
    /// </remarks>
    [Fact]
    public void NoActionOnAStashedWindowFocusesIt()
    {
        WindowCandidate stashed = new(0x901, "a window", "TestClass", "test", 42, false, true,
            null, "tiling", "cloaked", "__scratchpad", false, "\\\\.\\DISPLAY1", false, false, 0, "notes", null);

        IReadOnlyList<PaletteAction> actions = PaletteActions.For(stashed, "1", Spaces);

        Assert.NotEmpty(actions);

        foreach (PaletteAction action in Flatten(actions))
        {
            Assert.DoesNotContain("focus-window", action.Command, StringComparison.Ordinal);

            if (action.Command.Length > 0)
                Assert.StartsWith("scratchpad notes", action.Command, StringComparison.Ordinal);
        }
    }

    /// <summary>An ordinary window is still reached by focusing it.</summary>
    [Fact]
    public void AnOrdinaryWindowIsStillFocused()
    {
        WindowCandidate ordinary = new(0x902, "a window", "TestClass", "test", 42, false, true,
            null, "tiling", "none", "3", true, "\\\\.\\DISPLAY1", false, false, 0, null, null);

        IReadOnlyList<PaletteAction> actions = PaletteActions.For(ordinary, "1", Spaces);

        Assert.Contains(actions, a => a.Name == "Go to it");
        Assert.DoesNotContain(actions, a => a.Name == "Summon it");
        Assert.Contains(Flatten(actions), a => a.Command.Contains("focus-window", StringComparison.Ordinal));
    }

    /// <summary>
    /// Summoning already lands the window where you are, so "Bring it here" would be
    /// the same action twice - the second time as a move that cannot move anything.
    /// </summary>
    [Fact]
    public void AStashedWindowIsNotAlsoOfferedBringItHere()
    {
        WindowCandidate stashed = new(0x901, "a window", "TestClass", "test", 42, false, true,
            null, "tiling", "cloaked", "__scratchpad", false, "\\\\.\\DISPLAY1", false, false, 0, "notes", null);

        IReadOnlyList<PaletteAction> actions = PaletteActions.For(stashed, "1", Spaces);

        Assert.Contains(actions, a => a.Name == "Summon it");
        Assert.DoesNotContain(actions, a => a.Name == "Bring it here");
    }

    /// <summary>Actions and their children, as one sequence.</summary>
    private static IEnumerable<PaletteAction> Flatten(IEnumerable<PaletteAction> actions)
    {
        foreach (PaletteAction action in actions)
        {
            yield return action;

            if (action.Children is { Count: > 0 } children)
                foreach (PaletteAction child in Flatten(children))
                    yield return child;
        }
    }

    [Fact]
    public void EveryWindowCanBeExplained()
    {
        // The one action that does nothing to the window. Until now this report was
        // reachable only from a shell, which is the wrong place: by the time you are
        // asking why a window behaves oddly, you are looking at it.
        PaletteAction explain = Assert.Single(
            PaletteActions.For(Window(handle: 0x1D0076), "1"),
            a => a.Explains is not null);

        Assert.Equal(0x1D0076, explain.Explains);
        Assert.Equal(string.Empty, explain.Command);
    }

    [Fact]
    public void ExplainingIsOfferedLastAndIsNotDestructive()
    {
        // Last because it is the one thing in the list that changes nothing, and not
        // destructive because there is nothing to warn about.
        IReadOnlyList<PaletteAction> actions = PaletteActions.For(Window(), "1");

        Assert.Equal(actions.Count - 1, actions.ToList().FindIndex(a => a.Explains is not null));
        Assert.False(actions[^1].Destructive);
    }

    [Fact]
    public void InspectingIsFindableByTheNameOfTheCommandThatProducesIt()
    {
        // It was called "Explain this window", which described it better and was
        // findable only by somebody who had already found it. Anybody who knows
        // `shubbak inspect` exists and wants it here searches for "inspect".
        PaletteAction inspect = Assert.Single(
            PaletteActions.For(Window(), "1"),
            a => a.Explains is not null);

        Assert.Contains("Inspect", inspect.Name, StringComparison.Ordinal);

        // Both vocabularies hit: the description keeps the old wording, and the
        // description is searched as well as the name.
        Assert.Contains("Explain", inspect.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectingHasAChordOfItsOwn()
    {
        // Three layers of "you had to already know" - no chord, not a mode, and last
        // in a list reached by a key that is itself undocumented. This is the first of
        // those undone; the palette lets this one chord through even when the action
        // guard is on, because it is the only action that takes no action.
        PaletteAction inspect = Assert.Single(
            PaletteActions.For(Window(), "1"),
            a => a.Explains is not null);

        Assert.Equal("Ctrl+Shift+I", inspect.Chord);
    }

    [Fact]
    public void AnExplainActionSurvivesBecomingARow()
    {
        // The handle has to reach the window, which is what actually sends the
        // request. Dropping it in the conversion would leave a row that looks right
        // and explains nothing.
        PaletteEntry row = Assert.Single(
            PaletteActions.AsEntries(PaletteActions.For(Window(handle: 0x2A), "1")),
            e => e.Explains is not null);

        Assert.Equal(0x2A, row.Explains);
    }

    [Fact]
    public void AChordSurvivesBecomingARow()
    {
        // It used to reach the row only as a badge - a caption saying which key would
        // work, on a row that could not be found by that key. So the list printed
        // Ctrl+Shift+S beside "Make sticky" and pressing it there did nothing.
        IReadOnlyList<PaletteEntry> rows =
            PaletteActions.AsEntries(PaletteActions.For(Window(), "1"));

        PaletteEntry sticky = Assert.Single(rows, e => e.Primary == "Make sticky");

        Assert.Equal("Ctrl+Shift+S", sticky.Chord);
    }

    [Fact]
    public void EveryChordedActionIsFindableByItsBadge()
    {
        // The two have to agree, because the badge is what a user reads and the chord
        // is what the key looks for. Held together here rather than by inspection.
        IReadOnlyList<PaletteEntry> rows =
            PaletteActions.AsEntries(PaletteActions.For(Window(), "9"));

        foreach (PaletteEntry row in rows.Where(e => e.Chord is not null))
            Assert.Contains(row.Chord!, row.Badges);
    }

    [Fact]
    public void ARowWithNoChordAdvertisesNone()
    {
        // "Go to it" is what Enter already does, and a badge there would be a key that
        // does not exist.
        IReadOnlyList<PaletteEntry> rows =
            PaletteActions.AsEntries(PaletteActions.For(Window(), "1"));

        Assert.Null(Assert.Single(rows, e => e.Primary == "Go to it").Chord);
    }
}