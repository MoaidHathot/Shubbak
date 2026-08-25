using Dalil.Core;
using Shubbak.Core.Geometry;

namespace Dalil.Core.Tests;

/// <summary>
/// Where the palette puts things, and where the mouse finds them.
/// </summary>
/// <remarks>
/// The row rectangles used to be worked out inside the drawing loop. A hit test that
/// recomputed them independently would agree today and disagree the first time either
/// side changed a margin - and the symptom, a click selecting the row above the one
/// under the pointer, is obvious to a user and invisible in review. One calculation
/// now serves both, and these tests are what hold it to being an inverse of itself.
/// </remarks>
public sealed class PaletteLayoutTests
{
    private static PaletteLayout Layout(double scale = 1.0, int rows = 12, int rowHeight = 38) =>
        new(new DalilConfig { VisibleRows = rows, RowHeight = rowHeight, Width = 720 },
            scale,
            new Rect(0, 0, 720, 900));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void EveryRowHitTestsBackToItself(double scale)
    {
        PaletteLayout layout = Layout(scale);

        for (int slot = 0; slot < layout.VisibleRows; slot++)
        {
            Rect bounds = layout.RowBounds(slot);

            // The centre, and both edges inside the row. If the two calculations ever
            // drift apart, the edges go first.
            Assert.Equal(slot, layout.SlotAt(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2)));
            Assert.Equal(slot, layout.SlotAt(bounds.X + 1, bounds.Y));
            Assert.Equal(slot, layout.SlotAt(bounds.Right - 1, bounds.Bottom - 1));
        }
    }

    [Fact]
    public void RowsDoNotOverlapOrLeaveGaps()
    {
        PaletteLayout layout = Layout();

        for (int slot = 1; slot < layout.VisibleRows; slot++)
        {
            Assert.Equal(layout.RowBounds(slot - 1).Bottom, layout.RowBounds(slot).Y);
        }
    }

    [Fact]
    public void TheSearchBoxIsNotARow()
    {
        PaletteLayout layout = Layout();
        Rect box = layout.SearchBox;

        // Clicking the search field must not act on whatever result is nearest, which
        // is what clamping instead of refusing would do.
        Assert.Equal(-1, layout.SlotAt(box.X + 10, box.Y + (box.Height / 2)));
    }

    [Fact]
    public void TheHintBarIsNotARow()
    {
        PaletteLayout layout = Layout();

        Assert.Equal(-1, layout.SlotAt(layout.Canvas.X + 20, layout.HintBarTop + 2));
        Assert.Equal(-1, layout.SlotAt(layout.Canvas.X + 20, layout.Canvas.Bottom - 1));
    }

    [Fact]
    public void TheMarginsAreNotRows()
    {
        PaletteLayout layout = Layout();
        Rect first = layout.RowBounds(0);

        Assert.Equal(-1, layout.SlotAt(layout.Canvas.X, first.Y + 4));
        Assert.Equal(-1, layout.SlotAt(layout.Canvas.Right - 1, first.Y + 4));
    }

    [Fact]
    public void NothingAboveTheListIsARow()
    {
        PaletteLayout layout = Layout();

        Assert.Equal(-1, layout.SlotAt(100, layout.ListTop - 1));
        Assert.Equal(-1, layout.SlotAt(100, 0));
    }

    [Fact]
    public void TheListStartsBelowTheSearchBox()
    {
        PaletteLayout layout = Layout();

        Assert.True(layout.ListTop > layout.SearchBox.Bottom);
    }

    [Fact]
    public void EverythingScalesTogether()
    {
        PaletteLayout at96 = Layout(1.0);
        PaletteLayout at150 = Layout(1.5);

        // Not a coincidence worth asserting exactly - rounding makes that brittle -
        // but the palette laid out at raw pixels on a scaled display was a real bug,
        // and "the chrome grew" is the property that would have caught it.
        Assert.True(at150.Padding > at96.Padding);
        Assert.True(at150.TextInset > at96.TextInset);
        Assert.True(at150.HintBar > at96.HintBar);
        Assert.True(at150.ListTop > at96.ListTop);
    }
}
