using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Workspaces going home: to the monitor the configuration bound them to, when it is
/// attached, by whatever name the configuration used.
/// </summary>
/// <remarks>
/// <para>
/// Removing a monitor migrates its workspaces to a survivor and has for a long time.
/// Adding it back did nothing: a workspace's preference was consulted when it was
/// created and never again, so every dock and undock ended with a round of moving
/// workspaces back by hand. These pin the other half.
/// </para>
/// <para>
/// The state machine never reads the configuration. A name in <c>monitor=</c> reaches
/// it as <see cref="WorkspaceNode.PreferredMonitorName"/>, and the names a display
/// answers to reach it as <see cref="MonitorNode.Names"/>; the host writes both. So
/// these tests write both too, and the resolution they exercise is the whole of what
/// the state machine does with them.
/// </para>
/// </remarks>
public sealed class WorkspaceRehomingTests
{
    private static WindowManager TwoMonitors(out MonitorNode left, out MonitorNode right)
    {
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: "1");
        left = wm.Root.Monitors[0];
        right = wm.Root.Monitors[1];
        return wm;
    }

    // ---- by position ----------------------------------------------------------

    [Fact]
    public void AWorkspaceBoundByPositionGoesBackWhenItsMonitorReturns()
    {
        WindowManager wm = TwoMonitors(out MonitorNode left, out MonitorNode right);

        var bound = new WorkspaceNode("2") { PreferredMonitorIndex = 1 };
        wm.AddWorkspace(bound);
        Assert.Same(right, bound.Monitor);

        // Undock: the workspace is pushed onto the survivor.
        wm.RemoveMonitor(right);
        Assert.Same(left, bound.Monitor);

        // Dock: the same panel comes back, and nothing in the tree remembers it.
        var returned = TreeBuilder.Monitor("\\\\.\\DISPLAY2", x: 1920);
        wm.AddMonitor(returned);

        WmResult result = wm.RehomeWorkspaces();

        Assert.True(result.Succeeded);
        Assert.Same(returned, bound.Monitor);

        WorkspaceMoved moved = result.Single<WorkspaceMoved>();
        Assert.Same(left, moved.From);
        Assert.Same(returned, moved.To);
    }

    [Fact]
    public void AWorkspaceAlreadyAtHomeIsLeftAloneAndProducesNoEvents()
    {
        WindowManager wm = TwoMonitors(out _, out MonitorNode right);

        var bound = new WorkspaceNode("2") { PreferredMonitorIndex = 1 };
        wm.AddWorkspace(bound);

        WmResult result = wm.RehomeWorkspaces();

        // Cheap to run on every reconciliation, because the common case is that
        // everything is where it should be.
        Assert.True(result.Succeeded);
        Assert.Empty(result.Events);
        Assert.Same(right, bound.Monitor);
    }

    [Fact]
    public void AWorkspaceWithNoOpinionStaysWhereItIs()
    {
        WindowManager wm = TwoMonitors(out MonitorNode left, out MonitorNode right);

        var free = new WorkspaceNode("2");
        wm.AddWorkspace(free, right);

        WmResult result = wm.RehomeWorkspaces();

        Assert.Empty(result.Events);
        Assert.Same(right, free.Monitor);
        Assert.NotSame(left, free.Monitor);
    }

    [Fact]
    public void AWorkspaceWhoseHomeIsNotAttachedStaysWhereItIs()
    {
        WindowManager wm = WmFixture.Create(monitors: 1, workspaceNames: "1");
        MonitorNode only = wm.Root.Monitors[0];

        // Bound to a third display this machine does not have right now - the laptop
        // away from its dock.
        var bound = new WorkspaceNode("2") { PreferredMonitorIndex = 2 };
        wm.AddWorkspace(bound);

        Assert.Same(only, bound.Monitor);
        Assert.Empty(wm.RehomeWorkspaces().Events);
    }

    // ---- by name --------------------------------------------------------------

    [Fact]
    public void AWorkspaceBoundByNameFollowsThePanelNotThePosition()
    {
        // The whole point of a name. After a replug Windows may hand the same panel a
        // different position, so a workspace bound to "the second display" lands on
        // the wrong one and a workspace bound to "the Dell" does not.
        WindowManager wm = TwoMonitors(out MonitorNode left, out MonitorNode right);

        right.Names = ["dell"];

        var bound = new WorkspaceNode("2") { PreferredMonitorName = "dell" };
        wm.AddWorkspace(bound);
        Assert.Same(right, bound.Monitor);

        wm.RemoveMonitor(right);
        Assert.Same(left, bound.Monitor);

        // Comes back at position 1 again, but as a different GDI name - which is what
        // a replug can do - and the host names it from its device path.
        var returned = TreeBuilder.Monitor("\\\\.\\DISPLAY3", x: 1920);
        returned.Names = ["dell"];
        wm.AddMonitor(returned);

        wm.RehomeWorkspaces();

        Assert.Same(returned, bound.Monitor);
    }

    [Fact]
    public void ANameTheHostHasNotResolvedFallsBackToThePositionalSpellings()
    {
        WindowManager wm = TwoMonitors(out _, out MonitorNode right);

        // Nobody wrote Names, but the config said monitor="DISPLAY2" - the tail of a
        // device name is one of the spellings the tree resolves for itself.
        var bound = new WorkspaceNode("2") { PreferredMonitorName = "DISPLAY2" };
        wm.AddWorkspace(bound);

        Assert.Same(right, bound.Monitor);
    }

    [Fact]
    public void ANameThatFitsTwoDisplaysTakesTheFirstInEnumerationOrder()
    {
        // Two of the same model report the same friendly name. Deterministic rather
        // than clever: the device path is how the config tells them apart.
        WindowManager wm = TwoMonitors(out MonitorNode left, out MonitorNode right);
        left.Names = ["dell"];
        right.Names = ["dell"];

        var bound = new WorkspaceNode("2") { PreferredMonitorName = "dell" };
        wm.AddWorkspace(bound);

        Assert.Same(left, bound.Monitor);
    }

    [Fact]
    public void ANameBeatsAPositionWhenBothAreSet()
    {
        WindowManager wm = TwoMonitors(out MonitorNode left, out MonitorNode right);
        left.Names = ["laptop"];

        var bound = new WorkspaceNode("2") { PreferredMonitorName = "laptop", PreferredMonitorIndex = 1 };
        wm.AddWorkspace(bound);

        Assert.Same(left, bound.Monitor);
        Assert.NotSame(right, bound.Monitor);
    }

    // ---- what the user sees ---------------------------------------------------

    [Fact]
    public void AShownWorkspaceStaysShownOnItsNewMonitor()
    {
        // Automatic, so it must not hide what the user is looking at. A workspace that
        // was on screen when the monitor came back is on screen afterwards - on the
        // monitor it belongs to, which is the point of binding it there.
        WindowManager wm = TwoMonitors(out MonitorNode left, out MonitorNode right);

        var bound = new WorkspaceNode("2") { PreferredMonitorIndex = 1 };
        wm.AddWorkspace(bound);
        wm.ActivateWorkspace(bound);

        wm.RemoveMonitor(right);
        wm.ActivateWorkspace(bound);
        Assert.True(bound.IsActive);
        Assert.Same(left, bound.Monitor);

        var returned = TreeBuilder.Monitor("\\\\.\\DISPLAY2", x: 1920);
        wm.AddMonitor(returned);

        WmResult result = wm.RehomeWorkspaces();

        Assert.Same(returned, bound.Monitor);
        Assert.True(bound.IsActive);

        // Both monitors announce what they are showing now: the destination shows the
        // arrival, and the source shows whatever it fell back to.
        Assert.Contains(result.Events, e => e is WorkspaceActivated { Workspace: var w, Monitor: var m } &&
            ReferenceEquals(w, bound) && ReferenceEquals(m, returned));
        Assert.Contains(result.Events, e => e is WorkspaceActivated { Monitor: var m } && ReferenceEquals(m, left));
    }

    [Fact]
    public void AHiddenWorkspaceDoesNotTakeOverTheMonitorItReturnsTo()
    {
        WindowManager wm = TwoMonitors(out _, out MonitorNode right);

        // The destination is already showing something the user chose.
        var shown = new WorkspaceNode("3");
        wm.AddWorkspace(shown, right);
        wm.ActivateWorkspace(shown);

        // A hidden workspace bound there, currently parked on the other monitor.
        var bound = new WorkspaceNode("2") { PreferredMonitorIndex = 1 };
        wm.AddWorkspace(bound, wm.Root.Monitors[0]);
        Assert.False(bound.IsActive);

        WmResult result = wm.RehomeWorkspaces();

        Assert.Same(right, bound.Monitor);
        Assert.True(shown.IsActive);
        Assert.False(bound.IsActive);
        Assert.DoesNotContain(result.Events, e => e is WorkspaceActivated);
    }

    [Fact]
    public void FocusFollowsOnlyTheWorkspaceThatHeldIt()
    {
        WindowManager wm = TwoMonitors(out MonitorNode left, out MonitorNode right);

        var bound = new WorkspaceNode("2") { PreferredMonitorIndex = 1 };
        wm.AddWorkspace(bound);
        wm.RemoveMonitor(right);

        // The user is working on workspace 1, on the left; the bound one is hidden.
        wm.ActivateWorkspace(left.Workspaces[0]);
        Assert.Same(left, wm.FocusedMonitor);

        var returned = TreeBuilder.Monitor("\\\\.\\DISPLAY2", x: 1920);
        wm.AddMonitor(returned);
        wm.RehomeWorkspaces();

        Assert.Same(returned, bound.Monitor);
        Assert.Same(left, wm.FocusedMonitor);
    }

    [Fact]
    public void TheScratchpadNeverMoves()
    {
        WindowManager wm = TwoMonitors(out MonitorNode left, out _);

        // Summon and stash to create the scratchpad workspace on the left monitor.
        wm.Open("a");
        wm.ToggleScratchpad("default");

        WorkspaceNode pad = Assert.Single(wm.Root.Monitors.SelectMany(m => m.Workspaces), w => w.IsScratchpad);
        Assert.Same(left, pad.Monitor);

        // Even with a preference somebody managed to write onto it.
        pad.PreferredMonitorIndex = 1;

        Assert.Empty(wm.RehomeWorkspaces().Events);
        Assert.Same(left, pad.Monitor);
    }

    // ---- moving by name, as a command ---------------------------------------

    [Fact]
    public void MoveWorkspaceByDeclaredNameGoesThere()
    {
        WindowManager wm = TwoMonitors(out _, out MonitorNode right);
        right.Names = ["dell-right"];
        wm.Open("a");

        WmResult result = wm.MoveWorkspaceToMonitor("dell-right");

        Assert.True(result.Succeeded);
        Assert.Same(right, wm.FocusedWorkspace!.Monitor);
        Assert.Same(right, wm.FocusedMonitor);
        Assert.True(wm.FocusedWorkspace.IsActive);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("DISPLAY2")]
    [InlineData("display2")]
    [InlineData(@"\\.\DISPLAY2")]
    public void MoveWorkspaceAcceptsEveryPositionalSpelling(string reference)
    {
        WindowManager wm = TwoMonitors(out _, out MonitorNode right);
        wm.Open("a");

        WmResult result = wm.MoveWorkspaceToMonitor(reference);

        Assert.True(result.Succeeded);
        Assert.Same(right, wm.FocusedWorkspace!.Monitor);
    }

    [Fact]
    public void MoveWorkspaceToTheMonitorItIsOnIsRefusedOutLoud()
    {
        WindowManager wm = TwoMonitors(out MonitorNode left, out _);
        left.Names = ["here"];
        wm.Open("a");

        WmResult result = wm.MoveWorkspaceToMonitor("here");

        Assert.False(result.Succeeded);
        Assert.Contains("already on", result.RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveWorkspaceToAnUnknownNameSaysWhatWouldHaveWorked()
    {
        WindowManager wm = TwoMonitors(out MonitorNode left, out _);
        left.Names = ["laptop"];
        wm.Open("a");

        WmResult result = wm.MoveWorkspaceToMonitor("projector");

        Assert.False(result.Succeeded);
        Assert.Contains("projector", result.RejectionReason, StringComparison.Ordinal);

        // Every way in: the position, the device name, and the names the config gave.
        Assert.Contains("0 = ", result.RejectionReason, StringComparison.Ordinal);
        Assert.Contains("DISPLAY1", result.RejectionReason, StringComparison.Ordinal);
        Assert.Contains("laptop", result.RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredNameOutranksADeviceNameThatHappensToCollide()
    {
        // Somebody calls a monitor "DISPLAY1" in their config and means the one on
        // the right. The config's word wins over Windows' spelling.
        WindowManager wm = TwoMonitors(out _, out MonitorNode right);
        right.Names = ["DISPLAY1"];

        Assert.Same(right, wm.FindMonitor("DISPLAY1"));
    }
}

/// <summary>
/// The positional spellings a monitor reference accepts, and the ones it refuses.
/// </summary>
public sealed class MonitorReferenceTests
{
    [Theory]
    [InlineData("0", true)]
    [InlineData("12", true)]
    [InlineData("-1", true)]
    [InlineData("DISPLAY1", true)]
    [InlineData("display12", true)]
    [InlineData(@"\\.\DISPLAY3", true)]
    [InlineData("dell-left", false)]
    [InlineData("DISPLAY", false)]
    [InlineData("DISPLAYX", false)]
    [InlineData("DISPLAY1a", false)]
    [InlineData("1.5", false)]
    [InlineData("", false)]
    public void PositionalSpellingsAreRecognised(string reference, bool positional)
    {
        // The loader uses this to decide whether a word needs to have been declared:
        // a number or a device name is checked at runtime against what is attached,
        // anything else against the config.
        Assert.Equal(positional, MonitorReference.IsPositional(reference));
    }

    [Fact]
    public void AnIndexOutOfRangeResolvesToNothingRatherThanThePrimary()
    {
        // AddWorkspace falls back to the primary on its own; the reference must not,
        // or a command aimed at a missing display would silently hit the wrong one.
        RootNode root = TreeBuilder.Root(TreeBuilder.Monitor());

        Assert.Null(MonitorReference.Resolve(root, "1"));
        Assert.Null(MonitorReference.Resolve(root, "-1"));
        Assert.Same(root.Monitors[0], MonitorReference.Resolve(root, "0"));
    }

    [Fact]
    public void ADeviceNameResolvesByItsTailCaseInsensitively()
    {
        RootNode root = TreeBuilder.Root(
            TreeBuilder.Monitor("\\\\.\\DISPLAY1"),
            TreeBuilder.Monitor("\\\\.\\DISPLAY2", x: 1920));

        Assert.Same(root.Monitors[1], MonitorReference.Resolve(root, "DISPLAY2"));
        Assert.Same(root.Monitors[1], MonitorReference.Resolve(root, "display2"));
        Assert.Same(root.Monitors[1], MonitorReference.Resolve(root, @"\\.\DISPLAY2"));
        Assert.Null(MonitorReference.Resolve(root, "DISPLAY3"));
    }
}
