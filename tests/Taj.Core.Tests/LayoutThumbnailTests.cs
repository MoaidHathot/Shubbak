using Shubbak.Core.Layouts;
using Shubbak.Ui.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// A layout drawn small: the panes the thumbnail places, as the window manager
/// would place that many windows.
/// </summary>
/// <remarks>
/// The geometry is the layout engine's own, so these do not re-derive it; they pin
/// the picture each layout makes at bar size - which is what a person sees - and the
/// properties that make the picture a picture: panes stay in the box, none overlap,
/// every layout looks different from every other.
/// </remarks>
public sealed class LayoutThumbnailTests
{
    /// <summary>A pane as pixels in a 16-pixel box, for assertions that read like the screen.</summary>
    private static (int X, int Y, int W, int H) Px(UnitRect r, int size = 16) =>
        ((int)Math.Round(r.X * size), (int)Math.Round(r.Y * size), (int)Math.Round(r.Width * size), (int)Math.Round(r.Height * size));

    private static UnitRect[] Panes(string layout, int panes = 4, int size = 16, int gap = 1)
    {
        UnitRect[]? rects = LayoutThumbnail.Panes(layout, panes, size, size, gap);
        Assert.NotNull(rects);
        return rects;
    }

    [Fact]
    public void TheSpiralHasItsBigPaneOnTheLeftAndDwindlesToTheBottomRight()
    {
        (int, int, int, int)[] panes = [.. Panes("fibonacci").Select(r => Px(r))];

        Assert.Equal((0, 0, 8, 16), panes[0]);     // the left half
        Assert.Equal((9, 0, 7, 8), panes[1]);      // top right
        Assert.Equal((9, 9, 3, 7), panes[2]);      // bottom right, left half
        Assert.Equal((13, 9, 3, 7), panes[3]);     // bottom right, right half
    }

    [Fact]
    public void TheVerticalSpiralStartsWithATopAndBottom()
    {
        (int, int, int, int)[] panes = [.. Panes("fibonacci-v").Select(r => Px(r))];

        Assert.Equal((0, 0, 16, 8), panes[0]);
        Assert.Equal((0, 9, 8, 7), panes[1]);
        Assert.Equal((9, 9, 7, 3), panes[2]);
        Assert.Equal((9, 13, 7, 3), panes[3]);
    }

    [Fact]
    public void TheMirroredSpiralIsTheSpiralTurnedAHalfTurn()
    {
        // Big pane on the right, and the rest dwindling to the top left.
        (int, int, int, int)[] panes = [.. Panes("fibonacci-mirrored").Select(r => Px(r))];

        Assert.Equal((9, 0, 7, 16), panes[0]);
        Assert.Equal((0, 9, 8, 7), panes[1]);
        Assert.Equal((5, 0, 3, 8), panes[2]);
        Assert.Equal((0, 0, 4, 8), panes[3]);
    }

    [Fact]
    public void TheMasterLayoutsAreAMainPaneAndEqualStrips()
    {
        (int, int, int, int)[] left = [.. Panes("master-left").Select(r => Px(r))];
        Assert.Equal((0, 0, 8, 16), left[0]);
        Assert.Equal(3, left.Skip(1).Count(p => p.Item1 == 9 && p.Item3 == 7));

        (int, int, int, int)[] top = [.. Panes("master-top").Select(r => Px(r))];
        Assert.Equal((0, 0, 16, 8), top[0]);
        Assert.Equal(3, top.Skip(1).Count(p => p.Item2 == 9 && p.Item4 == 7));

        Assert.Equal((9, 0, 7, 16), Px(Panes("master-right")[0]));
        Assert.Equal((0, 9, 16, 7), Px(Panes("master-bottom")[0]));
    }

    [Fact]
    public void TheGridIsTwoByTwo()
    {
        (int, int, int, int)[] panes = [.. Panes("grid").Select(r => Px(r))];

        Assert.Equal((0, 0, 8, 8), panes[0]);
        Assert.Equal((9, 0, 7, 8), panes[1]);
        Assert.Equal((0, 9, 8, 7), panes[2]);
        Assert.Equal((9, 9, 7, 7), panes[3]);
    }

    [Fact]
    public void TheSplitsAreStrips()
    {
        Assert.All(Panes("splith"), r => Assert.Equal(1.0, r.Height));
        Assert.All(Panes("splitv"), r => Assert.Equal(1.0, r.Width));
        Assert.Equal(4, Panes("splith").Select(r => r.X).Distinct().Count());
    }

