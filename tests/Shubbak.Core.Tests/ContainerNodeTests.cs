using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for <see cref="ContainerNode"/>, focused on the size-ratio invariant:
/// children's ratios always sum to 1.0 while the container is non-empty.
/// </summary>
/// <remarks>
/// This invariant is load-bearing. If it drifts, every layout that divides by ratio
/// produces windows that no longer fill the screen - and because the drift is
/// gradual, the symptom appears long after the mutation that caused it.
/// </remarks>
public sealed class ContainerNodeTests
{
    private const double Tolerance = 1e-9;

    private static void AssertNormalised(ContainerNode container)
    {
        if (container.Count == 0) return;

        double total = container.Children.Sum(c => c.SizeRatio);
        Assert.True(
            Math.Abs(total - 1.0) < Tolerance,
            $"Ratios sum to {total:R}, expected 1.0. " +
            $"Individual: [{string.Join(", ", container.Children.Select(c => c.SizeRatio.ToString("F6", null)))}]");
    }

    [Fact]
    public void InsertGivesEachChildAnEqualShare()
    {
        var row = new ContainerNode(SplitLayout.Horizontal);

        for (int i = 1; i <= 4; i++)
        {
            row.Add(TreeBuilder.Window($"w{i}"));
            AssertNormalised(row);
            Assert.All(row.Children, c => Assert.Equal(1.0 / i, c.SizeRatio, Tolerance));
        }
    }

    [Fact]
    public void InsertPreservesRelativeSizesOfExistingChildren()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        var row = TreeBuilder.Row(a, b);

        row.SetChildRatio(a, 0.8);   // a:b is now 4:1

        row.Add(TreeBuilder.Window("c"));
        AssertNormalised(row);

