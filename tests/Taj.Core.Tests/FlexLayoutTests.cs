using Shubbak.Core.Geometry;
using Taj.Core.Layout;

namespace Taj.Core.Tests;

/// <summary>
/// A predictable text measurer.
/// </summary>
/// <remarks>
/// Every character is a fixed width and every line a fixed height, so layout tests
/// assert exact pixel values without depending on installed fonts. Real measurement
/// is DirectWrite's job; the interface exists precisely so it can be substituted
/// here.
/// </remarks>
internal sealed class FixedTextMeasurer : ITextMeasurer
{
    public const int CharacterWidth = 10;
    public const int LineHeight = 16;

    public Size Measure(string text, FontStyle font) =>
        new((text ?? string.Empty).Length * CharacterWidth, LineHeight);
}

/// <summary>Tests for the flex layout engine.</summary>
public sealed class FlexLayoutTests
{
    private static readonly FlexLayout Layout = new(new FixedTextMeasurer());

    private static VisualNode Text(string id, string text, BoxStyle box = default) => new()
    {
        Id = id,
        Kind = VisualKind.Text,
        Text = text,
        Box = box,
    };

    private static VisualNode Row(params VisualNode[] children)
    {
        var node = new VisualNode { Id = "row", Direction = FlexDirection.Row };
        foreach (VisualNode child in children) node.Add(child);
        return node;
    }

    private static VisualNode Find(VisualNode root, string id) =>
        root.SelfAndDescendants().First(n => n.Id == id);

    [Fact]
    public void ChildrenAreLaidOutLeftToRight()
    {
        VisualNode root = Row(Text("a", "AB"), Text("b", "CDE"));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(0, Find(root, "a").Rect.Left);
        Assert.Equal(20, Find(root, "a").Rect.Width);
        Assert.Equal(20, Find(root, "b").Rect.Left);
        Assert.Equal(30, Find(root, "b").Rect.Width);
    }

    [Fact]
    public void GapSeparatesChildren()
    {
        var root = new VisualNode { Id = "row", Direction = FlexDirection.Row, Gap = 8 };
        root.Add(Text("a", "AB"));
        root.Add(Text("b", "CD"));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(28, Find(root, "b").Rect.Left);
    }

    [Fact]
    public void GrowDistributesSpareSpace()
    {
        VisualNode root = Row(
            Text("fixed", "AB"),
            Text("flexible", "", new BoxStyle(Grow: 1)));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(20, Find(root, "fixed").Rect.Width);
        Assert.Equal(180, Find(root, "flexible").Rect.Width);
    }

    [Fact]
    public void GrowIsSharedInProportion()
    {
        VisualNode root = Row(
            Text("one", "", new BoxStyle(Grow: 1)),
            Text("three", "", new BoxStyle(Grow: 3)));

        Layout.Arrange(root, new Rect(0, 0, 400, 30));

        Assert.Equal(100, Find(root, "one").Rect.Width);
        Assert.Equal(300, Find(root, "three").Rect.Width);
    }

    [Fact]
    public void GrowingChildrenFillTheParentExactly()
    {
        // Rounding each child independently would leave the last one a pixel short
        // of the right edge, which is visible against the bar background.
        VisualNode root = Row(
            Text("a", "", new BoxStyle(Grow: 1)),
            Text("b", "", new BoxStyle(Grow: 1)),
            Text("c", "", new BoxStyle(Grow: 1)));

        Layout.Arrange(root, new Rect(0, 0, 1000, 30));

        Assert.Equal(1000, Find(root, "c").Rect.Right);
        Assert.Equal(
            1000,
            Find(root, "a").Rect.Width + Find(root, "b").Rect.Width + Find(root, "c").Rect.Width);
    }

    [Fact]
    public void ShrinkTakesMoreFromLargerChildrenAndFitsExactly()
    {
        // Flexbox shrinks proportionally to size, so a long window title gives up far
        // more than the clock beside it - but the clock does shrink too. Protecting
        // it entirely is what MinWidth and NoShrink are for, covered below.
        VisualNode root = Row(
            Text("long", new string('x', 30)),   // 300px of content
            Text("short", "12:00"));             // 50px

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        int longWidth = Find(root, "long").Rect.Width;
        int shortWidth = Find(root, "short").Rect.Width;

        Assert.True(longWidth < 300, $"the long node should have shrunk, got {longWidth}");
        Assert.True(shortWidth < 50, $"the short node should have shrunk too, got {shortWidth}");

        // Proportional: the long node gave up roughly six times as much.
        Assert.True(300 - longWidth > (50 - shortWidth) * 4);

        // And the result fits exactly, rather than spilling past the bar's edge.
        Assert.Equal(200, longWidth + shortWidth);
    }

