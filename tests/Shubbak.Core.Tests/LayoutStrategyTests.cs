using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for the P2 layout strategies.
/// </summary>
/// <remarks>
/// Two properties are asserted for every layout, because breaking either produces
/// visible artefacts: children must stay inside their parent's rectangle, and they
/// must not overlap. Beyond that each layout has its own defining behaviour.
/// </remarks>
public sealed class LayoutStrategyTests
{
    private static Dictionary<string, Rect> Arrange(ILayout layout, int windows, int gap = 0)
    {
        var workspace = new WorkspaceNode("1", layout);
        for (int i = 0; i < windows; i++) workspace.Add(TreeBuilder.Window($"w{i}"));

        return TreeBuilder.ArrangeToMap(
            workspace, new ArrangeOptions(InnerGap: gap), width: 1000, height: 800);
    }

    private static void AssertWithinBounds(IEnumerable<Rect> rects, Rect bounds)
    {
        foreach (Rect rect in rects)
        {
            Assert.True(
                rect.Left >= bounds.Left && rect.Top >= bounds.Top &&
                rect.Right <= bounds.Right && rect.Bottom <= bounds.Bottom,
                $"{rect} escapes {bounds}");
        }
    }

    private static void AssertNoOverlap(List<Rect> rects)
    {
        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                Assert.False(
                    rects[i].IntersectsWith(rects[j]),
                    $"{rects[i]} overlaps {rects[j]}");
            }
        }
    }

    public static TheoryData<string> AllLayouts
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in LayoutRegistry.CanonicalNames) data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void EveryLayoutKeepsChildrenInsideTheParent(string layoutName)
    {
        ILayout layout = LayoutRegistry.Resolve(layoutName);

        foreach (int count in (int[])[1, 2, 3, 5, 8])
        {
            Dictionary<string, Rect> map = Arrange(layout, count);
            AssertWithinBounds(map.Values, new Rect(0, 0, 1000, 800));
        }
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void EveryLayoutProducesNonEmptyTiles(string layoutName)
    {
        // A zero-sized tile is unclickable and looks like the window has vanished.
        ILayout layout = LayoutRegistry.Resolve(layoutName);
        Dictionary<string, Rect> map = Arrange(layout, 5);

        Assert.All(map.Values, rect => Assert.False(rect.IsEmpty, $"{rect} is empty"));
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void EveryLayoutIsDeterministic(string layoutName)
    {
        ILayout layout = LayoutRegistry.Resolve(layoutName);

        Dictionary<string, Rect> first = Arrange(layout, 6);
        Dictionary<string, Rect> second = Arrange(layout, 6);

        Assert.Equal(first, second);
    }

    // ---- fibonacci ---------------------------------------------------------

    [Fact]
    public void FibonacciHalvesTheRemainingSpaceEachTime()
    {
        // Four windows on a 1000x800 area:
        //   w0 takes the left half        (0,0 500x800)
        //   w1 takes the top of the rest  (500,0 500x400)
        //   w2 takes the left of the rest (500,400 250x400)
        //   w3 inherits the remainder     (750,400 250x400)
        Dictionary<string, Rect> map = Arrange(FibonacciLayout.Horizontal, 4);

        Assert.Equal(new Rect(0, 0, 500, 800), map["w0"]);
        Assert.Equal(new Rect(500, 0, 500, 400), map["w1"]);
        Assert.Equal(new Rect(500, 400, 250, 400), map["w2"]);
        Assert.Equal(new Rect(750, 400, 250, 400), map["w3"]);
    }

    [Fact]
    public void FibonacciTilesWithoutGapsOrOverlaps()
    {
        Dictionary<string, Rect> map = Arrange(FibonacciLayout.Horizontal, 6);

        List<Rect> rects = [.. map.Values];
        AssertNoOverlap(rects);

        // The spiral must account for every pixel of the area.
        Assert.Equal(1000L * 800L, rects.Sum(r => r.Area));
    }

    [Fact]
    public void MirroredFibonacciSpiralsTheOtherWay()
    {
        Dictionary<string, Rect> normal = Arrange(FibonacciLayout.Horizontal, 3);
        Dictionary<string, Rect> mirrored = Arrange(FibonacciLayout.Mirrored, 3);

        // The first window takes the left half normally and the right half mirrored.
        Assert.Equal(0, normal["w0"].Left);
        Assert.Equal(1000, mirrored["w0"].Right);
    }

    [Fact]
    public void FibonacciInsertsAtTheEndSoItSubdividesTheSmallestRegion()
    {
        // Inserting anywhere else would displace an existing tile rather than
        // continuing the spiral.
        var workspace = new WorkspaceNode("1", FibonacciLayout.Horizontal);
        WindowNode a = TreeBuilder.Window("a");
        workspace.Add(a);
        workspace.Add(TreeBuilder.Window("b"));

        Assert.Equal(2, FibonacciLayout.Horizontal.ResolveInsertIndex(workspace, a));
    }

    // ---- master-stack ------------------------------------------------------

    [Fact]
    public void MasterStackGivesTheFirstWindowTheMasterArea()
    {
        Dictionary<string, Rect> map = Arrange(MasterStackLayout.Left, 3);

        Assert.Equal(new Rect(0, 0, 500, 800), map["w0"]);

        // The stack divides the right half evenly.
        Assert.Equal(new Rect(500, 0, 500, 400), map["w1"]);
        Assert.Equal(new Rect(500, 400, 500, 400), map["w2"]);
    }

    [Fact]
    public void MasterStackResizeGrowsTheMasterArea()
    {
        var workspace = new WorkspaceNode("1", MasterStackLayout.Left);
        WindowNode master = TreeBuilder.Window("master");
        workspace.Add(master);
        workspace.Add(TreeBuilder.Window("s1"));
        workspace.Add(TreeBuilder.Window("s2"));

        int before = TreeBuilder.ArrangeToMap(workspace, width: 1000, height: 800)["master"].Width;

        // Dragging the divider adjusts the first child's ratio, exactly as it does
        // in a split layout - no layout-specific resize code needed.
        workspace.SetChildRatio(master, 0.7);

        Dictionary<string, Rect> map = TreeBuilder.ArrangeToMap(workspace, width: 1000, height: 800);

        Assert.Equal(500, before);
        Assert.True(map["master"].Width > before,
            $"master should have grown from {before}, got {map["master"].Width}");

        // The stack keeps the remainder exactly.
        Assert.Equal(1000, map["master"].Width + map["s1"].Width);
    }

    [Fact]
    public void MasterStackOnTheRightPutsTheStackFirst()
    {
        Dictionary<string, Rect> map = Arrange(MasterStackLayout.Right, 3);

        Assert.Equal(1000, map["w0"].Right);
        Assert.Equal(0, map["w1"].Left);
    }

    [Fact]
    public void MasterStackInsertsAtTheHeadOfTheStack()
    {
        // dwm's behaviour: the newest window is the most likely to be promoted
        // next, so it sits adjacent to master.
        var workspace = new WorkspaceNode("1", MasterStackLayout.Left);
        workspace.Add(TreeBuilder.Window("a"));
        workspace.Add(TreeBuilder.Window("b"));

        Assert.Equal(1, MasterStackLayout.Left.ResolveInsertIndex(workspace, null));
    }

    [Fact]
    public void MasterStackWithASingleWindowUsesTheWholeArea()
    {
        Dictionary<string, Rect> map = Arrange(MasterStackLayout.Left, 1);
        Assert.Equal(new Rect(0, 0, 1000, 800), map["w0"]);
    }

    // ---- grid --------------------------------------------------------------

    [Fact]
    public void GridArrangesFourWindowsAsTwoByTwo()
    {
        Dictionary<string, Rect> map = Arrange(GridLayout.Instance, 4);

        Assert.Equal(new Rect(0, 0, 500, 400), map["w0"]);
        Assert.Equal(new Rect(500, 0, 500, 400), map["w1"]);
        Assert.Equal(new Rect(0, 400, 500, 400), map["w2"]);
        Assert.Equal(new Rect(500, 400, 500, 400), map["w3"]);
    }

    [Fact]
    public void GridStretchesAShortFinalRowAcrossTheWidth()
    {
        // Three windows: two on top, one below. Leaving the last row half-width
        // would look like a bug rather than a layout.
        Dictionary<string, Rect> map = Arrange(GridLayout.Instance, 3);

        Assert.Equal(1000, map["w2"].Width);
        Assert.Equal(0, map["w2"].Left);
    }

    [Fact]
    public void GridTilesExactly()
    {
        Dictionary<string, Rect> map = Arrange(GridLayout.Instance, 9);

        List<Rect> rects = [.. map.Values];
        AssertNoOverlap(rects);
        Assert.Equal(1000L * 800L, rects.Sum(r => r.Area));
    }

    // ---- monocle -----------------------------------------------------------

    [Fact]
    public void MonocleGivesEveryWindowTheWholeArea()
    {
        // Deliberately not implemented by hiding the others: they stay laid out, so
        // leaving monocle is instantaneous.
        Dictionary<string, Rect> map = Arrange(MonocleLayout.Instance, 4);

        Assert.All(map.Values, rect => Assert.Equal(new Rect(0, 0, 1000, 800), rect));
    }

    [Fact]
    public void MonocleNavigationCyclesThroughTheStack()
    {
        // Every window shares one rectangle, so "right" cannot mean anything
        // spatial; mapping it to "next" keeps the usual keys working.
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        var workspace = new WorkspaceNode("1", MonocleLayout.Instance);
        workspace.Add(a);
        workspace.Add(b);

        Assert.Same(b, MonocleLayout.Instance.Navigate(workspace, a, Direction.Right));
        Assert.Same(a, MonocleLayout.Instance.Navigate(workspace, b, Direction.Left));
    }

    // ---- gaps --------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void GapsNeverPushChildrenOutsideTheParent(string layoutName)
    {
        ILayout layout = LayoutRegistry.Resolve(layoutName);
        Dictionary<string, Rect> map = Arrange(layout, 5, gap: 12);

        AssertWithinBounds(map.Values, new Rect(0, 0, 1000, 800));

        // Monocle is the one layout where overlap is the entire point: every window
        // gets the whole area and z-order decides what is seen.
        if (layout is not MonocleLayout) AssertNoOverlap([.. map.Values]);
    }

    // ---- navigation --------------------------------------------------------

    [Fact]
    public void GeometricNavigationFindsTheNeighbourInAFibonacciSpiral()
    {
        // The spiral has no list order matching screen order, so navigation must be
        // resolved from the rectangles themselves.
        var workspace = new WorkspaceNode("1", FibonacciLayout.Horizontal);
        WindowNode a = TreeBuilder.Window("a");
        WindowNode b = TreeBuilder.Window("b");
        WindowNode c = TreeBuilder.Window("c");
        workspace.Add(a);
        workspace.Add(b);
        workspace.Add(c);

        MonitorNode monitor = TreeBuilder.Monitor(width: 1000, height: 800);
        monitor.AddWorkspace(workspace);
        _ = TreeBuilder.Root(monitor);
        new LayoutEngine().Arrange(workspace, ArrangeOptions.Default);

        // a is the left half; b is the top-right; c is the bottom-right.
        Assert.Same(a, FocusNavigator.Navigate(b, Direction.Left));
        Assert.Same(c, FocusNavigator.Navigate(b, Direction.Down));
        Assert.Same(b, FocusNavigator.Navigate(c, Direction.Up));
    }

    // ---- registry ----------------------------------------------------------

    [Fact]
    public void LayoutCycleVisitsEveryEntryAndReturns()
    {
        ILayout start = LayoutRegistry.Resolve("splith");
        ILayout current = start;

        HashSet<string> visited = [];

        for (int i = 0; i < 16; i++)
        {
            current = LayoutRegistry.Next(current);
            visited.Add(current.Name);
            if (ReferenceEquals(current, start)) break;
        }

        Assert.Same(start, current);
        Assert.True(visited.Count >= 5, $"cycle only visited {visited.Count} layouts");
    }

    [Fact]
    public void CycleIsReversible()
    {
        ILayout start = LayoutRegistry.Resolve("splith");
        Assert.Same(start, LayoutRegistry.Previous(LayoutRegistry.Next(start)));
    }

    [Theory]
    [InlineData("dwindle", "fibonacci")]
    [InlineData("spiral", "fibonacci")]
    [InlineData("master", "master-left")]
    [InlineData("column", "splitv")]
    [InlineData("FIBONACCI", "fibonacci")]
    public void AliasesResolveToTheirCanonicalLayout(string alias, string canonical)
    {
        Assert.Equal(canonical, LayoutRegistry.Resolve(alias).Name);
    }
}
