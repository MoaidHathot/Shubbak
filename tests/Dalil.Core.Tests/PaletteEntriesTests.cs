using Dalil.Core;
using Shubbak.Ipc;

namespace Dalil.Core.Tests;

/// <summary>
/// Turning what the window manager reports into rows a person can read.
/// </summary>
/// <remarks>
/// Judgements rather than plumbing, which is why they are tested apart from the
/// model: what a window with no title is called, which states earn a badge and which
/// are ordinary, and what pressing Enter on a row actually does.
/// </remarks>
public sealed class PaletteEntriesTests
{
    private static WindowCandidate Window(
        long handle = 0x100,
        string title = "a window",
        string className = "TestClass",
        string process = "test",
        bool managed = true,
        string? reason = null,
        string? state = "tiling",
        string concealment = "none",
        string? workspace = "1",
        bool sticky = false,
        bool elevated = false,
        long focusSequence = 0) =>
        new(handle, title, className, process, 42, elevated, managed, reason, state,
            concealment, workspace, true, "\\\\.\\DISPLAY1", sticky, false, focusSequence);

    [Fact]
    public void EnterOnAWindowFocusesItByHandle()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Window(handle: 0x1D0076)]));

        // One command covers both cases. For a managed window the daemon switches
        // workspace and raises it; for one the tree has never heard of it falls
        // through to revealing - uncloaking, restoring and foregrounding.
        Assert.Equal("focus-window 1900662", entry.Command);
    }

    [Fact]
    public void RecencyBecomesTheRowsRank()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Window(focusSequence: 17)]));

        Assert.Equal(17, entry.Rank);
    }

    [Fact]
    public void AWindowWithNoTitleIsNamedByItsClass()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window(title: "", className: "Ghost")]));

        // An untitled window may be exactly the one that has gone wrong. A blank row
        // would be unsearchable and unselectable, which is the opposite of useful.
        Assert.Equal("(Ghost)", entry.Primary);
    }

    [Fact]
    public void TheProcessAndWorkspaceAreSearchableToo()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window(title: "Untitled document", process: "notepad", workspace: "3")]));

        // Finding a window by its application when the title says nothing about it is
        // most of what this is for.
        Assert.Contains("notepad", entry.Secondary, StringComparison.Ordinal);
        Assert.Contains("3", entry.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmanagedWindowSaysSo()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window(managed: false, reason: "window is elevated", state: null)]));

        Assert.Contains("unmanaged", entry.Badges);
    }

    [Fact]
    public void UnmanagedWindowsCanBeLeftOut()
    {
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForWindows(
            [Window(handle: 1), Window(handle: 2, managed: false, state: null)],
            includeUnmanaged: false);

        Assert.Single(entries);
    }

    [Fact]
    public void ShubbaksOwnConcealmentIsNotWorthABadge()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window(managed: true, concealment: "cloaked", workspace: "7")]));

        // Every managed window on an inactive workspace is cloaked, because that is
        // how Shubbak conceals them. Badging all of them would mark most of the list
        // and mean nothing - and "on workspace 7" has already said where it is.
        Assert.DoesNotContain("cloaked", entry.Badges);
    }

    [Fact]
    public void ConcealmentByAnyoneElseIsWorthABadge()
    {
        PaletteEntry cloaked = Assert.Single(
            PaletteEntries.ForWindows([Window(managed: false, concealment: "cloaked", state: null)]));

        PaletteEntry hidden = Assert.Single(
            PaletteEntries.ForWindows([Window(concealment: "hidden")]));

        PaletteEntry minimised = Assert.Single(
            PaletteEntries.ForWindows([Window(concealment: "minimised", state: "minimised")]));

        Assert.Contains("cloaked", cloaked.Badges);

        // The one a user cannot undo without help: a window left hidden by a window
        // manager that died is invisible to Alt+Tab and to the taskbar both.
        Assert.Contains("hidden", hidden.Badges);
        Assert.Contains("minimised", minimised.Badges);
    }

    [Fact]
    public void StickyAndElevatedAreShown()
    {
        PaletteEntry entry = Assert.Single(
            PaletteEntries.ForWindows([Window(sticky: true, elevated: true)]));

        Assert.Contains("sticky", entry.Badges);
        Assert.Contains("elevated", entry.Badges);
    }

    [Fact]
    public void TilingIsTheOrdinaryStateAndEarnsNothing()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Window(state: "tiling")]));

        Assert.Empty(entry.Badges);
    }

    // ---- commands -------------------------------------------------------------

    [Fact]
    public void ACommandWithNoArgumentsRunsWhenChosen()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForCommands(
            [new CommandInfo("wm-toggle-pause", "Suspend tiling, or resume it", [], [])]));

        Assert.Equal("wm-toggle-pause", entry.Command);
    }

    [Fact]
    public void ACommandThatNeedsArgumentsIsOfferedRatherThanRun()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForCommands(
            [new CommandInfo("focus-window", "Focus a window by handle", ["windowhandle"], [])]));

        // An empty command means "put this in the search box to finish". Running it
        // bare would be refused by the parser and read as a broken palette.
        Assert.Equal(string.Empty, entry.Command);
        Assert.Contains("<windowhandle>", entry.Badges);
    }

    // ---- workspaces and layouts -------------------------------------------------

    [Fact]
    public void AWorkspaceIsChosenByName()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWorkspaces(
            [new WorkspaceInfo(1, "3", "code", true, true, "m", "splith", 2, 0, 0, true)]));

        Assert.Equal("focus --workspace 3", entry.Command);

        // Shown by its label, searched by it too: someone who named a workspace
        // "code" will type "code", not "3".
        Assert.Equal("code", entry.Primary);
        Assert.Contains("focused", entry.Badges);
    }

    [Fact]
    public void BusyWorkspacesComeFirst()
    {
        IReadOnlyList<PaletteEntry> entries = PaletteEntries.ForWorkspaces(
        [
            new WorkspaceInfo(1, "1", "", false, false, "m", "splith", 0, 0, 0),
            new WorkspaceInfo(2, "2", "", false, true, "m", "splith", 5, 0, 0),
        ]);

        // An empty workspace is somewhere to go, not something to find.
        Assert.True(entries[1].Rank > entries[0].Rank);
    }

    [Fact]
    public void ALayoutIsSetWhenChosen()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForLayouts(["fibonacci"]));

        Assert.Equal("layout --set fibonacci", entry.Command);
    }

    // ---- membership --------------------------------------------------------

    private static WindowCandidate Tagged(string workspace, params string[] tags) =>
        new(0x900, "a window", "TestClass", "test", 42, false, true, null, "tiling",
            "none", workspace, true, "\\\\.\\DISPLAY1", false, false, 0, null, tags);

    [Fact]
    public void ATaggedWindowSaysWhereItWillFollowYou()
    {
        // Tagging records complete membership, so the set always contains the
        // workspace the window is already on - see WindowManager.Tag, which explains
        // why that is needed for it to be able to come back.
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Tagged("3", "3", "-")]));

        Assert.Contains("also on -", entry.Badges);
    }

    [Fact]
    public void TheWorkspaceItIsAlreadyOnIsNotListed()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Tagged("3", "3", "-")]));

        // "also on 3, -" for a window sitting on 3 is the noisier half of the answer.
        Assert.DoesNotContain(entry.Badges, b => b.Contains('3'));
    }

    [Fact]
    public void AWindowTaggedOnlyToItsOwnWorkspaceFollowsNowhere()
    {
        Assert.Empty(PaletteEntries.FollowsTo(Tagged("3", "3")));

        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Tagged("3", "3")]));
        Assert.DoesNotContain(entry.Badges, b => b.StartsWith("also on", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUntaggedWindowSaysNothingAboutFollowing()
    {
        PaletteEntry entry = Assert.Single(PaletteEntries.ForWindows([Window()]));

        Assert.DoesNotContain(entry.Badges, b => b.StartsWith("also on", StringComparison.Ordinal));
    }

    [Fact]
    public void ATaggedWindowIsOfferedAWayToStopIt()
    {
        IReadOnlyList<PaletteAction> actions = PaletteActions.For(Tagged("3", "3", "-"), "3");

        // The escape hatch was previously discoverable only by reading your own
        // configuration, which is not where somebody looks when a window is moving on
        // its own.
        PaletteAction stop = Assert.Single(actions, a => a.Name.StartsWith("Stop it", StringComparison.Ordinal));

        Assert.EndsWith("tag --clear", stop.Command, StringComparison.Ordinal);
        Assert.Contains("-", stop.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUntaggedWindowIsNotOfferedIt()
    {
        // Offering to clear tags that do not exist advertises a problem the user does
        // not have.
        Assert.DoesNotContain(
            PaletteActions.For(Window(), "1"),
            a => a.Name.StartsWith("Stop it", StringComparison.Ordinal));
    }
}