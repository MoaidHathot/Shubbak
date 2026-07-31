using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for <see cref="LayoutEngine"/>: recursion, outer gaps, window states, and
/// the handling of inactive workspaces.
/// </summary>
public sealed class LayoutEngineTests
{
    private static (RootNode Root, MonitorNode Monitor, WorkspaceNode Workspace) Setup(
        int width = 1000, int height = 800, params Node[] children)
    {
        WorkspaceNode workspace = TreeBuilder.Workspace(children: children);
        MonitorNode monitor = TreeBuilder.Monitor(width: width, height: height);
        monitor.AddWorkspace(workspace);
        RootNode root = TreeBuilder.Root(monitor);
        return (root, monitor, workspace);
    }

    [Fact]
    public void InactiveWorkspacesAreStillArrangedButMarkedInvisible()
    {
        // Arranging hidden workspaces costs nothing and means switching to one shows
        // a correct layout immediately rather than a frame of stale geometry.
        WindowNode visible = TreeBuilder.Window("visible");
        WindowNode hidden = TreeBuilder.Window("hidden");

        WorkspaceNode ws1 = TreeBuilder.Workspace("1", children: visible);
        WorkspaceNode ws2 = TreeBuilder.Workspace("2", children: hidden);

        MonitorNode monitor = TreeBuilder.Monitor(width: 1000, height: 800);
        monitor.AddWorkspace(ws1);
        monitor.AddWorkspace(ws2);
        RootNode root = TreeBuilder.Root(monitor);

        IReadOnlyList<Placement> placements = new LayoutEngine().Arrange(root, ArrangeOptions.Default);

        Placement forVisible = placements.Single(p => p.Window == visible);
        Placement forHidden = placements.Single(p => p.Window == hidden);

        Assert.True(forVisible.Visible);
        Assert.False(forHidden.Visible);

        // Both received a real rectangle.
        Assert.Equal(new Rect(0, 0, 1000, 800), forVisible.Rect);
        Assert.Equal(new Rect(0, 0, 1000, 800), forHidden.Rect);
    }

    [Fact]
    public void FloatingWindowsDoNotConsumeATilingSlot()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode floating = TreeBuilder.Window("floating");
        WindowNode b = TreeBuilder.Window("b");

        (RootNode root, _, _) = Setup(1000, 800, a, floating, b);
        floating.State = WindowState.Floating;
        floating.FloatingRect = new Rect(100, 100, 300, 200);

        IReadOnlyList<Placement> placements = new LayoutEngine().Arrange(root, ArrangeOptions.Default);

        // a and b split the whole width as though the floating window were absent.
        Assert.Equal(new Rect(0, 0, 500, 800), placements.Single(p => p.Window == a).Rect);
        Assert.Equal(new Rect(500, 0, 500, 800), placements.Single(p => p.Window == b).Rect);

