using Shubbak.Core.Geometry;
using Shubbak.Ui.Layout;

namespace Shubbak.Ui.Tests;

/// <summary>
/// A child taller than its row: where the overflow goes.
/// </summary>
/// <remarks>
/// <para>
/// The bar's icon is a picture with four pixels of padding on every side - the pill
/// it gains on hover is a square around it - so <c>icon size=20</c> is a node of 28.
/// In a bar of <c>height 23</c> that is five pixels too tall, and the alignment used
/// to clamp the centring offset at zero, which put the whole overflow at the bottom:
/// the picture sat six pixels from the top and a pixel over the bottom edge, so it
/// read as both off-centre and cropped - a Windows Terminal icon with its "PRE" badge
/// cut off, on a 4K display at 150 percent, where the bar is 35 pixels and the node
/// 42.
/// </para>
/// <para>
/// Centring means centring: an overflowing child is placed so the overflow is split,
/// and the row's edges clip it. For the icon the clipped part is its own transparent
/// padding, and the picture is whole and in the middle.
/// </para>
/// </remarks>
public sealed class CrossAxisOverflowTests
{
    private static readonly FlexLayout Layout = new(new FixedTextMeasurer());

    private static VisualNode Row(AlignItems align, params VisualNode[] children)
    {
        var node = new VisualNode { Id = "row", Direction = FlexDirection.Row, Align = align };
        foreach (VisualNode child in children) node.Add(child);
        return node;
    }

    /// <summary>The bar's icon node as the bar builds it, scaled to 150 percent: a 30px picture in 6px of padding.</summary>
    private static VisualNode Icon() => new()
    {
        Id = "icon",
        Kind = VisualKind.Image,
        Image = new ImageBitmap(30, 30, new uint[30 * 30]),
        Box = new BoxStyle(Width: 42, Height: 42, Padding: Edges.All(6)),
    };

    private static VisualNode Find(VisualNode root, string id) =>
        root.SelfAndDescendants().First(n => n.Id == id);

    [Fact]
    public void AnIconTallerThanTheBarIsCentredAndItsPictureStaysInside()
    {
        // The user's bar: height 23 at 150 percent is 35 pixels; the icon node is 42.
        VisualNode root = Row(AlignItems.Stretch, Icon());

        Layout.Arrange(root, new Rect(0, 0, 400, 35));

        VisualNode icon = Find(root, "icon");

        // Seven pixels of overflow, split: three above, four below.
        Assert.Equal(-3, icon.Rect.Top);
        Assert.Equal(42, icon.Rect.Height);

        // The picture is the node less its padding, and it is whole: 3..33 of 0..35.
        int pictureTop = icon.Rect.Top + icon.Box.Padding.Top;
        int pictureBottom = icon.Rect.Bottom - icon.Box.Padding.Bottom;

        Assert.Equal(3, pictureTop);
        Assert.Equal(33, pictureBottom);
    }

    [Fact]
    public void AnIconThatFitsIsCentredAsBefore()
    {
        // The case that always worked, kept working: 42 in 50 leaves four above.
        VisualNode root = Row(AlignItems.Stretch, Icon());

        Layout.Arrange(root, new Rect(0, 0, 400, 50));

        Assert.Equal(4, Find(root, "icon").Rect.Top);
        Assert.Equal(42, Find(root, "icon").Rect.Height);
    }

    [Fact]
    public void CentredTextTallerThanTheRowOverflowsBothEdges()
    {
        // Not only pictures: a line of text centred in a row shorter than the line
        // used to keep its top and lose its descenders. It loses a little of each
        // now, which is what centred means.
        var root = Row(AlignItems.Center, new VisualNode { Id = "a", Kind = VisualKind.Text, Text = "AB" });

        Layout.Arrange(root, new Rect(0, 0, 200, 10));

        // A 16px line in 10px: six over, three above and three below.
        Assert.Equal(-3, Find(root, "a").Rect.Top);
        Assert.Equal(16, Find(root, "a").Rect.Height);
    }

    [Fact]
    public void EndAlignedTextTallerThanTheRowKeepsItsBottomOnTheRowsBottom()
    {
        // End means the far edge, whatever the size: the overflow is at the top.
        var root = Row(AlignItems.End, new VisualNode { Id = "a", Kind = VisualKind.Text, Text = "AB" });

        Layout.Arrange(root, new Rect(0, 0, 200, 10));

        Assert.Equal(-6, Find(root, "a").Rect.Top);
        Assert.Equal(10, Find(root, "a").Rect.Bottom);
    }

    [Fact]
    public void HitTestingStillFindsAnOverflowingChildWhereItIsDrawn()
    {
        // The pill a clickable icon gains on hover follows the node, so a click on the
        // visible part of an overflowing icon must land on it.
        VisualNode root = Row(AlignItems.Stretch, Icon());

        Layout.Arrange(root, new Rect(0, 0, 400, 35));

        Assert.Equal("icon", root.HitTest(20, 17)?.Id);
        Assert.Equal("icon", root.HitTest(20, 1)?.Id);
    }
}
