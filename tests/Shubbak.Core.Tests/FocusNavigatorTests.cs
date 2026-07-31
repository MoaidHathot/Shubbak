using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for <see cref="FocusNavigator"/>.
/// </summary>
/// <remarks>
/// Directional focus is where nested layouts are most likely to surprise a user, so
/// these tests describe arrangements by their on-screen geometry rather than by
/// tree shape. Every case must run an arrange pass first: descent picks by
/// rectangle overlap, so navigation is only meaningful once rectangles exist.
/// </remarks>
public sealed class FocusNavigatorTests
{
    /// <summary>
    /// Arranges the workspace so that <see cref="Node.Rect"/> is populated, which
    /// directional descent depends on.
    /// </summary>
    private static void Arrange(WorkspaceNode workspace, int width = 1000, int height = 800)
    {
        MonitorNode monitor = TreeBuilder.Monitor(width: width, height: height);
        monitor.AddWorkspace(workspace);
        _ = TreeBuilder.Root(monitor);

        new LayoutEngine().Arrange(workspace, ArrangeOptions.Default);
    }

    [Fact]
    public void MovesBetweenSiblingsAlongTheContainerAxis()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, b]);
        Arrange(workspace);

        Assert.Same(b, FocusNavigator.Navigate(a, Direction.Right));
        Assert.Same(a, FocusNavigator.Navigate(b, Direction.Left));
    }

    [Fact]
    public void ReturnsNullAtTheWorkspaceEdge()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, b]);
        Arrange(workspace);

        // Crossing to another monitor is the command layer's decision, not ours.
        Assert.Null(FocusNavigator.Navigate(a, Direction.Left));
        Assert.Null(FocusNavigator.Navigate(b, Direction.Right));
        Assert.Null(FocusNavigator.Navigate(a, Direction.Up));
    }

    [Fact]
    public void EscapesANestedSplitByAskingTheParent()
    {
        // Workspace(splith) [ a | Column [ b / c ] ]
        // A vertical split cannot answer "left", so the question must rise to the
        // workspace, which can.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode column = TreeBuilder.Column(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);
        Arrange(workspace);

        Assert.Same(a, FocusNavigator.Navigate(b, Direction.Left));
        Assert.Same(a, FocusNavigator.Navigate(c, Direction.Left));
    }

    [Fact]
    public void DescendsIntoTheChildPhysicallyBeside()
    {
        // Workspace [ a | Column [ b(top) / c(bottom) ] ]
        // Layout: a is full height on the left; b occupies the top-right quadrant
        // and c the bottom-right.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode column = TreeBuilder.Column(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);
        Arrange(workspace);

        // a spans the full height, so it overlaps b and c equally. The tie is broken
        // by centre distance, which is also equal, so the first candidate wins -
        // stable and predictable, which is what matters.
        WindowNode? target = FocusNavigator.Navigate(a, Direction.Right);
        Assert.NotNull(target);
        Assert.Contains(target, new[] { b, c });
    }

    [Fact]
    public void DescentPicksByOverlapNotByOrder()
    {
        // Workspace(splitv) [ Row [ a | b ] / Row [ c | d ] ]
        //   a b
        //   c d
        // Moving down from a must land on c, not d, even though d is also below.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        WindowNode d = TreeBuilder.Window("d");

        ContainerNode top = TreeBuilder.Row(a, b);
        ContainerNode bottom = TreeBuilder.Row(c, d);
        WorkspaceNode workspace = TreeBuilder.Workspace(
            layout: SplitLayout.Vertical, children: [top, bottom]);
        Arrange(workspace);

        Assert.Same(c, FocusNavigator.Navigate(a, Direction.Down));
        Assert.Same(d, FocusNavigator.Navigate(b, Direction.Down));
        Assert.Same(a, FocusNavigator.Navigate(c, Direction.Up));
        Assert.Same(b, FocusNavigator.Navigate(d, Direction.Up));
    }

    [Fact]
    public void DescentEntersARowAtItsNearEdge()
    {
        // Workspace(splitv) [ a / Row [ b | c ] ]
        // Moving down from the full-width a enters the row from the left, so b.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode row = TreeBuilder.Row(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(
            layout: SplitLayout.Vertical, children: [a, row]);
        Arrange(workspace);

        Assert.Same(b, FocusNavigator.Navigate(a, Direction.Down));
    }

    [Fact]
    public void NavigatesThroughThreeLevelsOfNesting()
    {
        // Workspace(splith) [ a | Column [ b / Row [ c | d ] ] ]
        //   +---+-------+
        //   |   |   b   |
        //   | a +---+---+
        //   |   | c | d |
        //   +---+---+---+
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        WindowNode d = TreeBuilder.Window("d");

        ContainerNode innerRow = TreeBuilder.Row(c, d);
        ContainerNode column = TreeBuilder.Column(b, innerRow);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);
        Arrange(workspace);

        Assert.Same(d, FocusNavigator.Navigate(c, Direction.Right));
        Assert.Same(c, FocusNavigator.Navigate(d, Direction.Left));

        // Up from c leaves the inner row and lands on b.
        Assert.Same(b, FocusNavigator.Navigate(c, Direction.Up));

        // Left from c escapes two levels to reach a.
        Assert.Same(a, FocusNavigator.Navigate(c, Direction.Left));
    }

    [Fact]
    public void SkipsFloatingWindows()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode floating = TreeBuilder.Window("floating");
        WindowNode b = TreeBuilder.Window("b");
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, floating, b]);

        floating.State = WindowState.Floating;
        Arrange(workspace);

        // Floating windows are outside the tiling flow, so directional movement
        // passes straight over them.
        Assert.Same(b, FocusNavigator.Navigate(a, Direction.Right));
    }

    [Fact]
    public void NavigateToNodeReturnsTheSubtreeNotTheLeaf()
    {
        // move --direction needs the neighbouring container, so that a window lands
        // beside it rather than inside whichever leaf happens to be nearest.
        WindowNode a = TreeBuilder.Window("a");
        ContainerNode column = TreeBuilder.Column(TreeBuilder.Window("b"), TreeBuilder.Window("c"));
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);
        Arrange(workspace);

        Assert.Same(column, FocusNavigator.NavigateToNode(a, Direction.Right));
    }

    [Fact]
    public void CycleWalksEveryWindowRegardlessOfNesting()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode column = TreeBuilder.Column(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);
        Arrange(workspace);

        Assert.Same(b, FocusNavigator.Cycle(workspace, a, forward: true));
        Assert.Same(c, FocusNavigator.Cycle(workspace, b, forward: true));

        // Wraps at both ends.
        Assert.Same(a, FocusNavigator.Cycle(workspace, c, forward: true));
        Assert.Same(c, FocusNavigator.Cycle(workspace, a, forward: false));
    }

    [Fact]
    public void CycleOnAnEmptyWorkspaceReturnsNull()
    {
        WorkspaceNode workspace = TreeBuilder.Workspace();
        Assert.Null(FocusNavigator.Cycle(workspace, null, forward: true));
    }
}
