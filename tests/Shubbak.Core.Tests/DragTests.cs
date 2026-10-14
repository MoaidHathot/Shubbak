using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for dragging a tiled window with the mouse.
/// </summary>
/// <remarks>
/// Dropping on the middle of another window swaps them; dropping near an edge
/// inserts beside it. Swap alone cannot express "put this to the left of that",
/// which is most of what anyone wants a mouse for, so both are covered here.
/// </remarks>
public sealed class DragTests
{
    /// <summary>Two windows side by side on a 1000x800 workspace.</summary>
    private static (WindowManager Wm, WindowNode Left, WindowNode Right) TwoAcross()
    {
        var wm = new WindowManager();
        wm.AddMonitor(TreeBuilder.Monitor(width: 1000, height: 800));
        wm.AddWorkspace(new WorkspaceNode("1"));
        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);

        WindowNode left = wm.Open("left");
        WindowNode right = wm.Open("right");

        wm.ComputePlacements();
        return (wm, left, right);
    }

    private static WorkspaceNode Workspace(WindowManager wm) => wm.FocusedWorkspace!;

    // ---- resolution --------------------------------------------------------

    [Fact]
    public void DroppingOnTheMiddleOfAWindowSwaps()
    {
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        // The right tile spans x 500-1000; its centre is 750.
        DropTarget? drop = DragResolver.Resolve(Workspace(wm), left, 750, 400);

        Assert.NotNull(drop);
        Assert.Equal(DropKind.Swap, drop.Value.Kind);
        Assert.Same(right, drop.Value.Target);
    }

    [Theory]
    [InlineData(520, 400, DropKind.Before, Axis.Horizontal)]   // left edge of the right tile
    [InlineData(980, 400, DropKind.After, Axis.Horizontal)]    // right edge
    [InlineData(750, 40, DropKind.Before, Axis.Vertical)]      // top edge
    [InlineData(750, 760, DropKind.After, Axis.Vertical)]      // bottom edge
    public void DroppingNearAnEdgeInsertsBesideIt(int x, int y, DropKind kind, Axis axis)
    {
        (WindowManager wm, WindowNode left, _) = TwoAcross();

        DropTarget? drop = DragResolver.Resolve(Workspace(wm), left, x, y);

        Assert.NotNull(drop);
        Assert.Equal(kind, drop.Value.Kind);
        Assert.Equal(axis, drop.Value.Axis);
    }

    [Fact]
    public void ACornerResolvesToTheNearestEdge()
    {
        // Ambiguous by construction; the nearest edge has to win, or a corner drop
        // would be unpredictable.
        (WindowManager wm, WindowNode left, _) = TwoAcross();

        // Near the right tile's top-left, but closer to the top than the left.
        DropTarget? drop = DragResolver.Resolve(Workspace(wm), left, 560, 10);

        Assert.NotNull(drop);
        Assert.Equal(Axis.Vertical, drop.Value.Axis);
        Assert.Equal(DropKind.Before, drop.Value.Kind);
    }

    [Fact]
    public void TheDraggedWindowIsNeverItsOwnTarget()
    {
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        // Dropped over its own tile.
        DropTarget? drop = DragResolver.Resolve(Workspace(wm), left, 250, 400);

        // The only other candidate is the right tile, and it is too far away to be a
        // near miss, so this is not a drop at all.
        Assert.True(drop is null || drop.Value.Target == right);
    }

    [Fact]
    public void ADropInAGapStillFindsTheNearestTile()
    {
        // Inner gaps mean there is real dead space between tiles; a drop landing in a
        // six-pixel gutter should do what the user obviously meant.
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        DropTarget? drop = DragResolver.Resolve(Workspace(wm), left, 501, 400);

        Assert.NotNull(drop);
        Assert.Same(right, drop.Value.Target);
    }

    [Fact]
    public void ADropFarFromEverythingResolvesToNothing()
    {
        (WindowManager wm, WindowNode left, _) = TwoAcross();

        Assert.Null(DragResolver.Resolve(Workspace(wm), left, 100_000, 100_000));
    }

    [Fact]
    public void FloatingWindowsAreNotDropTargets()
    {
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        wm.SetWindowState(right, WindowState.Floating);
        wm.ComputePlacements();

        Assert.Null(DragResolver.Resolve(Workspace(wm), left, 750, 400));
    }

    // ---- move versus resize ------------------------------------------------

    [Fact]
    public void ASmallNudgeIsNotAMove()
    {
        // Clicking a title bar produces a move of a pixel or two. Acting on it would
        // rearrange the layout every time the user focused a window by clicking.
        var before = new Rect(0, 0, 500, 800);
        var after = new Rect(3, 2, 500, 800);

        Assert.False(DragResolver.IsMove(before, after));
        Assert.False(DragResolver.IsResize(before, after));
    }

    [Fact]
    public void ADragAcrossTheScreenIsAMove()
    {
        Assert.True(DragResolver.IsMove(new Rect(0, 0, 500, 800), new Rect(600, 0, 500, 800)));
    }

    [Fact]
    public void ChangingTheSizeIsAResize()
    {
        Assert.True(DragResolver.IsResize(new Rect(0, 0, 500, 800), new Rect(0, 0, 700, 800)));
    }

    [Fact]
    public void AFewPixelsOfSizeChangeIsNotAResize()
    {
        // Windows adjusts a window's size by a pixel or two during some moves, and
        // treating that as a resize would silently perturb the layout ratios.
        Assert.False(DragResolver.IsResize(new Rect(0, 0, 500, 800), new Rect(0, 0, 502, 799)));
    }

    // ---- execution ---------------------------------------------------------

    [Fact]
    public void DroppingOnTheMiddleExchangesTheTwoWindows()
    {
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        WmResult result = wm.DropWindow(left, 750, 400);

        Assert.True(result.Succeeded);
        Assert.Equal([right, left], Workspace(wm).Children.Cast<WindowNode>());
    }

    [Fact]
    public void DroppingOnALeftEdgeInsertsBefore()
    {
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        // Drag the left window onto the right window's left edge: it should end up
        // between them, which is where it already is - so assert the order holds.
        wm.DropWindow(left, 520, 400);

        Assert.Equal([left, right], Workspace(wm).Children.Cast<WindowNode>());
    }

    [Fact]
    public void DroppingOnARightEdgeMovesPastTheTarget()
    {
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        wm.DropWindow(left, 980, 400);

        Assert.Equal([right, left], Workspace(wm).Children.Cast<WindowNode>());
    }

    [Fact]
    public void DroppingOnACrossAxisEdgeStacksTheWindows()
    {
        // The interesting case: dropping onto the top of a window in a horizontal
        // row means "stack us vertically there".
        //
        // Asserted by the resulting geometry rather than by tree shape, because a
        // workspace left holding a single container is flattened into it - so the
        // outcome here is a vertical workspace rather than a nested column. Visually
        // identical, and flatter, which is the whole point of that flattening.
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        WmResult result = wm.DropWindow(left, 750, 40);

        Assert.True(result.Succeeded);

        wm.ComputePlacements();

        Assert.True(left.Rect.Bottom <= right.Rect.Top,
            $"dropped above, so it should sit higher: left={left.Rect} right={right.Rect}");

        Assert.Equal(1000, left.Rect.Width);
        Assert.Equal(1000, right.Rect.Width);
    }

    [Fact]
    public void CrossAxisDropInsideANestedContainerCreatesRealNesting()
    {
        // Where the flattening does not apply: with a third window present the
        // workspace keeps more than one child, so the new container survives.
        (WindowManager wm, WindowNode a, WindowNode b) = TwoAcross();

        WindowNode c = wm.Open("c");
        wm.ComputePlacements();

        // Drop a onto b's top edge; the workspace still holds c alongside.
        wm.DropWindow(a, b.Rect.CenterX, b.Rect.Top + 10);

        WorkspaceNode workspace = Workspace(wm);
        ContainerNode wrapper = Assert.Single(workspace.Children.OfType<ContainerNode>());

        Assert.Same(SplitLayout.Vertical, wrapper.Layout);
        Assert.Equal([a, b], wrapper.Children.Cast<WindowNode>());
        Assert.Contains(c, workspace.Children);
    }

    [Fact]
    public void NestingFromADropArrangesTheWindowsAsDropped()
    {
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        wm.DropWindow(left, 750, 760);   // bottom edge - below the target
        wm.ComputePlacements();

        Assert.True(left.Rect.Top > right.Rect.Top,
            $"dropped below, so it should sit lower: left={left.Rect} right={right.Rect}");
    }

    [Fact]
    public void DroppingOnNothingIsRejectedSoTheCallerCanSnapBack()
    {
        (WindowManager wm, WindowNode left, _) = TwoAcross();

        WmResult result = wm.DropWindow(left, 100_000, 100_000);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public void DraggingAFloatingWindowIsRejected()
    {
        // Floating windows keep whatever geometry the user gave them; re-tiling one
        // because it was dragged would defeat the point of floating it.
        (WindowManager wm, WindowNode left, _) = TwoAcross();

        wm.SetWindowState(left, WindowState.Floating);

        Assert.False(wm.DropWindow(left, 750, 400).Succeeded);
    }

    [Fact]
    public void DroppingOntoAnotherMonitorMovesTheWindowThere()
    {
        var wm = new WindowManager();
        wm.AddMonitor(TreeBuilder.Monitor("\\\\.\\DISPLAY1", x: 0, width: 1000, height: 800));
        wm.AddMonitor(TreeBuilder.Monitor("\\\\.\\DISPLAY2", x: 1000, width: 1000, height: 800));

        wm.AddWorkspace(new WorkspaceNode("1"), wm.Root.Monitors[0]);
        wm.AddWorkspace(new WorkspaceNode("2"), wm.Root.Monitors[1]);
        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);

        WindowNode window = wm.Open("dragged");
        wm.ComputePlacements();

        // The second monitor's workspace is empty, so there is nothing to land
        // beside - it becomes a plain move.
        WmResult result = wm.DropWindow(window, 1500, 400);

        Assert.True(result.Succeeded);
        Assert.Equal("2", window.Workspace!.Name);
    }

    [Fact]
    public void DroppingLeavesNoRedundantContainersBehind()
    {
        // Workspace [ a | Column [ b / c ] ] - dragging b out must collapse the
        // column, or invisible structure accumulates and focus starts behaving oddly.
        (WindowManager wm, WindowNode a, WindowNode b) = TwoAcross();

        wm.FocusWindow(b);
        wm.Split(SplitLayout.Vertical);
        WindowNode c = wm.Open("c");
        wm.ComputePlacements();

        wm.DropWindow(b, a.Rect.CenterX, a.Rect.CenterY);

        WorkspaceNode workspace = Workspace(wm);

        Assert.All(workspace.SelfAndDescendants().OfType<ContainerNode>(),
            container => Assert.True(
                container is WorkspaceNode || container.Count > 1,
                "a container was left with a single child"));

        Assert.Contains(c, workspace.DescendantWindows());
    }

    // ---- drag to resize ----------------------------------------------------

    [Fact]
    public void DraggingABorderAdjustsTheRatio()
    {
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        // The left tile is 500 wide; the user drags its right border to 700.
        WmResult result = wm.ResizeFromDrag(left, new Rect(0, 0, 700, 800));

        Assert.True(result.Succeeded);

        wm.ComputePlacements();

        Assert.Equal(700, left.Rect.Width);
        Assert.Equal(300, right.Rect.Width);
    }

    [Fact]
    public void ResizingKeepsTheContainerFull()
    {
        (WindowManager wm, WindowNode left, WindowNode right) = TwoAcross();

        wm.ResizeFromDrag(left, new Rect(0, 0, 320, 800));
        wm.ComputePlacements();

        Assert.Equal(1000, left.Rect.Width + right.Rect.Width);
    }

    [Fact]
    public void ResizingAcrossANestedContainerAppliesAtTheRightAncestor()
    {
        // Workspace(splith) [ a | Column [ b / c ] ]
        // Widening b horizontally cannot be satisfied by the column, so it must
        // widen the column itself.
        (WindowManager wm, WindowNode a, WindowNode b) = TwoAcross();

        wm.FocusWindow(b);
        wm.Split(SplitLayout.Vertical);
        wm.Open("c");
        wm.ComputePlacements();

        wm.ResizeFromDrag(b, new Rect(b.Rect.X, b.Rect.Y, 700, b.Rect.Height));
        wm.ComputePlacements();

        Assert.Equal(300, a.Rect.Width);
        Assert.Equal(700, b.Rect.Width);
    }

    [Fact]
    public void ResizingWithNoChangeIsRejected()
    {
        (WindowManager wm, WindowNode left, _) = TwoAcross();

        Assert.False(wm.ResizeFromDrag(left, left.Rect).Succeeded);
    }

    [Fact]
    public void ResizingAFloatingWindowIsRejected()
    {
        (WindowManager wm, WindowNode left, _) = TwoAcross();

        wm.SetWindowState(left, WindowState.Floating);

        Assert.False(wm.ResizeFromDrag(left, new Rect(0, 0, 700, 800)).Succeeded);
    }

    [Fact]
    public void ResizingReportsTheContainerAsResized()
    {
        // As the keyboard's resize does. The daemon chooses the animation profile from
        // the events, and a mouse resize that reported nothing was shown as a move,
        // while nothing listening to the tree heard that it had happened at all.
        (WindowManager wm, WindowNode left, _) = TwoAcross();

        WmResult result = wm.ResizeFromDrag(left, new Rect(0, 0, 700, 800));

        Assert.True(result.Has<ContainerResized>());
    }

    [Fact]
    public void ResizingWithGapsGivesExactlyTheWidthAsked()
    {
        // The share used to be the asked width over the container's, and the
        // container's includes the gap between the tiles, so a drag fell short of
        // where the border was let go by the gap's share of it.
        var wm = new WindowManager(new WmOptions { InnerGap = 10 });
        wm.AddMonitor(TreeBuilder.Monitor(width: 1000, height: 800));
        wm.AddWorkspace(new WorkspaceNode("1"));
        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);

        WindowNode left = wm.Open("left");
        WindowNode right = wm.Open("right");
        wm.ComputePlacements();

        wm.ResizeFromDrag(left, left.Rect with { Width = 700 });
        wm.ComputePlacements();

        Assert.Equal(700, left.Rect.Width);
        Assert.Equal(290, right.Rect.Width);
    }

    [Fact]
    public void ResizingOnASecondMonitorIsMeasuredThere()
    {
        // Nothing in the arithmetic may depend on where the monitor is: a window on a
        // display to the right of the primary has an x in the thousands, and a share
        // computed from an edge rather than a width would be nonsense there.
        var wm = new WindowManager();
        wm.AddMonitor(TreeBuilder.Monitor("\\\\.\\DISPLAY1", x: 0, width: 1920, height: 1080));
        wm.AddMonitor(TreeBuilder.Monitor("\\\\.\\DISPLAY2", x: 1920, width: 1000, height: 800));

        wm.AddWorkspace(new WorkspaceNode("1"), wm.Root.Monitors[0]);
        wm.AddWorkspace(new WorkspaceNode("2"), wm.Root.Monitors[1]);
        wm.ActivateWorkspace(wm.Root.Monitors[1].Workspaces[0]);

        WindowNode left = wm.Open("left");
        WindowNode right = wm.Open("right");
        wm.ComputePlacements();

        Assert.Equal(1920, left.Rect.X);

        wm.ResizeFromDrag(left, left.Rect with { Width = left.Rect.Width - 20 });
        wm.ComputePlacements();

        Assert.Equal(480, left.Rect.Width);
        Assert.Equal(520, right.Rect.Width);
    }

    [Fact]
    public void AnAxisTheShareDoesNotGovernIsLeftAlone()
    {
        // The reported case. A spiral of two is split left/right, so both windows have
        // the whole height, and a drag that only touched the width arrived with a
        // height a few pixels off - the shadow, before the daemon stopped passing it.
        // That height was read as a share of more than one, clamped to the most a
        // child may have, and the spiral's one ratio sizes a window in both
        // directions: a border nudged inwards gave the window almost the whole screen,
        // with the other left a sliver.
        WindowManager wm = WmFixture.Create();
        wm.SetLayout(FibonacciLayout.Horizontal);
        WindowNode firefox = wm.Open("firefox");
        WindowNode terminal = wm.Open("terminal");
        wm.Arrange();

        Rect current = firefox.Rect;
        var dragged = new Rect(current.X, current.Y, current.Width - 20, current.Height + 7);

        Assert.True(wm.ResizeFromDrag(firefox, dragged).Succeeded);
        wm.Arrange();

        Assert.Equal(current.Width - 20, firefox.Rect.Width);
        Assert.Equal(1920 - firefox.Rect.Width, terminal.Rect.Width);
        Assert.Equal(current.Height, firefox.Rect.Height);
    }

    [Theory]
    [InlineData(2, 0, 20)]
    [InlineData(2, 0, -20)]
    [InlineData(3, 0, 20)]
    [InlineData(3, 0, -20)]
    [InlineData(4, 0, 20)]
    [InlineData(4, 1, 20)]
    [InlineData(5, 2, -20)]
    public void DraggingAnEdgeInASpiralMovesItByTheDrag(int windows, int index, int by)
    {
        // The spiral gives a child own ÷ (own + the average of the rest), not
        // own ÷ total, so storing the pixel share as the ratio overshot by more the
        // more windows there were: with three, a drag of twenty pixels moved the edge
        // by three hundred, and with six a nudge took the window to the clamp.
        WindowManager wm = WmFixture.Create();
        wm.SetLayout(FibonacciLayout.Horizontal);

        var opened = new WindowNode[windows];
        for (int i = 0; i < windows; i++) opened[i] = wm.Open($"w{i}");
        wm.Arrange();

        WindowNode window = opened[index];
        Rect current = window.Rect;

        Assert.True(wm.ResizeFromDrag(window, current with { Width = current.Width + by }).Succeeded);
        wm.Arrange();

        // Only the width: an inner window of the spiral is sized in both directions
        // by its one weight, by design, so its height moves with it.
        Assert.Equal(current.Width + by, window.Rect.Width);
    }

    [Fact]
    public void DraggingTheMasterEdgeMovesTheDividerByTheDrag()
    {
        // Master's share is its weight against the stack's per-window weight, which
        // is not the pixel share either.
        WindowManager wm = WmFixture.Create();
        wm.SetLayout(MasterStackLayout.Left);
        WindowNode master = wm.Open("master");
        wm.Open("s1");
        wm.Open("s2");
        wm.Open("s3");
        wm.Arrange();

        Rect current = master.Rect;

        Assert.True(wm.ResizeFromDrag(master, current with { Width = current.Width + 30 }).Succeeded);
        wm.Arrange();

        Assert.Equal(current.Width + 30, master.Rect.Width);
    }

    [Fact]
    public void DraggingPastWhatAnyShareCanGiveStopsAtTheLimit()
    {
        // The spiral never gives a child more than 95% of a division, so a border
        // dragged beyond that lands on the limit rather than being refused - which is
        // what a border dragged past its limit does everywhere.
        WindowManager wm = WmFixture.Create();
        wm.SetLayout(FibonacciLayout.Horizontal);
        WindowNode first = wm.Open("first");
        WindowNode second = wm.Open("second");
        wm.Arrange();

        Assert.True(wm.ResizeFromDrag(first, first.Rect with { Width = 1900 }).Succeeded);
        wm.Arrange();

        Assert.Equal(1824, first.Rect.Width);
        Assert.Equal(96, second.Rect.Width);
    }
}
