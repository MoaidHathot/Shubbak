using Shubbak.Ui.Layout;

namespace Shubbak.Ui.Tests;

/// <summary>
/// Scaling a tree from device-independent pixels to a display's own.
/// </summary>
/// <remarks>
/// The numbers in a config are written once and meant to look the same everywhere.
/// These pin the arithmetic that promise rests on: the factor a DPI gives, how a
/// length rounds, which lengths may never vanish, and that every part of every node
/// is reached - a padding left unscaled looks like a font that grew but a pill that
/// did not, and is the kind of thing only a test notices before a user does.
/// </remarks>
public sealed class VisualScalingTests
{
    [Theory]
    [InlineData(96u, 1.0)]
    [InlineData(120u, 1.25)]
    [InlineData(144u, 1.5)]
    [InlineData(192u, 2.0)]
    public void TheFactorIsTheRatioToNinetySix(uint dpi, double expected) =>
        Assert.Equal(expected, VisualScaling.FactorFor(dpi), precision: 6);

    [Fact]
    public void AnUnknownDpiIsTreatedAsTheBaseline()
    {
        // GetDpiForWindow answers zero for a window that has gone; the bar must not
        // scale itself to nothing on the way out.
        Assert.Equal(1.0, VisualScaling.FactorFor(0));
    }

    [Theory]
    [InlineData(6, 1.5, 9)]
    [InlineData(6, 1.25, 8)]   // 7.5 rounds away from zero, not to even
    [InlineData(34, 1.5, 51)]
    [InlineData(1, 1.25, 1)]
    [InlineData(0, 2.0, 0)]
    public void ALengthRoundsToTheNearestPixel(int length, double factor, int expected) =>
        Assert.Equal(expected, VisualScaling.Scale(length, factor));

    [Fact]
    public void ABorderThatExistsStaysAtLeastAPixel()
    {
        // At 75 percent a one-pixel border rounds to one; a scale small enough to
        // round it to zero would have made the border vanish where the config asked
        // for one. Zero stays zero: no border is no border at any scale.
        Assert.Equal(1, VisualScaling.ScaleAtLeastOne(1, 0.4));
        Assert.Equal(0, VisualScaling.ScaleAtLeastOne(0, 2.0));
    }

    [Fact]
    public void AFactorOfOneLeavesTheTreeAlone()
    {
        VisualNode root = Tree();
        BoxStyle before = root.Children[0].Box;

        VisualScaling.Scale(root, 1.0);

        Assert.Equal(before, root.Children[0].Box);
    }

    [Fact]
    public void EveryPartOfEveryNodeIsScaled()
    {
        VisualNode root = Tree();

        VisualScaling.Scale(root, 1.5);

        VisualNode pill = root.Children[0];

        Assert.Equal(15, root.Gap);
        Assert.Equal(new Edges(6, 3, 6, 3), pill.Box.Padding);
        Assert.Equal(new Edges(3, 0, 3, 0), pill.Box.Margin);
        Assert.Equal(120, pill.Box.Width);
        Assert.Equal(30, pill.Box.MinWidth);
        Assert.Equal(300, pill.Box.MaxWidth);
        Assert.Equal(18.0, pill.Style.Font.Size);
        Assert.Equal(6, pill.Style.CornerRadius);
        Assert.Equal(2, pill.Style.BorderWidth);

        // The hover style is a second style on the same node and scales with it, or
        // the widget would jump in size the moment the pointer arrived.
        Assert.Equal(18.0, pill.HoverStyle!.Value.Font.Size);

        // A grandchild, so the walk is the whole tree and not the first level.
        Assert.Equal(new Edges(3, 3, 3, 3), pill.Children[0].Box.Padding);
    }

    [Fact]
    public void WidthsThatAreNotSetStayUnset()
    {
        // A null width means "as wide as the content"; scaling it to a number would
        // pin a widget that was meant to flex.
        var node = new VisualNode { Id = "n", Kind = VisualKind.Text, Text = "x" };

        VisualScaling.Scale(node, 2.0);

        Assert.Null(node.Box.Width);
        Assert.Null(node.Box.Height);
        Assert.Null(node.Box.MaxWidth);
    }

    [Fact]
    public void ANonPositiveFactorIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VisualScaling.Scale(Tree(), 0));

    private static VisualNode Tree()
    {
        var style = VisualStyle.Default with
        {
            Font = new FontStyle("Segoe UI", 12),
            CornerRadius = 4,
            BorderWidth = 1,
        };

        var pill = new VisualNode
        {
            Id = "pill",
            Kind = VisualKind.Container,
            Box = new BoxStyle(
                Width: 80,
                MinWidth: 20,
                MaxWidth: 200,
                Padding: new Edges(4, 2, 4, 2),
                Margin: new Edges(2, 0, 2, 0)),
            Style = style,
            HoverStyle = style,
        };

        pill.Children.Add(new VisualNode
        {
            Id = "inner",
            Kind = VisualKind.Text,
            Text = "x",
            Box = new BoxStyle(Padding: Edges.All(2)),
            Style = style,
        });

        var root = new VisualNode { Id = "root", Kind = VisualKind.Container, Gap = 10, Style = style };
        root.Children.Add(pill);

        return root;
    }
}
