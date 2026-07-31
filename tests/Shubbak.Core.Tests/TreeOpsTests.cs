using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for <see cref="TreeOps"/>, especially flattening.
/// </summary>
/// <remarks>
/// Flattening is the least visible and most important behaviour here. Redundant
/// containers - empty ones, or ones holding a single child - are invisible on
/// screen but change how focus and resize behave. If they accumulate, the window
/// manager develops "haunted" behaviour that cannot be diagnosed by looking at the
/// display.
/// </remarks>
public sealed class TreeOpsTests
{
    [Fact]
    public void DetachRemovesTheNodeAndFlattensWhatIsLeft()
    {
        // Workspace [ a | Column [ b / c ] ] - removing b leaves Column with one
        // child, which is redundant and must collapse.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode column = TreeBuilder.Column(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);

        TreeOps.Detach(b);

        Assert.Equal(2, workspace.Count);
        Assert.Same(a, workspace.Children[0]);
        Assert.Same(c, workspace.Children[1]);
        Assert.Null(column.Parent);
    }

    [Fact]
    public void FlattenCollapsesAChainOfRedundantContainers()
    {
        // Workspace [ a | Column [ Row [ b ] ] ] - two levels of single-child
        // nesting, both meaningless.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        ContainerNode inner = TreeBuilder.Row(b);
        ContainerNode outer = TreeBuilder.Column(inner);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, outer]);

        TreeOps.Flatten(inner);

        Assert.Equal(2, workspace.Count);
        Assert.Same(b, workspace.Children[1]);
    }

    [Fact]
    public void FlattenPreservesTheSizeOfTheCollapsedSlot()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        ContainerNode column = TreeBuilder.Column(b, TreeBuilder.Window("c"));
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);

        workspace.SetChildRatio(column, 0.7);
        TreeOps.Detach(column.Children[1]);

        // b inherits the container's 70% slot rather than resetting to 50%.
        Assert.Same(b, workspace.Children[1]);
        Assert.Equal(0.7, b.SizeRatio, 1e-6);
    }

    [Fact]
    public void EmptyWorkspaceIsNeverRemovedByFlattening()
    {
        // A declared workspace must survive going empty, or its keybinding breaks.
        WindowNode a = TreeBuilder.Window("a");
        WorkspaceNode workspace = TreeBuilder.Workspace("3", children: a);
        MonitorNode monitor = TreeBuilder.Monitor();
        monitor.AddWorkspace(workspace);

        TreeOps.Detach(a);

        Assert.True(workspace.IsEmpty);
        Assert.Same(monitor, workspace.Parent);
        Assert.Contains(workspace, monitor.Workspaces);
    }

    [Fact]
    public void WorkspaceAbsorbsASoleContainerChildIncludingItsLayout()
    {
        // Workspace(splith) [ Column [ b / c ] ] is indistinguishable on screen from
        // Workspace(splitv) [ b / c ], and the flatter form is what the user means.
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode column = TreeBuilder.Column(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(layout: SplitLayout.Horizontal, children: column);

        TreeOps.Flatten(workspace);

        Assert.Equal(2, workspace.Count);
        Assert.Same(SplitLayout.Vertical, workspace.Layout);
        Assert.Same(b, workspace.Children[0]);
        Assert.Same(c, workspace.Children[1]);
    }

    [Fact]
    public void WrapCreatesNestingInPlaceWithoutMovingAnything()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, b]);
        workspace.SetChildRatio(b, 0.6);

        ContainerNode wrapper = TreeOps.Wrap(b, SplitLayout.Vertical);

        Assert.Same(wrapper, workspace.Children[1]);
        Assert.Same(wrapper, b.Parent);
        Assert.Equal(0.6, wrapper.SizeRatio, 1e-6);
        Assert.Equal(1.0, b.SizeRatio, 1e-9);

        // Nothing visibly moves until a second window joins the wrapper.
        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace, width: 1000, height: 600);
        Assert.Equal(new Rect(400, 0, 600, 600), map["b"]);
    }

    [Fact]
    public void ReparentMovesBetweenContainersAndTidiesTheSource()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode column = TreeBuilder.Column(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);

        TreeOps.Reparent(b, workspace, 0);

        Assert.Same(b, workspace.Children[0]);

        // The column is down to one child, so it collapses.
        Assert.Equal(3, workspace.Count);
        Assert.DoesNotContain(column, workspace.Children);
    }

    [Fact]
    public void ReparentWithinTheSameContainerIsAMove()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, b, c]);

        TreeOps.Reparent(c, workspace, 0);

        Assert.Same(c, workspace.Children[0]);
        Assert.Equal(3, workspace.Count);
    }

    [Fact]
    public void ReparentIntoOwnDescendantThrows()
    {
        ContainerNode inner = TreeBuilder.Column(TreeBuilder.Window("b"));
        ContainerNode outer = TreeBuilder.Row(inner);
        _ = TreeBuilder.Workspace(children: outer);

        Assert.Throws<InvalidOperationException>(() => TreeOps.Reparent(outer, inner, 0));
    }

    [Fact]
    public void SwapAcrossDifferentContainersExchangesPlaces()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode column = TreeBuilder.Column(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);

        TreeOps.Swap(a, b);

        Assert.Same(b, workspace.Children[0]);
        Assert.Same(column, workspace.Children[1]);
        Assert.Same(a, column.Children[0]);
        Assert.Same(c, column.Children[1]);
    }

    [Fact]
    public void SwapKeepsSizesAttachedToSlots()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode column = TreeBuilder.Column(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);

        workspace.SetChildRatio(a, 0.3);
        column.SetChildRatio(b, 0.8);

        TreeOps.Swap(a, b);

        // Each window adopts the size of the slot it lands in; nothing resizes.
        Assert.Equal(0.3, b.SizeRatio, 1e-6);
        Assert.Equal(0.8, a.SizeRatio, 1e-6);
    }

    [Fact]
    public void SwapWithAnAncestorThrows()
    {
        WindowNode b = TreeBuilder.Window("b");
        ContainerNode column = TreeBuilder.Column(b, TreeBuilder.Window("c"));
        _ = TreeBuilder.Workspace(children: [TreeBuilder.Window("a"), column]);

        Assert.Throws<InvalidOperationException>(() => TreeOps.Swap(column, b));
    }

    [Fact]
    public void ToggleSplitDirectionFlipsTheAxis()
    {
        ContainerNode row = TreeBuilder.Row(TreeBuilder.Window("a"), TreeBuilder.Window("b"));

        Assert.Same(SplitLayout.Vertical, TreeOps.ToggleSplitDirection(row));
        Assert.Same(SplitLayout.Horizontal, TreeOps.ToggleSplitDirection(row));
    }

    [Fact]
    public void ToggleSplitDirectionOnAWorkspaceChangesItsOwnLayout()
    {
        // A workspace is a container, so this needs no special handling - which is
        // the whole point of layout living on containers.
        WorkspaceNode workspace = TreeBuilder.Workspace(
            layout: SplitLayout.Horizontal,
            children: [TreeBuilder.Window("a"), TreeBuilder.Window("b")]);

        TreeOps.ToggleSplitDirection(workspace);

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace, width: 1000, height: 600);
        Assert.Equal(new Rect(0, 0, 1000, 300), map["a"]);
        Assert.Equal(new Rect(0, 300, 1000, 300), map["b"]);
    }

    [Fact]
    public void NearestAncestorOnAxisSkipsContainersOnTheWrongAxis()
    {
        // Workspace(splith) [ a | Column [ b / c ] ]
        // Widening b cannot be satisfied by the column, so it must resolve to the
        // workspace above it.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode column = TreeBuilder.Column(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);

        Assert.Same(workspace, TreeOps.NearestAncestorOnAxis(b, Axis.Horizontal));
        Assert.Same(column, TreeOps.NearestAncestorOnAxis(b, Axis.Vertical));
    }

    [Fact]
    public void NearestAncestorOnAxisIgnoresSingleChildContainers()
    {
        // A container with one child cannot satisfy a resize: there is no sibling to
        // take the space from.
        WindowNode b = TreeBuilder.Window("b");
        ContainerNode lonely = TreeBuilder.Column(b);
        WorkspaceNode workspace = TreeBuilder.Workspace(
            layout: SplitLayout.Vertical,
            children: [TreeBuilder.Window("a"), lonely]);

        Assert.Same(workspace, TreeOps.NearestAncestorOnAxis(b, Axis.Vertical));
    }

    [Fact]
    public void ChildContainingFindsTheBranchLeadingToADescendant()
    {
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode inner = TreeBuilder.Column(TreeBuilder.Window("b"), c);
        ContainerNode outer = TreeBuilder.Row(TreeBuilder.Window("a"), inner);

        Assert.Same(inner, TreeOps.ChildContaining(outer, c));
        Assert.Null(TreeOps.ChildContaining(inner, outer));
    }

    [Fact]
    public void InsertByLayoutPlacesBesideTheReference()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, b]);

        WindowNode inserted = TreeBuilder.Window("new");
        TreeOps.InsertByLayout(workspace, inserted, a);

        Assert.Same(inserted, workspace.Children[1]);
    }
}
