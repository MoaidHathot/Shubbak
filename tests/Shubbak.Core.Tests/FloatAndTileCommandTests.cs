using Shubbak.Core.Commands;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Stating a window's state rather than toggling it.
/// </summary>
/// <remarks>
/// A rule needs to say what is true, not what to change. Written as a toggle, "this
/// application always floats" stops being true the moment anything else has already
/// floated the window - and the built-in dialog rule floats some of them before any
/// user rule runs.
/// </remarks>
public sealed class FloatAndTileCommandTests
{
    private static WindowManager Create() =>
        WmFixture.Create(monitors: 1, workspaceNames: ["1"]);

    [Fact]
    public void FloatIsIdempotent()
    {
        WindowManager wm = Create();
        WindowNode window = wm.Open("a");

        wm.SetFocusedWindowState(WindowState.Floating);
        wm.SetFocusedWindowState(WindowState.Floating);

        Assert.Equal(WindowState.Floating, window.State);
    }

    [Fact]
    public void TileIsIdempotent()
    {
        WindowManager wm = Create();
        WindowNode window = wm.Open("a");

        wm.SetFocusedWindowState(WindowState.Floating);
        wm.SetFocusedWindowState(WindowState.Tiling);
        wm.SetFocusedWindowState(WindowState.Tiling);

        Assert.Equal(WindowState.Tiling, window.State);
    }

    [Fact]
    public void ToggleStillAlternates()
    {
        WindowManager wm = Create();
        WindowNode window = wm.Open("a");

        wm.ToggleFloating();
        Assert.Equal(WindowState.Floating, window.State);

        wm.ToggleFloating();
        Assert.Equal(WindowState.Tiling, window.State);
    }

    [Fact]
    public void AFloatingWindowKeepsItsOwnRectangle()
    {
        // The property that makes an untiled window usable: the layout stops deciding
        // where it goes.
        WindowManager wm = Create();

        wm.Open("tiled");
        WindowNode floating = wm.Open("floating");

        wm.SetFocusedWindowState(WindowState.Floating);
        floating.FloatingRect = new Geometry.Rect(500, 400, 300, 200);

        Layouts.Placement placement = Assert.Single(
            wm.ComputePlacements(), p => ReferenceEquals(p.Window, floating));

        Assert.Equal(new Geometry.Rect(500, 400, 300, 200), placement.Rect);
    }

    [Fact]
    public void AFloatingWindowDoesNotTakeATileFromItsSiblings()
    {
        // The other half: the remaining tiled windows share the whole area, so an
        // untiled window leaves no gap behind it.
        WindowManager wm = Create();

        WindowNode a = wm.Open("a");
        wm.Open("b");

        Geometry.Rect twoWay = Assert.Single(
            wm.ComputePlacements(), p => ReferenceEquals(p.Window, a)).Rect;

        wm.FocusWindow(a);
        wm.SetFocusedWindowState(WindowState.Floating);

        Geometry.Rect afterFloating = Assert.Single(
            wm.ComputePlacements(), p => p.Window.Identity.Title == "b").Rect;

        Assert.True(
            afterFloating.Width > twoWay.Width,
            $"b should have taken the whole width once a floated; was {afterFloating}");
    }

    [Fact]
    public void FloatWithNothingFocusedIsRejectedRatherThanThrowing()
    {
        WindowManager wm = Create();

        WmResult result = wm.SetFocusedWindowState(WindowState.Floating);

        Assert.False(result.Succeeded);
    }
}