    [Fact]
    public void ShrinkRedistributesAroundAChildThatHitsItsMinimum()
    {
        // Without a second pass, a node pinned at its minimum would silently
        // reintroduce the overflow and the content would spill.
        VisualNode root = Row(
            Text("flexible", new string('x', 30)),
            Text("pinned", "12:00", new BoxStyle(MinWidth: 50)));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(50, Find(root, "pinned").Rect.Width);
        Assert.Equal(150, Find(root, "flexible").Rect.Width);
    }

    [Fact]
    public void MinimumWidthIsRespectedWhenShrinking()
    {
        VisualNode root = Row(
            Text("a", new string('x', 40), new BoxStyle(MinWidth: 60)),
            Text("b", new string('y', 40), new BoxStyle(MinWidth: 60)));

        Layout.Arrange(root, new Rect(0, 0, 150, 30));

        Assert.True(Find(root, "a").Rect.Width >= 60);
        Assert.True(Find(root, "b").Rect.Width >= 60);
    }

    [Fact]
    public void NonShrinkableChildrenKeepTheirSize()
    {
        VisualNode root = Row(
            Text("fixed", "12:00", new BoxStyle(NoShrink: true)),
            Text("flexible", new string('x', 40)));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(50, Find(root, "fixed").Rect.Width);
    }

    [Theory]
    [InlineData(JustifyContent.Start, 0)]
    [InlineData(JustifyContent.Center, 75)]
    [InlineData(JustifyContent.End, 150)]
    public void JustifyPositionsContentWithinTheParent(JustifyContent justify, int expectedLeft)
    {
        var root = new VisualNode { Id = "row", Direction = FlexDirection.Row, Justify = justify };
        root.Add(Text("a", "ABCDE"));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(expectedLeft, Find(root, "a").Rect.Left);
    }

    [Fact]
    public void SpaceBetweenPushesChildrenToTheEnds()
    {
        var root = new VisualNode
        {
            Id = "row",
            Direction = FlexDirection.Row,
            Justify = JustifyContent.SpaceBetween,
        };

        root.Add(Text("a", "AB"));
        root.Add(Text("b", "CD"));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(0, Find(root, "a").Rect.Left);
        Assert.Equal(200, Find(root, "b").Rect.Right);
    }

    [Fact]
    public void AlignCenterCentresOnTheCrossAxis()
    {
        var root = new VisualNode
        {
            Id = "row",
            Direction = FlexDirection.Row,
            Align = AlignItems.Center,
        };

        root.Add(Text("a", "AB"));

        Layout.Arrange(root, new Rect(0, 0, 200, 40));

        // A 16px line centred in 40px leaves 12px above.
        Assert.Equal(12, Find(root, "a").Rect.Top);
        Assert.Equal(16, Find(root, "a").Rect.Height);
    }

    [Fact]
    public void AlignStretchFillsTheCrossAxis()
    {
        var root = new VisualNode
        {
            Id = "row",
            Direction = FlexDirection.Row,
            Align = AlignItems.Stretch,
        };

        root.Add(Text("a", "AB"));

        Layout.Arrange(root, new Rect(0, 0, 200, 40));

        Assert.Equal(0, Find(root, "a").Rect.Top);
        Assert.Equal(40, Find(root, "a").Rect.Height);
    }

    [Fact]
    public void PaddingInsetsChildren()
    {
        var root = new VisualNode
        {
            Id = "row",
            Direction = FlexDirection.Row,
            Box = new BoxStyle(Padding: new Edges(10, 5, 10, 5)),
        };

        root.Add(Text("a", "AB", new BoxStyle(Grow: 1)));

        Layout.Arrange(root, new Rect(0, 0, 200, 40));

        Rect child = Find(root, "a").Rect;

        Assert.Equal(10, child.Left);
        Assert.Equal(190, child.Right);
    }

    [Fact]
    public void MarginSeparatesAChildFromItsNeighbours()
    {
        VisualNode root = Row(
            Text("a", "AB", new BoxStyle(Margin: new Edges(0, 0, 12, 0))),
            Text("b", "CD"));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(32, Find(root, "b").Rect.Left);
    }