        // The newcomer takes 1/3; a and b share the rest in their original 4:1 ratio.
        Assert.Equal(1.0 / 3.0, row.Children[2].SizeRatio, Tolerance);
        Assert.Equal(4.0, a.SizeRatio / b.SizeRatio, 1e-6);
    }

    [Fact]
    public void RemoveRedistributesProportionally()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        var row = TreeBuilder.Row(a, b, c);

        row.SetChildRatio(a, 0.5);
        double ratioBefore = b.SizeRatio / c.SizeRatio;

        row.Remove(a);

        AssertNormalised(row);
        Assert.Equal(2, row.Count);
        Assert.Equal(ratioBefore, b.SizeRatio / c.SizeRatio, 1e-6);
    }

    [Fact]
    public void RemovedChildIsDetachedAndReset()
    {
        WindowNode a = TreeBuilder.Window("a");
        var row = TreeBuilder.Row(a, TreeBuilder.Window("b"));

        row.Remove(a);

        Assert.Null(a.Parent);
        Assert.Equal(-1, a.IndexInParent);
        Assert.Equal(1.0, a.SizeRatio);
    }

    [Fact]
    public void RatiosSurviveManyInsertRemoveCycles()
    {
        // Floating point drift is cumulative, so a single round trip proves nothing.
        var row = new ContainerNode(SplitLayout.Horizontal);
        var windows = new List<Node>();

        for (int i = 0; i < 5; i++)
        {
            WindowNode w = TreeBuilder.Window($"seed{i}");
            row.Add(w);
            windows.Add(w);
        }

        for (int cycle = 0; cycle < 500; cycle++)
        {
            WindowNode added = TreeBuilder.Window($"churn{cycle}");
            row.Add(added);
            row.SetChildRatio(added, 0.37);
            row.Remove(added);
        }

        AssertNormalised(row);
        Assert.Equal(5, row.Count);
        Assert.All(row.Children, c => Assert.True(c.SizeRatio > 0));
    }

    [Fact]
    public void SetChildRatioLeavesOtherSiblingsProportional()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        var row = TreeBuilder.Row(a, b, c);

        // Establish a deliberately uneven relationship between b and c: after this
        // they hold 0.5 and 0.25, i.e. 2:1.
        row.SetChildRatio(b, 0.5);
        double siblingRatioBefore = b.SizeRatio / c.SizeRatio;
        Assert.Equal(2.0, siblingRatioBefore, 1e-6);

        // Growing a must take space from b and c *proportionally*, so their
        // relationship to each other is untouched. This is what makes dragging one
        // border feel local rather than rearranging the whole container.
        row.SetChildRatio(a, 0.4);

        AssertNormalised(row);
        Assert.Equal(0.4, a.SizeRatio, 1e-6);
        Assert.Equal(siblingRatioBefore, b.SizeRatio / c.SizeRatio, 1e-6);
    }

    [Fact]
    public void SetChildRatioClampsSoSiblingsCannotBeCollapsed()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        var row = TreeBuilder.Row(a, b);

        row.SetChildRatio(a, 5.0);

        AssertNormalised(row);
        Assert.True(b.SizeRatio >= Node.MinSizeRatio,
            $"sibling collapsed to {b.SizeRatio}, which would make it unclickable");
        Assert.True(a.SizeRatio < 1.0);
    }

    [Fact]
    public void SetChildRatioOnSoleChildIsAlwaysFull()
    {
        WindowNode a = TreeBuilder.Window("a");
        var row = TreeBuilder.Row(a);

        row.SetChildRatio(a, 0.2);

        Assert.Equal(1.0, a.SizeRatio);
    }

    [Fact]
    public void SwapChildrenExchangesPositionsAndKeepsSizesWithTheSlots()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        var row = TreeBuilder.Row(a, b);

        row.SetChildRatio(a, 0.7);
        row.SwapChildren(a, b);

        Assert.Same(b, row.Children[0]);
        Assert.Same(a, row.Children[1]);

        // The left slot is still 70% wide; the windows traded places without resizing.
        Assert.Equal(0.7, b.SizeRatio, 1e-6);
        Assert.Equal(0.3, a.SizeRatio, 1e-6);
    }

    [Fact]
    public void MoveChildSlidesWithoutResizing()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        var row = TreeBuilder.Row(a, b, c);

        row.SetChildRatio(a, 0.5);
        double ratioA = a.SizeRatio;

        row.MoveChild(a, 2);

        Assert.Same(a, row.Children[2]);
        Assert.Equal(ratioA, a.SizeRatio, 1e-9);
        AssertNormalised(row);
    }

    [Fact]
    public void ReplaceTransfersTheSlotAndItsSize()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        var row = TreeBuilder.Row(a, b);
        row.SetChildRatio(a, 0.65);

        var replacement = new ContainerNode(SplitLayout.Vertical);
        row.Replace(a, replacement);

        Assert.Same(replacement, row.Children[0]);
        Assert.Equal(0.65, replacement.SizeRatio, 1e-6);
        Assert.Null(a.Parent);
        AssertNormalised(row);
    }

    [Fact]
    public void InsertingAnAlreadyAttachedNodeThrows()
    {
        WindowNode a = TreeBuilder.Window("a");
        _ = TreeBuilder.Row(a);
        var other = new ContainerNode(SplitLayout.Horizontal);

        Assert.Throws<InvalidOperationException>(() => other.Add(a));
    }

    [Fact]
    public void InsertingAContainerIntoItsOwnDescendantThrows()
    {
        var outer = new ContainerNode(SplitLayout.Horizontal);
        var inner = new ContainerNode(SplitLayout.Vertical);
        outer.Add(inner);

        // Would create a cycle and hang every tree walk.
        Assert.Throws<InvalidOperationException>(() => inner.Add(outer));
    }

    [Fact]
    public void EqualiseChildrenGivesEveryChildTheSameShare()
    {
        var row = TreeBuilder.Row(
            TreeBuilder.Window("a"), TreeBuilder.Window("b"), TreeBuilder.Window("c"));

        row.SetChildRatio(row.Children[0], 0.8);
        row.EqualiseChildren();

        AssertNormalised(row);
        Assert.All(row.Children, c => Assert.Equal(1.0 / 3.0, c.SizeRatio, 1e-9));
    }

    [Fact]
    public void DetachAllEmptiesTheContainerAndResetsChildren()
    {
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        var row = TreeBuilder.Row(a, b);

        IReadOnlyList<Node> detached = row.DetachAll();

        Assert.Equal(2, detached.Count);
        Assert.True(row.IsEmpty);
        Assert.All(detached, n => Assert.Null(n.Parent));
        Assert.All(detached, n => Assert.Equal(1.0, n.SizeRatio));
    }
}
