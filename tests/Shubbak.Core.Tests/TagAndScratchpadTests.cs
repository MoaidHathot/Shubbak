using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for workspace tags and the scratchpad.
/// </summary>
/// <remarks>
/// The tag model is worth being precise about. A Windows window has one position on
/// one monitor and physically cannot be drawn twice, so "member of several
/// workspaces" means the window <i>relocates</i> to whichever tagged workspace was
/// most recently activated. These tests pin that behaviour, including the cases
/// where the alternative reading would be tempting.
/// </remarks>
public sealed class TagAndScratchpadTests
{
    // ---- tags --------------------------------------------------------------

    [Fact]
    public void TaggingMakesAWindowFollowToAnotherWorkspace()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode chat = wm.Open("chat");

        wm.Tag("2", TagMode.Add);
        wm.FocusWorkspace("2");

        // The window came along, rather than staying behind on workspace 1.
        Assert.Equal("2", chat.Workspace!.Name);
        Assert.True(chat.Workspace.IsActive);
    }

    [Fact]
    public void ATaggedWindowFollowsBackAndForth()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode chat = wm.Open("chat");
        wm.Tag("2", TagMode.Add);

        wm.FocusWorkspace("2");
        Assert.Equal("2", chat.Workspace!.Name);

        wm.FocusWorkspace("1");
        Assert.Equal("1", chat.Workspace!.Name);

        wm.FocusWorkspace("2");
        Assert.Equal("2", chat.Workspace!.Name);
    }

    [Fact]
    public void UntaggedWindowsStayWhereTheyAre()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode stays = wm.Open("stays");
        WindowNode follows = wm.Open("follows");

        wm.FocusWindow(follows);
        wm.Tag("2", TagMode.Add);

        wm.FocusWorkspace("2");

        Assert.Equal("1", stays.Workspace!.Name);
        Assert.Equal("2", follows.Workspace!.Name);
    }

    [Fact]
    public void TaggingRecordsCompleteMembershipIncludingTheCurrentWorkspace()
    {
        // The tag set names every workspace the window belongs to, not just the
        // newly added one. Without the current workspace in the set the
        // relationship would be one-way: the window would follow to the new
        // workspace and then have no tag for the one it came from.
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        wm.Open("chat");

        WmResult result = wm.Tag("2", TagMode.Add);
        WindowTagsChanged evt = result.Single<WindowTagsChanged>();

        Assert.Equal(["1", "2"], evt.Tags.Order(StringComparer.Ordinal));
        Assert.False(evt.IsSticky);
    }

    [Fact]
    public void ToggleAddsThenRemoves()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode chat = wm.Open("chat");

        wm.Tag("2", TagMode.Toggle);
        Assert.Contains("2", chat.Tags);

        wm.Tag("2", TagMode.Toggle);
        Assert.DoesNotContain("2", chat.Tags);
    }

    [Fact]
    public void TaggingToTheWindowsOwnWorkspaceIsRejected()
    {
        // The tag could never be satisfied by relocation, so it would sit in the
        // window's tag set doing nothing - which is worse than being told.
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        wm.Open("chat");

        WmResult result = wm.Tag("1", TagMode.Add);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public void AWindowCanBeTaggedToSeveralWorkspaces()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2", "3"]);
        WindowNode notes = wm.Open("notes");

        wm.Tag("2", TagMode.Add);
        wm.Tag("3", TagMode.Add);

        // Membership is 1, 2 and 3 - the workspace it started on plus both targets.
        Assert.Equal(["1", "2", "3"], notes.Tags.Order(StringComparer.Ordinal));

        wm.FocusWorkspace("3");
        Assert.Equal("3", notes.Workspace!.Name);

        wm.FocusWorkspace("2");
        Assert.Equal("2", notes.Workspace!.Name);

        wm.FocusWorkspace("1");
        Assert.Equal("1", notes.Workspace!.Name);
    }

    [Fact]
    public void RemovingATagStopsTheWindowFollowing()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode chat = wm.Open("chat");

        wm.Tag("2", TagMode.Add);
        wm.Tag("2", TagMode.Remove);

        wm.FocusWorkspace("2");

        Assert.Equal("1", chat.Workspace!.Name);
    }

    [Fact]
    public void ClearingTagsAlsoClearsSticky()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode chat = wm.Open("chat");

        wm.Tag("2", TagMode.Add);
        wm.ToggleSticky();

        Assert.True(chat.IsSticky);
        Assert.NotEmpty(chat.Tags);

        wm.ClearTags();

        Assert.False(chat.IsSticky);
        Assert.Empty(chat.Tags);
    }

    [Fact]
    public void StickyWindowsFollowEveryWorkspaceOnTheirMonitor()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2", "3"]);
        WindowNode notes = wm.Open("notes");

        wm.ToggleSticky();

        // Including workspaces created on demand after the flag was set, which is
        // exactly why sticky is a flag rather than a tag for each workspace.
        wm.FocusWorkspace("2");
        Assert.Equal("2", notes.Workspace!.Name);

        wm.FocusWorkspace("9");
        Assert.Equal("9", notes.Workspace!.Name);
    }

    [Fact]
    public void StickyDoesNotCrossMonitors()
    {
        // A window can only be on one monitor, and dragging it to another every time
        // focus moved would be actively hostile.
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: "1");
        MonitorNode second = wm.Root.Monitors[1];
        wm.AddWorkspace(new WorkspaceNode("2"), second);

        WindowNode notes = wm.Open("notes");
        wm.ToggleSticky();

        wm.ActivateWorkspace(second.Workspaces[0]);

        Assert.Same(wm.Root.Monitors[0], notes.Monitor);
    }

    [Fact]
    public void TaggedWindowsKeepTheirWorkspaceAliveUntilTheyLeave()
    {
        // The source workspace is reaped only if it is transient and empty; a tagged
        // window leaving is what empties it.
        WindowManager wm = WmFixture.Create(workspaceNames: "1");

        wm.FocusWorkspace("7");
        WindowNode temp = wm.Open("temp");
        wm.Tag("1", TagMode.Add);

        Assert.NotNull(wm.Root.FindWorkspace("7"));

        wm.FocusWorkspace("1");

        // The window followed, so the transient workspace it left is gone.
        Assert.Equal("1", temp.Workspace!.Name);
        Assert.Null(wm.Root.FindWorkspace("7"));
    }

    [Fact]
    public void ClosingATaggedWindowRemovesItEverywhere()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode chat = wm.Open("chat");
        wm.Tag("2", TagMode.Add);

        wm.UnmanageWindow(chat);
        wm.FocusWorkspace("2");

        Assert.Empty(wm.Root.DescendantWindows());
    }

    // ---- scratchpad --------------------------------------------------------

    [Fact]
    public void StashingHidesTheWindowAndMovesFocusOn()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: "1");
        WindowNode notes = wm.Open("notes");
        WindowNode other = wm.Open("other");

        wm.FocusWindow(notes);
        WmResult result = wm.ToggleScratchpad("notes");

        Assert.True(result.Succeeded);
        Assert.Equal(WindowManager.ScratchpadWorkspace, notes.Workspace!.Name);
        Assert.Same(other, wm.FocusedWindow);
    }

    [Fact]
    public void SummoningBringsTheWindowBackAndFocusesIt()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: "1");
        WindowNode notes = wm.Open("notes");
        wm.Open("other");

        wm.FocusWindow(notes);
        wm.ToggleScratchpad("notes");
        wm.ToggleScratchpad("notes");

        Assert.Equal("1", notes.Workspace!.Name);
        Assert.Same(notes, wm.FocusedWindow);

        // Summoned windows float, so they overlay the layout rather than
        // rearranging it - which is the point of a scratchpad.
        Assert.Equal(WindowState.Floating, notes.State);
    }

    [Fact]
    public void ScratchpadSlotsAreIndependent()
    {
        // A single unnamed scratchpad becomes a junk drawer the moment it holds more
        // than one thing.
        WindowManager wm = WmFixture.Create(workspaceNames: "1");
        WindowNode notes = wm.Open("notes");
        WindowNode terminal = wm.Open("terminal");

        wm.FocusWindow(notes);
        wm.ToggleScratchpad("notes");

        wm.FocusWindow(terminal);
        wm.ToggleScratchpad("term");

        Assert.Equal(2, wm.ScratchpadContents().Count());

        wm.ToggleScratchpad("term");

        Assert.Same(terminal, wm.FocusedWindow);
        Assert.Equal(WindowManager.ScratchpadWorkspace, notes.Workspace!.Name);
    }

    [Fact]
    public void SummoningBringsTheWindowToWhicheverWorkspaceIsActive()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode notes = wm.Open("notes");

        wm.ToggleScratchpad("notes");
        wm.FocusWorkspace("2");
        wm.ToggleScratchpad("notes");

        Assert.Equal("2", notes.Workspace!.Name);
    }

    [Fact]
    public void TheScratchpadCannotBeActivated()
    {
        // Activating it would display every stashed window at once, which is the
        // opposite of what stashing them was for.
        WindowManager wm = WmFixture.Create(workspaceNames: "1");
        wm.Open("notes");
        wm.ToggleScratchpad("notes");

        WmResult result = wm.FocusWorkspace(WindowManager.ScratchpadWorkspace);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public void StashingWithNothingFocusedIsRejected()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: "1");

        WmResult result = wm.ToggleScratchpad("notes");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public void StashedWindowsAreNotArrangedIntoTheVisibleLayout()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: "1");
        WindowNode notes = wm.Open("notes");
        WindowNode other = wm.Open("other");

        wm.FocusWindow(notes);
        wm.ToggleScratchpad("notes");

        // The remaining window takes the whole workspace.
        Assert.Equal(new Geometry.Rect(0, 0, 1920, 1080), wm.RectOf(other));

        // And the stashed one is not shown.
        Assert.DoesNotContain(
            wm.ComputePlacements(),
            p => p.Window == notes && p.Visible);
    }
}