        // The floating window keeps the geometry the user gave it.
        Assert.Equal(new Rect(100, 100, 300, 200), placements.Single(p => p.Window == floating).Rect);
    }

    [Fact]
    public void FullscreenIgnoresGapsAndUsesTheWholeWorkArea()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode full = TreeBuilder.Window("full");

        (RootNode root, _, _) = Setup(1000, 800, a, full);
        full.State = WindowState.Fullscreen;

        var options = new ArrangeOptions(OuterGap: Gaps.All(20), InnerGap: 10);
        IReadOnlyList<Placement> placements = new LayoutEngine().Arrange(root, options);

        // Gaps are a tiling concept; a fullscreen window is by definition not tiled.
        Assert.Equal(new Rect(0, 0, 1000, 800), placements.Single(p => p.Window == full).Rect);

        // The remaining tiled window still takes the full gapped area, since the
        // fullscreen one has left the flow.
        Assert.Equal(new Rect(20, 20, 960, 760), placements.Single(p => p.Window == a).Rect);
    }

    [Fact]
    public void MinimisedWindowsAreAlwaysInvisible()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode minimised = TreeBuilder.Window("minimised");

        (RootNode root, _, _) = Setup(1000, 800, a, minimised);
        minimised.State = WindowState.Minimised;

        IReadOnlyList<Placement> placements = new LayoutEngine().Arrange(root, ArrangeOptions.Default);

        Assert.False(placements.Single(p => p.Window == minimised).Visible);
        Assert.Equal(new Rect(0, 0, 1000, 800), placements.Single(p => p.Window == a).Rect);
    }

    [Fact]
    public void ContainersHoldingOnlyFloatingWindowsDoNotOccupySpace()
    {
        // Workspace [ a | Column [ floatingB / floatingC ] ]
        // The column is entirely outside the tiling flow, so a must get everything.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");

        b.State = WindowState.Floating;
        c.State = WindowState.Floating;

        ContainerNode column = TreeBuilder.Column(b, c);
        (RootNode root, _, _) = Setup(1000, 800, a, column);

        IReadOnlyList<Placement> placements = new LayoutEngine().Arrange(root, ArrangeOptions.Default);

        Assert.Equal(new Rect(0, 0, 1000, 800), placements.Single(p => p.Window == a).Rect);
    }

    [Fact]
    public void MixedTiledAndFloatingChildrenKeepTiledProportions()
    {
        // The engine arranges tiled children through a temporary view. This checks
        // the view preserves relative sizes and leaves the real tree untouched.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode floating = TreeBuilder.Window("floating");
        WindowNode b = TreeBuilder.Window("b");

        (RootNode root, _, WorkspaceNode workspace) = Setup(1000, 800, a, floating, b);
        floating.State = WindowState.Floating;

        workspace.SetChildRatio(a, 0.6);

        double ratioA = a.SizeRatio;
        double ratioB = b.SizeRatio;
        int indexA = a.IndexInParent;

        IReadOnlyList<Placement> placements = new LayoutEngine().Arrange(root, ArrangeOptions.Default);

        int widthA = placements.Single(p => p.Window == a).Rect.Width;
        int widthB = placements.Single(p => p.Window == b).Rect.Width;

        Assert.Equal(1000, widthA + widthB);
        Assert.True(widthA > widthB, "a was given the larger ratio and must be wider");

        // The tree is exactly as it was: same parent, same order, same ratios.
        Assert.Same(workspace, a.Parent);
        Assert.Equal(indexA, a.IndexInParent);
        Assert.Equal(ratioA, a.SizeRatio, 1e-9);
        Assert.Equal(ratioB, b.SizeRatio, 1e-9);
    }

    [Fact]
    public void ArrangeIsIdempotent()
    {
        // Running a second pass must not shift anything. If the engine mutated the
        // tree, drift would appear as windows creeping on every relayout.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        ContainerNode column = TreeBuilder.Column(TreeBuilder.Window("c"), TreeBuilder.Window("d"));

        (RootNode root, _, _) = Setup(1000, 800, a, b, column);

        var engine = new LayoutEngine();
        Dictionary<NodeId, Rect> first = engine
            .Arrange(root, ArrangeOptions.Default)
            .ToDictionary(p => p.Window.Id, p => p.Rect);

        Dictionary<NodeId, Rect> second = engine
            .Arrange(root, ArrangeOptions.Default)
            .ToDictionary(p => p.Window.Id, p => p.Rect);

        Assert.Equal(first, second);
    }

    [Fact]
    public void MultipleMonitorsAreArrangedInTheirOwnCoordinateSpaces()
    {
        WindowNode left = TreeBuilder.Window("left");
        WindowNode right = TreeBuilder.Window("right");

        MonitorNode m1 = TreeBuilder.Monitor("\\\\.\\DISPLAY1", 0, 0, 1920, 1080);
        MonitorNode m2 = TreeBuilder.Monitor("\\\\.\\DISPLAY2", 1920, 0, 1280, 1024);

        m1.AddWorkspace(TreeBuilder.Workspace("1", children: left));
        m2.AddWorkspace(TreeBuilder.Workspace("2", children: right));

        RootNode root = TreeBuilder.Root(m1, m2);

        IReadOnlyList<Placement> placements = new LayoutEngine().Arrange(root, ArrangeOptions.Default);

        Assert.Equal(new Rect(0, 0, 1920, 1080), placements.Single(p => p.Window == left).Rect);

        // The second monitor's origin is its own, not the desktop's.
        Assert.Equal(new Rect(1920, 0, 1280, 1024), placements.Single(p => p.Window == right).Rect);
    }

    [Fact]
    public void WorkAreaRatherThanBoundsIsTiled()
    {
        // The work area excludes the taskbar and any docked appbar, including Taj.
        var bounds = new Rect(0, 0, 1920, 1080);
        var workArea = new Rect(0, 0, 1920, 1040);
        var monitor = new MonitorNode("\\\\.\\DISPLAY1", bounds, workArea);

        WindowNode a = TreeBuilder.Window("a");
        monitor.AddWorkspace(TreeBuilder.Workspace("1", children: a));
        RootNode root = TreeBuilder.Root(monitor);

        IReadOnlyList<Placement> placements = new LayoutEngine().Arrange(root, ArrangeOptions.Default);

        Assert.Equal(workArea, placements.Single(p => p.Window == a).Rect);
    }

    [Fact]
    public void EmptyWorkspaceProducesNoPlacements()
    {
        (RootNode root, _, _) = Setup(1000, 800);

        Assert.Empty(new LayoutEngine().Arrange(root, ArrangeOptions.Default));
    }

    [Fact]
    public void NestedContainerRectsAreRecordedOnTheContainersThemselves()
    {
        // The bar and the inspector both need container geometry, not just leaves.
        WindowNode a = TreeBuilder.Window("a");
        ContainerNode column = TreeBuilder.Column(TreeBuilder.Window("b"), TreeBuilder.Window("c"));

        (RootNode root, _, WorkspaceNode workspace) = Setup(1000, 800, a, column);

        new LayoutEngine().Arrange(root, ArrangeOptions.Default);

        Assert.Equal(new Rect(0, 0, 1000, 800), workspace.Rect);
        Assert.Equal(new Rect(500, 0, 500, 800), column.Rect);
    }
}