    [Fact]
    public void MonocleIsOnePaneHoweverManyWindows()
    {
        // Every window has the whole area; drawing four of them on top of one another
        // would compound a translucent colour and say nothing one does not.
        UnitRect[] panes = Panes("monocle", panes: 4);

        Assert.Equal([new UnitRect(0, 0, 1, 1)], panes);
    }

    [Fact]
    public void FourPanesIsTheFewestThatTellTheLayoutsApart()
    {
        // The reason for the default: a spiral of three is exactly a master layout of
        // three, so a thumbnail of three could not say which the user was in.
        Assert.Equal(Panes("fibonacci", panes: 3), Panes("master-left", panes: 3));
        Assert.NotEqual(Panes("fibonacci", panes: 4), Panes("master-left", panes: 4));
    }

    [Fact]
    public void EveryLayoutLooksDifferentFromEveryOther()
    {
        List<(string Name, UnitRect[] Panes)> all = [.. LayoutRegistry.CanonicalNames.Select(n => (n, Panes(n)))];

        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                Assert.False(
                    all[i].Panes.SequenceEqual(all[j].Panes),
                    $"{all[i].Name} and {all[j].Name} draw the same picture");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    public void PanesStayInTheBoxAndDoNotOverlap(int count)
    {
        foreach (string name in LayoutRegistry.CanonicalNames)
        {
            UnitRect[] panes = Panes(name, count, size: 32);

            foreach (UnitRect pane in panes)
            {
                Assert.InRange(pane.X, 0, 1);
                Assert.InRange(pane.Y, 0, 1);
                Assert.InRange(pane.Right, 0, 1.0001);
                Assert.InRange(pane.Bottom, 0, 1.0001);
            }

            for (int i = 0; i < panes.Length; i++)
            {
                for (int j = i + 1; j < panes.Length; j++)
                {
                    bool apart = panes[i].Right <= panes[j].X + 1e-9 || panes[j].Right <= panes[i].X + 1e-9
                        || panes[i].Bottom <= panes[j].Y + 1e-9 || panes[j].Bottom <= panes[i].Y + 1e-9;

                    Assert.True(apart, $"{name}: pane {i} overlaps pane {j} with {count} windows");
                }
            }
        }
    }

    [Fact]
    public void OneWindowFillsTheBoxInEveryLayout()
    {
        foreach (string name in LayoutRegistry.CanonicalNames)
            Assert.Equal([new UnitRect(0, 0, 1, 1)], Panes(name, panes: 1));
    }

    [Fact]
    public void TheCountIsClamped()
    {
        Assert.Single(Panes("grid", panes: 0));
        Assert.Equal(LayoutThumbnail.MaxPanes, Panes("grid", panes: 50, size: 64).Length);
    }

    [Fact]
    public void AnAliasIsTheLayoutItNames()
    {
        // The window manager's registry accepts `dwindle` for fibonacci and `master`
        // for master-left, and whatever the window manager calls it the bar draws.
        Assert.Equal(Panes("fibonacci"), Panes("dwindle"));
        Assert.Equal(Panes("master-left"), Panes("master"));
    }

    [Fact]
    public void ALayoutTheWindowManagerDoesNotHaveIsNoPicture()
    {
        Assert.Null(LayoutThumbnail.Panes("tabbed", 4, 16, 16, 1));
        Assert.Null(LayoutThumbnail.Panes("", 4, 16, 16, 1));
        Assert.Null(LayoutThumbnail.Panes(null, 4, 16, 16, 1));

        Assert.True(LayoutThumbnail.Knows("grid"));
        Assert.False(LayoutThumbnail.Knows("tabbed"));
    }

    [Fact]
    public void TheGapIsPixelsOfTheBox()
    {
        // Two columns in a 16-pixel box with a 2-pixel gap: 7 + 2 + 7.
        UnitRect[] panes = Panes("splith", panes: 2, gap: 2);

        Assert.Equal((0, 0, 7, 16), Px(panes[0]));
        Assert.Equal((9, 0, 7, 16), Px(panes[1]));

        // And none at all tiles the box exactly.
        UnitRect[] tight = Panes("splith", panes: 2, gap: 0);
        Assert.Equal(1.0, tight[0].Width + tight[1].Width, precision: 9);
    }

    [Fact]
    public void ABoxNeedNotBeSquare()
    {
        UnitRect[]? panes = LayoutThumbnail.Panes("grid", 4, 24, 12, 1);

        Assert.NotNull(panes);
        Assert.Equal(4, panes.Length);
        Assert.Equal(12.0 / 24, panes[0].Width, precision: 9);
        Assert.Equal(6.0 / 12, panes[0].Height, precision: 9);
    }
}
