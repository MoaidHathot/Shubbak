using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for <see cref="SplitLayout"/>, the manual-split layout that P1 ships.
/// </summary>
/// <remarks>
/// The recurring assertion is <b>exact tiling</b>: children must cover their
/// parent's extent with no gap and no overlap, for every child count and every
/// area size. Getting this wrong produces a one-pixel seam at the screen edge that
/// looks like a rendering artefact and is very hard to trace back to rounding.
/// </remarks>
public sealed class SplitLayoutTests
{
    [Fact]
    public void SingleChildFillsArea()
    {
        WindowNode window = TreeBuilder.Window("a");
        WorkspaceNode workspace = TreeBuilder.Workspace(children: window);

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace);

        Assert.Equal(new Rect(0, 0, 1920, 1080), map["a"]);
    }

    [Fact]
    public void TwoChildrenSplitHorizontallyDownTheMiddle()
    {
        WorkspaceNode workspace = TreeBuilder.Workspace(
            children: [TreeBuilder.Window("a"), TreeBuilder.Window("b")]);

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace);

        Assert.Equal(new Rect(0, 0, 960, 1080), map["a"]);
        Assert.Equal(new Rect(960, 0, 960, 1080), map["b"]);
    }

    [Fact]
    public void TwoChildrenSplitVertically()
    {
        WorkspaceNode workspace = TreeBuilder.Workspace(
            layout: SplitLayout.Vertical,
            children: [TreeBuilder.Window("a"), TreeBuilder.Window("b")]);

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace);

        Assert.Equal(new Rect(0, 0, 1920, 540), map["a"]);
        Assert.Equal(new Rect(0, 540, 1920, 540), map["b"]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(17)]
    public void ChildrenTileParentExactly(int count)
    {
        // 1000 divides badly by 3, 7, 11 and 17, so any per-child rounding shows up.
        var workspace = new WorkspaceNode("1", SplitLayout.Horizontal);
        for (int i = 0; i < count; i++) workspace.Add(TreeBuilder.Window($"w{i}"));

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace, width: 1000, height: 800);

        List<Rect> rects = [.. Enumerable.Range(0, count).Select(i => map[$"w{i}"])];

        Assert.Equal(0, rects[0].Left);
        Assert.Equal(1000, rects[^1].Right);

        for (int i = 1; i < count; i++)
            Assert.Equal(rects[i - 1].Right, rects[i].Left);

        Assert.Equal(1000, rects.Sum(r => r.Width));
        Assert.All(rects, r => Assert.Equal(800, r.Height));
    }

    [Theory]
    [InlineData(3, 1001)]
    [InlineData(3, 1002)]
    [InlineData(7, 1365)]
    [InlineData(9, 999)]
    public void ExactTilingHoldsForAwkwardWidths(int count, int width)
    {
        var workspace = new WorkspaceNode("1", SplitLayout.Horizontal);
        for (int i = 0; i < count; i++) workspace.Add(TreeBuilder.Window($"w{i}"));

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace, width: width, height: 600);

        List<Rect> rects = [.. Enumerable.Range(0, count).Select(i => map[$"w{i}"])];

        Assert.Equal(width, rects.Sum(r => r.Width));
        Assert.Equal(width, rects[^1].Right);
    }

    [Fact]
    public void InnerGapSeparatesSiblingsWithoutLeakingAtTheEdges()
    {
        WorkspaceNode workspace = TreeBuilder.Workspace(
            children: [TreeBuilder.Window("a"), TreeBuilder.Window("b"), TreeBuilder.Window("c")]);

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(
            workspace, new ArrangeOptions(InnerGap: 10), width: 1000, height: 600);

        Rect a = map["a"], b = map["b"], c = map["c"];

        // The outer edges stay flush with the workspace; only the interior gaps eat space.
        Assert.Equal(0, a.Left);
        Assert.Equal(1000, c.Right);

        Assert.Equal(10, b.Left - a.Right);
        Assert.Equal(10, c.Left - b.Right);

        // 1000 - (2 gaps * 10) = 980 of usable width.
        Assert.Equal(980, a.Width + b.Width + c.Width);
    }

    [Fact]
    public void OuterGapInsetsTheWorkspaceFromTheWorkArea()
    {
        WorkspaceNode workspace = TreeBuilder.Workspace(children: TreeBuilder.Window("a"));

        var options = new ArrangeOptions(OuterGap: new Gaps(4, 26, 4, 4));
        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace, options);

        // Mirrors the outer_gap in the author's GlazeWM config: a tall top gap
        // reserving room for the bar.
        Assert.Equal(new Rect(4, 26, 1912, 1050), map["a"]);
    }

    [Fact]
    public void SizeRatiosDetermineProportions()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, b]);

        workspace.SetChildRatio(a, 0.75);

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace, width: 1000, height: 600);

        Assert.Equal(750, map["a"].Width);
        Assert.Equal(250, map["b"].Width);
        Assert.Equal(1000, map["a"].Width + map["b"].Width);
    }

    [Fact]
    public void NestedContainersComposeWithoutCooperation()
    {
        // Row [ a | Column [ b / c ] ] - the canonical nested arrangement.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");

        ContainerNode column = TreeBuilder.Column(b, c);
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, column]);

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace, width: 1000, height: 800);

        Assert.Equal(new Rect(0, 0, 500, 800), map["a"]);
        Assert.Equal(new Rect(500, 0, 500, 400), map["b"]);
        Assert.Equal(new Rect(500, 400, 500, 400), map["c"]);
    }

    [Fact]
    public void MinimumTileExtentIsHonouredWhenRatiosWouldStarveAChild()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, b]);

        // Push b far below the floor.
        workspace.SetChildRatio(a, 0.999);

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(
            workspace,
            new ArrangeOptions(MinimumTileExtent: 50),
            width: 1000,
            height: 600);

        Assert.True(map["b"].Width >= 50, $"expected b >= 50, got {map["b"].Width}");
        Assert.Equal(1000, map["a"].Width + map["b"].Width);
    }

    [Fact]
    public void MinimumTileExtentDoesNotDistortLegalLopsidedSplits()
    {
        // A 90/10 split is unusual but perfectly legal at this size, so the floor
        // must not quietly rebalance it.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [a, b]);

        workspace.SetChildRatio(a, 0.9);

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(
            workspace,
            new ArrangeOptions(MinimumTileExtent: 24),
            width: 1000,
            height: 600);

        Assert.Equal(900, map["a"].Width);
        Assert.Equal(100, map["b"].Width);
    }

    [Fact]
    public void ResolveInsertIndexPlacesAfterTheReference()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        ContainerNode row = TreeBuilder.Row(a, b, c);

        Assert.Equal(1, SplitLayout.Horizontal.ResolveInsertIndex(row, a));
        Assert.Equal(3, SplitLayout.Horizontal.ResolveInsertIndex(row, c));
        Assert.Equal(3, SplitLayout.Horizontal.ResolveInsertIndex(row, null));
    }

    [Fact]
    public void NavigateReturnsNullAcrossTheContainerAxis()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        ContainerNode row = TreeBuilder.Row(a, b);

        // A horizontal split can answer left/right but not up/down; returning null
        // is what lets the caller retry on the parent.
        Assert.Same(b, SplitLayout.Horizontal.Navigate(row, a, Direction.Right));
        Assert.Null(SplitLayout.Horizontal.Navigate(row, a, Direction.Down));
        Assert.Null(SplitLayout.Horizontal.Navigate(row, a, Direction.Left));
    }

    [Fact]
    public void TransposedFlipsTheAxis()
    {
        Assert.Same(SplitLayout.Vertical, SplitLayout.Horizontal.Transposed);
        Assert.Same(SplitLayout.Horizontal, SplitLayout.Vertical.Transposed);
    }
}
