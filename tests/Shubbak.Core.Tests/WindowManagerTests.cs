using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for <see cref="WindowManager"/>: the state machine's observable behaviour.
/// </summary>
/// <remarks>
/// These assert on <see cref="WmEvent"/>s and on tree shape rather than on Win32
/// side effects, which is the whole point of keeping the state machine free of
/// platform code.
/// </remarks>
public sealed class WindowManagerTests
{
    // ---- window lifecycle --------------------------------------------------

    [Fact]
    public void ManagingAWindowFocusesItAndReportsIt()
    {
        WindowManager wm = WmFixture.Create();

        WindowNode window = TreeBuilder.Window("a");
        WmResult result = wm.ManageWindow(window);

        Assert.True(result.Succeeded);
        Assert.Same(window, wm.FocusedWindow);
        Assert.Same(window, result.Single<WindowManaged>().Window);
        Assert.Same(window, result.Single<WindowFocused>().Window);
    }

    [Fact]
    public void NewWindowsAppearBesideTheFocusedOne()
    {
        // Appending instead would make a new window materialise at the far edge of
        // the screen, away from where the user is looking.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");

        wm.FocusWindow(a);
        WindowNode c = wm.Open("c");

        WorkspaceNode workspace = wm.FocusedWorkspace!;
        Assert.Equal([a, c, b], workspace.Children.Cast<WindowNode>());
    }

    [Fact]
    public void ClosingTheFocusedWindowMovesFocusToTheNextSibling()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        WindowNode c = wm.Open("c");

        wm.FocusWindow(b);
        WmResult result = wm.UnmanageWindow(b);

