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
    public void EveryActionFocusesTheWindowFirst()
    {
        IReadOnlyList<PaletteAction> actions = PaletteActions.For(Window(handle: 0x1D0076), "2");

        // Sent as one newline-separated message so nothing can move focus between the
        // two halves. "Focus it, then close it" as two round trips is a race that
        // closes whatever happened to be focused instead.
        foreach (PaletteAction action in actions)
            Assert.StartsWith("focus-window 1900662", action.Command, StringComparison.Ordinal);
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
    public void ClosingAndReleasingAreMarkedDestructive()
    {
        IReadOnlyList<PaletteAction> actions = PaletteActions.For(Window(), "2");

        Assert.True(Find(actions, "Close").Destructive);
        Assert.True(Find(actions, "Stop managing").Destructive);
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

        Assert.NotNull(entry.Actions);
        Assert.NotEmpty(entry.Actions);
    }

    [Fact]
    public void RowsThatAreOnlyEverChosenHaveNoActions()
    {
        PaletteEntry layout = Assert.Single(PaletteEntries.ForLayouts(["fibonacci"]));

        // Nothing sensible can be done *to* a layout, and offering an empty action
        // list would advertise a key that then does nothing.
        Assert.True(layout.Actions is null || layout.Actions.Count == 0);
    }
}