    [Fact]
    public void ColumnDirectionStacksVertically()
    {
        var root = new VisualNode { Id = "column", Direction = FlexDirection.Column };
        root.Add(Text("a", "AB"));
        root.Add(Text("b", "CD"));

        Layout.Arrange(root, new Rect(0, 0, 200, 100));

        Assert.Equal(0, Find(root, "a").Rect.Top);
        Assert.Equal(16, Find(root, "b").Rect.Top);
    }

    [Fact]
    public void NestedContainersCompose()
    {
        // Three zones, the middle one flexible: the canonical bar arrangement.
        var left = new VisualNode { Id = "left", Direction = FlexDirection.Row };
        left.Add(Text("ws", "12345"));

        var middle = new VisualNode
        {
            Id = "middle",
            Direction = FlexDirection.Row,
            Justify = JustifyContent.Center,
            Box = new BoxStyle(Grow: 1),
        };
        middle.Add(Text("title", "hello"));

        var right = new VisualNode
        {
            Id = "right",
            Direction = FlexDirection.Row,
            Justify = JustifyContent.End,
        };
        right.Add(Text("clock", "12:00"));

        var root = new VisualNode { Id = "bar", Direction = FlexDirection.Row };
        root.Add(left);
        root.Add(middle);
        root.Add(right);

        Layout.Arrange(root, new Rect(0, 0, 1000, 30));

        Assert.Equal(0, Find(root, "ws").Rect.Left);
        Assert.Equal(1000, Find(root, "clock").Rect.Right);

        // The middle zone absorbed the slack, and its content is centred within it.
        Rect title = Find(root, "title").Rect;
        Assert.True(title.Left > 100 && title.Right < 900, $"title not centred: {title}");
    }

    [Fact]
    public void HiddenNodesTakeNoSpace()
    {
        VisualNode hidden = Text("hidden", "XXXXX");
        hidden.Visible = false;

        VisualNode root = Row(Text("a", "AB"), hidden, Text("b", "CD"));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(20, Find(root, "b").Rect.Left);
    }

    [Fact]
    public void FixedWidthOverridesContentSize()
    {
        VisualNode root = Row(Text("a", "ABCDEFGH", new BoxStyle(Width: 40)));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(40, Find(root, "a").Rect.Width);
    }

    [Fact]
    public void MaxWidthClampsContent()
    {
        VisualNode root = Row(Text("a", new string('x', 50), new BoxStyle(MaxWidth: 120)));

        Layout.Arrange(root, new Rect(0, 0, 1000, 30));

        Assert.True(Find(root, "a").Rect.Width <= 120);
    }

    [Fact]
    public void EmptyContainersDoNotThrow()
    {
        var root = new VisualNode { Id = "empty", Direction = FlexDirection.Row };

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Equal(new Rect(0, 0, 200, 30), root.Rect);
    }

    [Fact]
    public void LayoutIsDeterministic()
    {
        VisualNode Build() => Row(
            Text("a", "AB", new BoxStyle(Grow: 1)),
            Text("b", "CDE"),
            Text("c", "F", new BoxStyle(Grow: 2)));

        VisualNode first = Build();
        VisualNode second = Build();

        Layout.Arrange(first, new Rect(0, 0, 777, 31));
        Layout.Arrange(second, new Rect(0, 0, 777, 31));

        Assert.Equal(
            first.SelfAndDescendants().Select(n => n.Rect),
            second.SelfAndDescendants().Select(n => n.Rect));
    }

    // ---- hit testing -------------------------------------------------------

    [Fact]
    public void HitTestFindsTheDeepestNode()
    {
        VisualNode root = Row(Text("a", "AB"), Text("b", "CD"));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        // Vertically centred by default, so probe the middle rather than the top.
        Assert.Equal("a", root.HitTest(5, 15)?.Id);
        Assert.Equal("b", root.HitTest(25, 15)?.Id);
    }

    [Fact]
    public void HitTestMissesOutsideTheBar()
    {
        VisualNode root = Row(Text("a", "AB"));

        Layout.Arrange(root, new Rect(0, 0, 200, 30));

        Assert.Null(root.HitTest(-1, 15));
        Assert.Null(root.HitTest(5, 100));
    }

    [Fact]
    public void HitTestSkipsHiddenNodes()
    {
        VisualNode hidden = Text("hidden", "AB");
        VisualNode root = Row(hidden);

        Layout.Arrange(root, new Rect(0, 0, 200, 30));
        hidden.Visible = false;

        Assert.NotEqual("hidden", root.HitTest(5, 15)?.Id);
    }
}