        // Forwards first: c slides into b's place, so that is where the eye goes.
        Assert.Same(c, wm.FocusedWindow);
        Assert.Equal(b.Id, result.Single<WindowUnmanaged>().Id);
        Assert.DoesNotContain(b, wm.FocusedWorkspace!.Children);
        Assert.Same(a, wm.FocusedWorkspace!.Children[0]);
    }

    [Fact]
    public void ClosingTheLastWindowInARowFallsBackToThePrevious()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");

        wm.FocusWindow(b);
        wm.UnmanageWindow(b);

        Assert.Same(a, wm.FocusedWindow);
    }

    [Fact]
    public void ClosingTheOnlyWindowLeavesNothingFocused()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");

        WmResult result = wm.UnmanageWindow(a);

        Assert.Null(wm.FocusedWindow);
        Assert.Null(result.Single<WindowFocused>().Window);
    }

    [Fact]
    public void FocusStaysLocalWhenClosingInsideANestedContainer()
    {
        // Workspace [ a | Column [ b / c ] ] - closing b must land on c, not jump
        // across the screen to a.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");

        wm.Split(SplitLayout.Vertical);
        WindowNode c = wm.Open("c");

        wm.FocusWindow(b);
        wm.UnmanageWindow(b);

        Assert.Same(c, wm.FocusedWindow);
        Assert.Contains(a, wm.FocusedWorkspace!.DescendantWindows());
    }

    [Fact]
    public void ClosingAWindowDoesNotLeaveRedundantContainersBehind()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");

        wm.Split(SplitLayout.Vertical);
        WindowNode c = wm.Open("c");

        wm.UnmanageWindow(c);

        // The wrapper container is down to one child and must collapse, or it will
        // silently change how focus and resize behave later.
        WorkspaceNode workspace = wm.FocusedWorkspace!;
        Assert.Equal(2, workspace.Count);
        Assert.All(workspace.Children, child => Assert.IsType<WindowNode>(child));
    }

    // ---- focus -------------------------------------------------------------

    [Fact]
    public void FocusDirectionMovesBetweenWindows()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Arrange();

        wm.FocusWindow(a);
        Assert.True(wm.FocusDirection(Direction.Right).Succeeded);
        Assert.Same(b, wm.FocusedWindow);
    }

    [Fact]
    public void FocusDirectionIsRejectedAtTheEdgeWithASingleMonitor()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        _ = wm.Open("b");
        wm.Arrange();

        wm.FocusWindow(a);
        WmResult result = wm.FocusDirection(Direction.Left);

        // Rejection is a normal outcome, reported rather than thrown, so the input
        // pipeline is never interrupted.
        Assert.False(result.Succeeded);
        Assert.NotNull(result.RejectionReason);
        Assert.Same(a, wm.FocusedWindow);
    }

    [Fact]
    public void FocusDirectionCrossesToTheAdjacentMonitor()
    {
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: "1");
        MonitorNode second = wm.Root.Monitors[1];
        wm.AddWorkspace(new WorkspaceNode("2"), second);
        wm.ActivateWorkspace(second.Workspaces[0]);
        WindowNode onSecond = wm.Open("right");

        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);
        WindowNode onFirst = wm.Open("left");
        wm.Arrange();

        Assert.True(wm.FocusDirection(Direction.Right).Succeeded);
        Assert.Same(onSecond, wm.FocusedWindow);
        Assert.Same(second, wm.FocusedMonitor);
        _ = onFirst;
    }

    [Fact]
    public void CrossingOntoAnEmptyMonitorLeavesFocusRecoverable()
    {
        // The reported failure. Moving a window off a monitor and then focusing onto
        // the monitor just emptied cleared focus, and since every direction command
        // navigates *from* a focused window, nothing on the keyboard could get it
        // back - only a mouse click, which is a foreground change the daemon listens
        // for. A real session sat like this for fourteen seconds.
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: "1");
        MonitorNode second = wm.Root.Monitors[1];
        wm.AddWorkspace(new WorkspaceNode("2"), second);

        WindowNode onFirst = wm.Open("left");
        wm.Arrange();
        wm.FocusWindow(onFirst);

        // Crossing onto an empty monitor is allowed: it is how you aim the next
        // window at an empty screen when new windows follow focus.
        Assert.True(wm.FocusDirection(Direction.Right).Succeeded);
        Assert.Null(wm.FocusedWindow);
        Assert.Same(second, wm.FocusedMonitor);

        // What matters is that it is not a one-way trip.
        Assert.True(wm.FocusDirection(Direction.Left).Succeeded);
        Assert.Same(onFirst, wm.FocusedWindow);
    }

    [Fact]
    public void FocusDirectionLandsOnTheCurrentWorkspaceBeforeMoving()
    {
        // With focus cleared but windows still present, the first keypress should put
        // the border back rather than send focus to another monitor. Moving is what
        // the second press is for.
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: "1");
        wm.AddWorkspace(new WorkspaceNode("2"), wm.Root.Monitors[1]);

        WindowNode a = wm.Open("a");
        wm.Arrange();
        wm.FocusWindow(null);

        Assert.True(wm.FocusDirection(Direction.Right).Succeeded);
        Assert.Same(a, wm.FocusedWindow);
    }

    [Fact]
    public void FocusDirectionStillRejectsWhenThereIsNowhereToGo()
    {
        // Nothing focused, nothing to land on, no monitor that way. Recovery must not
        // turn this into a false success - the rejection is what tells the user the
        // keybinding is working and the layout is empty.
        WindowManager wm = WmFixture.Create();
        wm.FocusWindow(null);

        WmResult result = wm.FocusDirection(Direction.Right);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public void ClosingTheLastTiledWindowFallsBackToAFloatingOne()
    {
        // Same dead end reached a different way. Every scan in SuccessorFor filtered
        // on IsTiled, so a workspace whose only remaining window was floating (or
        // fullscreen, or maximised) yielded nothing and focus was cleared - which is
        // what closing a window beside a fullscreen meeting would do.
        WindowManager wm = WmFixture.Create();
        WindowNode floating = wm.Open("floating");
        wm.SetWindowState(floating, WindowState.Floating);

        WindowNode tiled = wm.Open("tiled");
        wm.FocusWindow(tiled);

        wm.UnmanageWindow(tiled);

        Assert.Same(floating, wm.FocusedWindow);
    }

    [Fact]
    public void FocusingAWindowOnAHiddenWorkspaceActivatesThatWorkspace()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode a = wm.Open("a");

        wm.FocusWorkspace("2");
        WindowNode b = wm.Open("b");

        WmResult result = wm.FocusWindow(a);

        // Focus must never sit on something invisible.
        Assert.Same(a, wm.FocusedWindow);
        Assert.True(a.Workspace!.IsActive);
        Assert.True(result.Has<WorkspaceActivated>());
        _ = b;
    }

    // ---- workspaces --------------------------------------------------------

    [Fact]
    public void SwitchingWorkspacesRestoresThePreviousFocus()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.FocusWindow(a);

        wm.FocusWorkspace("2");
        Assert.Null(wm.FocusedWindow);

        wm.FocusWorkspace("1");

        // Switching away and back is lossless.
        Assert.Same(a, wm.FocusedWindow);
        _ = b;
    }

    [Fact]
    public void FocusingAWorkspaceOnDemandCreatesATransientOne()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: "1");

        WmResult result = wm.FocusWorkspace("9");

        Assert.True(result.Has<WorkspaceCreated>());
        WorkspaceNode created = wm.Root.FindWorkspace("9")!;
        Assert.True(created.IsTransient);
        Assert.True(created.IsActive);
    }

    [Fact]
    public void TransientWorkspacesAreReapedOnceEmptyAndInactive()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: "1");

        wm.FocusWorkspace("9");
        wm.Open("temp");
        wm.FocusWorkspace("1");

        // Still alive: it has a window.
        Assert.NotNull(wm.Root.FindWorkspace("9"));

        WindowNode temp = wm.Root.FindWorkspace("9")!.DescendantWindows().Single();
        WmResult result = wm.UnmanageWindow(temp);

        Assert.True(result.Has<WorkspaceDestroyed>());
        Assert.Null(wm.Root.FindWorkspace("9"));
    }

    [Fact]
    public void DeclaredWorkspacesSurviveGoingEmpty()
    {
        // A declared workspace must persist, or its keybinding stops working.
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);

        wm.FocusWorkspace("2");
        WindowNode only = wm.Open("only");
        wm.FocusWorkspace("1");
        WmResult result = wm.UnmanageWindow(only);

        Assert.False(result.Has<WorkspaceDestroyed>());
        Assert.NotNull(wm.Root.FindWorkspace("2"));
    }

    [Fact]
    public void RefocusingTheActiveWorkspaceTogglesBackWhenConfigured()
    {
        var options = new WmOptions { ToggleWorkspaceOnRefocus = true };
        WindowManager wm = WmFixture.Create(options, workspaceNames: ["1", "2"]);

        wm.FocusWorkspace("2");
        wm.FocusWorkspace("2");

        Assert.Equal("1", wm.FocusedWorkspace!.Name);
    }

    [Fact]
    public void RefocusingTheActiveWorkspaceIsANoOpByDefault()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);

        wm.FocusWorkspace("2");
        wm.FocusWorkspace("2");

        Assert.Equal("2", wm.FocusedWorkspace!.Name);
    }

    // ---- moving windows ----------------------------------------------------

    [Fact]
    public void MovingAWindowToAnotherWorkspaceLeavesFocusBehindByDefault()
    {
        // "Put this away" and "go there" are separate intentions; the author's
        // config expresses the combined one by binding two commands to one key.
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");

        wm.FocusWindow(b);
        wm.MoveToWorkspace("2");

        Assert.Equal("1", wm.FocusedWorkspace!.Name);
        Assert.Same(a, wm.FocusedWindow);
        Assert.Equal("2", b.Workspace!.Name);
    }

    [Fact]
    public void MovingAWindowCanFollowItWhenConfigured()
    {
        var options = new WmOptions { FollowWindowOnMove = true };
        WindowManager wm = WmFixture.Create(options, workspaceNames: ["1", "2"]);
        WindowNode b = wm.Open("b");

        wm.MoveToWorkspace("2");

        Assert.Equal("2", wm.FocusedWorkspace!.Name);
        Assert.Same(b, wm.FocusedWindow);
    }

    [Fact]
    public void MovingAWindowToAnotherMonitorTakesFocusWithIt()
    {
        // The distinction the "leave focus behind" rule turns on is whether the
        // window went into hiding. Another monitor's active workspace is on screen,
        // so it did not - and leaving focus behind means a second push in the same
        // direction moves a different window, which is not what anyone means by it.
        // GlazeWM keeps focus on the window here for the same reason.
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: ["1"]);
        wm.AddWorkspace(new WorkspaceNode("2"), wm.Root.Monitors[1]);
        wm.ActivateWorkspace(wm.Root.Monitors[1].Workspaces[0]);
        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);

        WindowNode a = wm.Open("a");
        wm.Arrange();

        wm.MoveDirection(Direction.Right);

        Assert.Same(a, wm.FocusedWindow);
        Assert.Equal("2", a.Workspace!.Name);
    }

    [Fact]
    public void MovingAWindowToAHiddenWorkspaceStillLeavesFocusBehind()
    {
        // The other half. Following a window somewhere invisible is exactly what
        // follow-window-on-move #false is for, and the monitor rule must not weaken
        // it - workspace 2 here shares a monitor with 1, so moving there hides it.
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: ["1", "2"]);
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");

        wm.FocusWindow(b);
        wm.MoveToWorkspace("2");

        Assert.Same(a, wm.FocusedWindow);
        Assert.Equal("1", wm.FocusedWorkspace!.Name);
        Assert.Equal("2", b.Workspace!.Name);
    }

    [Fact]
    public void MovingAWindowToAnotherMonitorEntersFromTheSideItArrivedAt()
    {
        // Pushed right, so it should appear at the neighbour's left edge - next to
        // the monitor it came from. Appending instead threw it to the far side of
        // the screen, which is the opposite of the direction the key describes.
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: ["1"]);
        wm.AddWorkspace(new WorkspaceNode("2"), wm.Root.Monitors[1]);
        wm.ActivateWorkspace(wm.Root.Monitors[1].Workspaces[0]);

        WindowNode browser = wm.Open("browser");

        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);
        wm.Open("terminal");
        WindowNode notepad = wm.Open("notepad");
        wm.FocusWindow(notepad);
        wm.Arrange();

        wm.MoveDirection(Direction.Right);

        WorkspaceNode destination = wm.Root.Monitors[1].Workspaces[0];

        Assert.Equal(
            ["notepad", "browser"],
            destination.DescendantWindows().Select(w => w.Identity.Title));

        Assert.Same(browser, destination.DescendantWindows().Last());
    }

    [Fact]
    public void MovingAWindowLeftEntersFromTheRightEdge()
    {
        // The mirror image. Only window on its monitor, so there is no sibling to
        // swap with and the move genuinely crosses - and it should land on the far
        // side of the destination, nearest the monitor it came from.
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: ["1"]);
        wm.Open("terminal");
        wm.Open("editor");

        wm.AddWorkspace(new WorkspaceNode("2"), wm.Root.Monitors[1]);
        wm.ActivateWorkspace(wm.Root.Monitors[1].Workspaces[0]);
        WindowNode notepad = wm.Open("notepad");
        wm.FocusWindow(notepad);
        wm.Arrange();

        wm.MoveDirection(Direction.Left);

        WorkspaceNode destination = wm.Root.Monitors[0].Workspaces[0];

        Assert.Equal(
            ["terminal", "editor", "notepad"],
            destination.DescendantWindows().Select(w => w.Identity.Title));
    }

    [Fact]
    public void MoveDirectionSwapsWithASibling()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Arrange();

        wm.FocusWindow(a);
        Assert.True(wm.MoveDirection(Direction.Right).Succeeded);

        Assert.Equal([b, a], wm.FocusedWorkspace!.Children.Cast<WindowNode>());
        Assert.Same(a, wm.FocusedWindow);
    }

    [Fact]
    public void MoveDirectionEscapesANestedContainerOneStepAtATime()
    {
        // Workspace(splith) [ a | Column [ b / c ] ]
        //   +---+---+
        //   |   | b |
        //   | a +---+
        //   |   | c |
        //   +---+---+
        // Moving b left lifts it out of the column and drops it immediately to the
        // column's left - between a and c. It must NOT leap to the far edge: each
        // press should move one position, so the user can stop halfway.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Split(SplitLayout.Vertical);
        WindowNode c = wm.Open("c");
        wm.Arrange();

        wm.FocusWindow(b);
        Assert.True(wm.MoveDirection(Direction.Left).Succeeded);

        WorkspaceNode workspace = wm.FocusedWorkspace!;
        Assert.Equal([a, b, c], workspace.Children.Cast<WindowNode>());

        // The column held only c afterwards, so it collapsed rather than lingering
        // as invisible structure.
        Assert.All(workspace.Children, child => Assert.IsType<WindowNode>(child));

        // A second press then swaps past a.
        wm.Arrange();
        Assert.True(wm.MoveDirection(Direction.Left).Succeeded);
        Assert.Equal([b, a, c], workspace.Children.Cast<WindowNode>());
    }

    [Fact]
    public void MovingToAWorkspaceThatDoesNotExistCreatesIt()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: "1");
        WindowNode a = wm.Open("a");

        WmResult result = wm.MoveToWorkspace("7");

        Assert.True(result.Has<WorkspaceCreated>());
        Assert.Equal("7", a.Workspace!.Name);
    }

    // ---- sizing ------------------------------------------------------------

    [Fact]
    public void ResizeAdjustsTheFocusedWindowsShare()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Arrange();

        wm.FocusWindow(a);
        Assert.True(wm.Resize(Axis.Horizontal, 0.1).Succeeded);

        Assert.Equal(0.6, a.SizeRatio, 1e-6);
        Assert.Equal(0.4, b.SizeRatio, 1e-6);
    }

    [Fact]
    public void ResizingReportsThatSomethingChanged()
    {
        // Resizing used to mutate the tree and report nothing. The daemon marks the
        // layout dirty from events, so the new ratios sat in the tree unapplied and
        // the keystroke appeared to do nothing - until an unrelated event forced a
        // relayout, which in practice meant switching workspace and back.
        WindowManager wm = WmFixture.Create();
        wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Arrange();

        wm.FocusWindow(b);
        WmResult result = wm.Resize(Axis.Horizontal, 0.1);

        Assert.True(result.Succeeded);
        Assert.Contains(result.Events, e => e is ContainerResized);
    }

    [Fact]
    public void EqualisingReportsThatSomethingChanged()
    {
        // Same defect, same silence.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        wm.Open("b");
        wm.Arrange();

        wm.FocusWindow(a);
        wm.Resize(Axis.Horizontal, 0.2);

        WmResult result = wm.EqualiseSiblings();

        Assert.True(result.Succeeded);
        Assert.Contains(result.Events, e => e is ContainerResized);
        Assert.Equal(0.5, a.SizeRatio, 1e-6);
    }

    [Fact]
    public void ResizeAcrossANestedContainerAppliesAtTheRightAncestor()
    {
        // Workspace(splith) [ a | Column [ b / c ] ]
        // Widening b cannot be satisfied by the column, so it must widen the column
        // itself within the workspace.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Split(SplitLayout.Vertical);
        wm.Open("c");
        wm.Arrange();

        wm.FocusWindow(b);
        Assert.True(wm.Resize(Axis.Horizontal, 0.2).Succeeded);

        ContainerNode column = (ContainerNode)wm.FocusedWorkspace!.Children[1];
        Assert.Equal(0.7, column.SizeRatio, 1e-6);
        Assert.Equal(0.3, a.SizeRatio, 1e-6);
    }

    [Fact]
    public void ResizeIsRejectedWhenNoContainerSplitsAlongThatAxis()
    {
        WindowManager wm = WmFixture.Create();
        wm.Open("a");
        wm.Arrange();

        WmResult result = wm.Resize(Axis.Vertical, 0.1);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RejectionReason);
    }

    // ---- structure ---------------------------------------------------------

    [Fact]
    public void ToggleTilingDirectionFlipsTheFocusedContainer()
    {
        WindowManager wm = WmFixture.Create();
        wm.Open("a");
        wm.Open("b");

        WmResult result = wm.ToggleTilingDirection();

        Assert.Equal("splitv", result.Single<LayoutChanged>().Layout);
        Assert.Same(SplitLayout.Vertical, wm.FocusedWorkspace!.Layout);
    }

    [Fact]
    public void SplitWrapsTheFocusedWindowSoTheNextOneNestsInside()
    {
        WindowManager wm = WmFixture.Create();
        wm.Open("a");
        WindowNode b = wm.Open("b");

        wm.Split(SplitLayout.Vertical);
        WindowNode c = wm.Open("c");

        var wrapper = (ContainerNode)wm.FocusedWorkspace!.Children[1];
        Assert.Same(SplitLayout.Vertical, wrapper.Layout);
        Assert.Equal([b, c], wrapper.Children.Cast<WindowNode>());
    }

    // ---- window state ------------------------------------------------------

    [Fact]
    public void ToggleFloatingRoundTripsAndRemembersGeometry()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        wm.Open("b");
        wm.Arrange();

        Rect tiled = a.Rect;
        wm.FocusWindow(a);

        wm.ToggleFloating();
        Assert.Equal(WindowState.Floating, a.State);
        Assert.Equal(tiled, a.FloatingRect);

        wm.ToggleFloating();
        Assert.Equal(WindowState.Tiling, a.State);
    }

    [Fact]
    public void FloatingAWindowGivesItsSpaceToTheRemainingTiles()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Arrange();

        wm.FocusWindow(a);
        wm.ToggleFloating();

        Assert.Equal(new Rect(0, 0, 1920, 1080), wm.RectOf(b));
    }

    [Fact]
    public void MinimisingTheFocusedWindowMovesFocusAway()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Arrange();

        wm.FocusWindow(a);
        wm.ToggleMinimised();

        Assert.Equal(WindowState.Minimised, a.State);
        Assert.Same(b, wm.FocusedWindow);
    }

    [Fact]
    public void StateChangesReportBothEnds()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");

        WmResult result = wm.ToggleFullscreen();
        WindowStateChanged change = result.Single<WindowStateChanged>();

        Assert.Equal(WindowState.Tiling, change.Previous);
        Assert.Equal(WindowState.Fullscreen, change.Current);
        _ = a;
    }

    // ---- monitors ----------------------------------------------------------

    [Fact]
    public void RemovingAMonitorMigratesItsWorkspacesRatherThanDiscardingThem()
    {
        // Displays disappear for mundane reasons - undocking, DisplayPort sleep.
        // Discarding the workspaces would strand every window on them off-screen.
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: "1");
        MonitorNode second = wm.Root.Monitors[1];
        wm.AddWorkspace(new WorkspaceNode("2"), second);
        wm.ActivateWorkspace(second.Workspaces[0]);
        WindowNode stranded = wm.Open("stranded");

        WmResult result = wm.RemoveMonitor(second);

        Assert.True(result.Has<MonitorRemoved>());
        Assert.True(result.Has<WorkspaceMoved>());
        Assert.Same(wm.Root.Monitors[0], stranded.Monitor);
        Assert.Single(wm.Root.Monitors);
    }

    [Fact]
    public void RemovingTheFocusedMonitorMovesThePointOfAction()
    {
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: "1");
        MonitorNode first = wm.Root.Monitors[0];
        MonitorNode second = wm.Root.Monitors[1];
        wm.AddWorkspace(new WorkspaceNode("2"), second);
        wm.ActivateWorkspace(second.Workspaces[0]);

        wm.RemoveMonitor(second);

        Assert.Same(first, wm.FocusedMonitor);
    }

    [Fact]
    public void MoveWorkspaceToMonitorRelocatesAndActivatesIt()
    {
        WindowManager wm = WmFixture.Create(monitors: 2, workspaceNames: "1");
        WindowNode a = wm.Open("a");

        WmResult result = wm.MoveWorkspaceToMonitor(Direction.Right);

        Assert.True(result.Succeeded);
        Assert.Same(wm.Root.Monitors[1], a.Monitor);
        Assert.True(a.Workspace!.IsActive);
    }

    [Fact]
    public void MoveWorkspaceIsRejectedWhenThereIsNoMonitorThatWay()
    {
        WindowManager wm = WmFixture.Create();
        wm.Open("a");

        WmResult result = wm.MoveWorkspaceToMonitor(Direction.Right);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RejectionReason);
    }

    // ---- misc --------------------------------------------------------------

    [Fact]
    public void TitleChangesAreReportedOnlyWhenTheTitleActuallyChanges()
    {
        // S4 showed NAMECHANGE firing far more often than the title really changes,
        // so the state machine filters before the bar ever sees it.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");

        WmResult unchanged = wm.UpdateTitle(a, "a");
        Assert.False(unchanged.Has<WindowTitleChanged>());

        WmResult changed = wm.UpdateTitle(a, "a - edited");
        WindowTitleChanged evt = changed.Single<WindowTitleChanged>();

        Assert.Equal("a", evt.Previous);
        Assert.Equal("a - edited", a.Identity.Title);
    }

    [Fact]
    public void BindingModeChangesAreReported()
    {
        WindowManager wm = WmFixture.Create();

        WmResult entered = wm.SetBindingMode("resize");
        Assert.Equal("resize", entered.Single<BindingModeChanged>().Mode);
        Assert.Equal("resize", wm.BindingMode);

        WmResult left = wm.SetBindingMode(null);
        Assert.Null(left.Single<BindingModeChanged>().Mode);
    }

    [Fact]
    public void EventsAreDrainedSoEachResultCarriesOnlyItsOwnChanges()
    {
        WindowManager wm = WmFixture.Create();
        wm.Open("a");

        WmResult second = wm.ToggleTilingDirection();

        // No leftovers from managing the window.
        Assert.False(second.Has<WindowManaged>());
        Assert.Single(second.Events);
    }
}
