using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// The window manager's newer verbs: maximise, the monitor-targeted focus and move,
/// swap, runtime gaps and the master count.
/// </summary>
/// <remarks>
/// Each of these is a gap somebody found by reaching for the key every other tiling
/// window manager has. They share one design rule with the verbs before them: a
/// request that cannot be satisfied is refused with a reason, never quietly turned
/// into a different action.
/// </remarks>
public sealed class NewerVerbsTests
{
    // ---- maximise -----------------------------------------------------------

    [Fact]
    public void MaximiseFillsTheWorkAreaAndTogglesBack()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Arrange();

        Rect half = b.Rect;

        Assert.True(wm.ToggleMaximised().Succeeded);
        Assert.Equal(WindowState.Maximised, b.State);
        Assert.Equal(WindowState.Tiling, a.State);

        wm.Arrange();
        Assert.Equal(wm.Root.Monitors[0].WorkArea, b.Rect);

        // Back to what it was: tiling, beside a, in its old half.
        Assert.True(wm.ToggleMaximised().Succeeded);
        Assert.Equal(WindowState.Tiling, b.State);
        Assert.Equal(half, wm.RectOf(b));
    }

    [Fact]
    public void AFloatingWindowComesBackFromMaximisedAsFloating()
    {
        // Maximised is an away state; what it returns to is what it left.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        wm.Arrange();

        wm.SetWindowState(a, WindowState.Floating);
        wm.ToggleMaximised();
        wm.ToggleMaximised();

        Assert.Equal(WindowState.Floating, a.State);
    }

    [Fact]
    public void MaximiseWithNothingFocusedIsRefused()
    {
        WindowManager wm = WmFixture.Create();

        WmResult result = wm.ToggleMaximised();

        Assert.False(result.Succeeded);
    }

    // ---- monitors -----------------------------------------------------------

    private static WindowManager TwoMonitors(out WorkspaceNode right)
    {
        WindowManager wm = WmFixture.Create(monitors: 2);

        right = new WorkspaceNode("2");
        wm.AddWorkspace(right, wm.Root.Monitors[1]);
        wm.ActivateWorkspace(right);
        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);

        return wm;
    }

    [Fact]
    public void FocusMonitorByDirectionLandsOnWhatThatMonitorWasLookingAt()
    {
        WindowManager wm = TwoMonitors(out WorkspaceNode right);
        WindowNode left = wm.Open("left");

        WindowNode far = TreeBuilder.Window("far");
        wm.ManageWindow(far, right);
        wm.FocusWindow(left);

        Assert.True(wm.FocusMonitor(Direction.Right, null).Succeeded);

        Assert.Same(far, wm.FocusedWindow);
        Assert.Same(wm.Root.Monitors[1], wm.FocusedMonitor);
    }

    [Fact]
    public void FocusMonitorByNameOrPosition()
    {
        WindowManager wm = TwoMonitors(out WorkspaceNode right);
        wm.Open("left");

        WindowNode far = TreeBuilder.Window("far");
        wm.ManageWindow(far, right);
        wm.FocusMonitor(Direction.Left, null);

        Assert.True(wm.FocusMonitor(null, "1").Succeeded);
        Assert.Same(far, wm.FocusedWindow);

        Assert.True(wm.FocusMonitor(null, @"\\.\DISPLAY1").Succeeded);
        Assert.Same(wm.Root.Monitors[0], wm.FocusedMonitor);
    }

    [Fact]
    public void FocusMonitorRefusesWhatIsNotThere()
    {
        WindowManager wm = TwoMonitors(out _);
        wm.Open("left");

        WmResult nothingLeft = wm.FocusMonitor(Direction.Left, null);
        WmResult noSuchName = wm.FocusMonitor(null, "projector");

        Assert.False(nothingLeft.Succeeded);
        Assert.False(noSuchName.Succeeded);
        Assert.Contains("projector", noSuchName.RejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveToMonitorPutsTheWindowOnThatMonitorsActiveWorkspace()
    {
        WindowManager wm = TwoMonitors(out WorkspaceNode right);
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.FocusWindow(b);

        Assert.True(wm.MoveToMonitor(Direction.Right, null).Succeeded);

        Assert.Same(right, b.Workspace);

        // Without --focus, focus stays behind on the source workspace.
        Assert.Same(a, wm.FocusedWindow);
    }

    [Fact]
    public void MoveToMonitorWithFocusFollows()
    {
        WindowManager wm = TwoMonitors(out WorkspaceNode right);
        wm.Open("a");
        WindowNode b = wm.Open("b");

        Assert.True(wm.MoveToMonitor(null, "1", focus: true).Succeeded);

        Assert.Same(right, b.Workspace);
        Assert.Same(b, wm.FocusedWindow);
        Assert.Same(wm.Root.Monitors[1], wm.FocusedMonitor);
    }

    [Fact]
    public void MoveToTheMonitorTheWindowIsOnIsRefused()
    {
        WindowManager wm = TwoMonitors(out _);
        wm.Open("a");

        WmResult result = wm.MoveToMonitor(null, "0");

        Assert.False(result.Succeeded);
        Assert.Contains("already", result.RejectionReason!, StringComparison.Ordinal);
    }

    // ---- swap ---------------------------------------------------------------

    [Fact]
    public void SwapExchangesTwoSiblings()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Arrange();

        Rect wasA = a.Rect;
        Rect wasB = b.Rect;

        wm.FocusWindow(a);
        Assert.True(wm.SwapDirection(Direction.Right).Succeeded);

        wm.Arrange();
        Assert.Equal(wasB, a.Rect);
        Assert.Equal(wasA, b.Rect);
        Assert.Same(a, wm.FocusedWindow);
    }

    [Fact]
    public void SwapAcrossContainersKeepsTheTreesShape()
    {
        // a | (b / c): swapping a with b puts b on the left and a in the column, and the
        // column is still a column. A move would have flattened or joined something.
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.Split(SplitLayout.Vertical);
        WindowNode c = wm.Open("c");
        wm.Arrange();

        ContainerNode column = b.ParentContainer!;
        Assert.Same(column, c.ParentContainer);

        wm.FocusWindow(a);
        Assert.True(wm.SwapDirection(Direction.Right).Succeeded);

        Assert.Same(column, a.ParentContainer);
        Assert.Same(wm.FocusedWorkspace, b.ParentContainer);
        Assert.Equal(2, column.Count);
    }

    [Fact]
    public void SwapWithNothingThatWayIsRefused()
    {
        WindowManager wm = WmFixture.Create();
        wm.Open("a");
        wm.Open("b");

        WmResult result = wm.SwapDirection(Direction.Right);

        Assert.False(result.Succeeded);
        Assert.Contains("right", result.RejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFloatingWindowCannotBeSwapped()
    {
        WindowManager wm = WmFixture.Create();
        wm.Open("a");
        WindowNode b = wm.Open("b");
        wm.SetWindowState(b, WindowState.Floating);

        Assert.False(wm.SwapDirection(Direction.Left).Succeeded);
    }

    // ---- gaps ---------------------------------------------------------------

    [Fact]
    public void GapsChangeByASignedAmountAndTheLayoutFollows()
    {
        WindowManager wm = WmFixture.Create(new WmOptions { InnerGap = 4, OuterGap = Gaps.All(8) });
        WindowNode a = wm.Open("a");
        wm.Open("b");
        wm.Arrange();

        int leftBefore = a.Rect.X;

        WmResult result = wm.AdjustGaps(inner: 2, outer: 4);

        Assert.True(result.Succeeded);
        Assert.True(result.Has<GapsChanged>());
        Assert.Equal(6, wm.Options.InnerGap);
        Assert.Equal(Gaps.All(12), wm.Options.OuterGap);

        wm.Arrange();
        Assert.Equal(leftBefore + 4, a.Rect.X);
    }

    [Fact]
    public void GapsCanBeSetOutright()
    {
        WindowManager wm = WmFixture.Create(new WmOptions { InnerGap = 4, OuterGap = Gaps.All(8) });
        wm.Open("a");

        Assert.True(wm.AdjustGaps(inner: 0, outer: 0, absolute: true).Succeeded);

        Assert.Equal(0, wm.Options.InnerGap);
        Assert.Equal(Gaps.All(0), wm.Options.OuterGap);
    }

    [Fact]
    public void GapsNeverGoNegativeAndAnUnchangedRequestIsRefused()
    {
        WindowManager wm = WmFixture.Create(new WmOptions { InnerGap = 0 });
        wm.Open("a");

        Assert.False(wm.AdjustGaps(inner: -4, outer: null).Succeeded);
        Assert.Equal(0, wm.Options.InnerGap);
        Assert.False(wm.AdjustGaps(null, null).Succeeded);

        // Past the ceiling is a typo, refused rather than clamped: +900 landing on the
        // largest allowed gap is a screen with no room to tile anything.
        WmResult absurd = wm.AdjustGaps(inner: 900, outer: null);

        Assert.False(absurd.Succeeded);
        Assert.Equal(0, wm.Options.InnerGap);
    }

    // ---- masters ------------------------------------------------------------

    [Fact]
    public void TheMasterCountChangesTheMasterLayoutInPlace()
    {
        WindowManager wm = WmFixture.Create();
        wm.Open("a");
        wm.Open("b");
        wm.Open("c");
        wm.SetLayout(MasterStackLayout.Left);

        WmResult result = wm.SetMasterCount(1);

        Assert.True(result.Succeeded);
        Assert.True(result.Has<LayoutChanged>());

        var layout = Assert.IsType<MasterStackLayout>(wm.FocusedWorkspace!.Layout);
        Assert.Equal(2, layout.MasterCount);
        Assert.Equal(Axis.Horizontal, layout.Axis);
        Assert.True(layout.MasterFirst);

        Assert.True(wm.SetMasterCount(1, absolute: true).Succeeded);
        Assert.Equal(1, ((MasterStackLayout)wm.FocusedWorkspace!.Layout).MasterCount);
    }

    [Fact]
    public void TheMasterCountIsRefusedOutsideAMasterLayout()
    {
        WindowManager wm = WmFixture.Create();
        wm.Open("a");

        WmResult result = wm.SetMasterCount(1);

        Assert.False(result.Succeeded);
        Assert.Contains("master", result.RejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMasterCountHasAFloorAndACeiling()
    {
        WindowManager wm = WmFixture.Create();
        wm.Open("a");
        wm.SetLayout(MasterStackLayout.Left);

        Assert.False(wm.SetMasterCount(-1).Succeeded, "one is the floor and already the count");
        Assert.True(wm.SetMasterCount(100, absolute: true).Succeeded);
        Assert.Equal(8, ((MasterStackLayout)wm.FocusedWorkspace!.Layout).MasterCount);
    }

    // ---- floating windows off screen ----------------------------------------

    [Fact]
    public void AFloatingRectangleOffEveryScreenIsBroughtBack()
    {
        var work = new Rect(0, 0, 1920, 1080);

        // Left where a second display used to be: slid in to the nearest edge.
        Assert.Equal(new Rect(1920 - 400, 100, 400, 300), LayoutEngine.ReHomed(new Rect(2500, 100, 400, 300), work));

        // Above the top: down to the top edge.
        Assert.Equal(new Rect(200, 0, 400, 300), LayoutEngine.ReHomed(new Rect(200, -900, 400, 300), work));

        // Larger than the display: shrunk to fit.
        Assert.Equal(new Rect(0, 0, 1920, 1080), LayoutEngine.ReHomed(new Rect(-5000, -5000, 3000, 2000), work));
    }

    [Fact]
    public void AFloatingRectangleTouchingTheScreenIsTheUsersToKeep()
    {
        var work = new Rect(0, 0, 1920, 1080);
        var straddling = new Rect(1800, 100, 400, 300);

        Assert.Equal(straddling, LayoutEngine.ReHomed(straddling, work));
    }

    [Fact]
    public void TheEngineReHomesAFloatingWindowAndRemembersWhereItPutIt()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        wm.SetWindowState(a, WindowState.Floating);
        a.FloatingRect = new Rect(5000, 5000, 400, 300);

        wm.Arrange();

        Assert.Equal(new Rect(1920 - 400, 1080 - 300, 400, 300), a.Rect);
        Assert.Equal(a.Rect, a.FloatingRect);
    }
    // ---- the desktop's maximise and the tree's --------------------------------

    [Theory]
    [InlineData(WindowState.Tiling, true, MaximiseTransition.Enter)]
    [InlineData(WindowState.Floating, true, MaximiseTransition.Enter)]
    [InlineData(WindowState.Maximised, false, MaximiseTransition.Leave)]
    [InlineData(WindowState.Maximised, true, MaximiseTransition.None)]
    [InlineData(WindowState.Tiling, false, MaximiseTransition.None)]
    [InlineData(WindowState.Fullscreen, true, MaximiseTransition.None)]
    [InlineData(WindowState.MonitorFullscreen, true, MaximiseTransition.None)]
    [InlineData(WindowState.Minimised, false, MaximiseTransition.None)]
    public void TheFlagAndTheTreeAgreeOrTheTreeGivesWay(WindowState state, bool zoomed, MaximiseTransition expected)
    {
        // Win+Up on a tiled window enters Maximised; Win+Down on a Maximised one
        // leaves it. Fullscreen windows match the flag for their own reasons and are
        // not touched; a minimised one has nothing to say.
        Assert.Equal(expected, NativeMaximise.Decide(state, zoomed, inGrace: false));
    }

    [Fact]
    public void AChangeTheTreeJustMadeIsNotSecondGuessed()
    {
        // toggle-maximized sets the state; the committer zooms the window a pass
        // later and Windows applies it a frame after that. A look in between sees
        // Maximised with the flag still off, which is not the user undoing it.
        Assert.Equal(MaximiseTransition.None, NativeMaximise.Decide(WindowState.Maximised, zoomed: false, inGrace: true));
        Assert.Equal(MaximiseTransition.None, NativeMaximise.Decide(WindowState.Tiling, zoomed: true, inGrace: true));
        Assert.True(NativeMaximise.Grace >= TimeSpan.FromMilliseconds(500));
    }
}