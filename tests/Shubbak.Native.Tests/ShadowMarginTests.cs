namespace Shubbak.Native.Tests;

/// <summary>
/// Tests for the invisible margin around a window.
/// </summary>
/// <remarks>
/// <para>
/// Since Vista a window's rectangle includes its drop shadow, which is transparent -
/// about seven pixels either side and below on Windows 10 and 11. A tiling window
/// manager that ignores it positions the rectangles perfectly and leaves a visible gap
/// of twice the shadow between every pair of windows.
/// </para>
/// <para>
/// The symptom is misleading: reducing the configured gap barely changes what the user
/// sees, because most of it was never the gap. It reads as the setting being ignored,
/// which is exactly how it was reported.
/// </para>
/// </remarks>
public sealed class ShadowMarginTests
{
    [Fact]
    public void NonsenseMeasurementsAreDiscarded()
    {
        // GetWindowRect and the compositor do not always answer in the same
        // coordinate space: the first is virtualised for a process that is not DPI
        // aware while the second reports physical pixels. On a 150% display that
        // produced insets in the hundreds, and negative ones - which would have
        // expanded every window over its neighbour instead of under its own shadow.
        //
        // This test host is not DPI aware, so it exercises exactly that path.
        using var window = new TestWindow();

        Win32Window.ShadowMargins margins = Win32Window.GetShadowMargins(window.Handle);

        Assert.True(
            margins.Left >= 0 && margins.Top >= 0 && margins.Right >= 0 && margins.Bottom >= 0,
            $"a negative inset would expand the window the wrong way, got {margins}");

        Assert.True(
            margins.Left <= 32 && margins.Top <= 32 && margins.Right <= 32 && margins.Bottom <= 32,
            $"an inset this large is a measurement error, not a shadow: {margins}");
    }



    [Theory]
    // The margins Windows 10 and 11 actually produce, plus the asymmetric and
    // degenerate cases, because a sign error shows up in some of these and not others.
    [InlineData(7, 0, 7, 7)]
    [InlineData(8, 1, 8, 8)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(11, 3, 5, 7)]
    public void PlacingThenMeasuringGivesBackTheSameRectangle(
        int left, int top, int right, int bottom)
    {
        // The invariant the whole scheme rests on. Placement grows a rectangle by the
        // shadow; measurement must shrink it by exactly the same amount.
        //
        // It did not. Placement compensated and measurement did not, so every shadowed
        // window read back fourteen pixels wider than it had been asked to be. The
        // layout took that for "the window has moved" and animated it - and since a
        // focus change re-runs the layout, focusing anything made it swell by the width
        // of its own shadow and settle back.
        //
        // Written against margins directly: a plain test window has no shadow, so
        // placing one and measuring it would agree with a broken implementation too.
        var margins = new Win32Window.ShadowMargins(left, top, right, bottom);
        var wanted = new Core.Geometry.Rect(300, 200, 640, 480);

        Core.Geometry.Rect placed = WindowCommitter.Expand(wanted, margins);
        Core.Geometry.Rect measured = WindowCommitter.Shrink(placed, margins);

        Assert.Equal(wanted, measured);
    }

    [Fact]
    public void MeasuringUndoesPlacingForEveryRectangle()
    {
        // Stated over a spread of rectangles rather than one, so an error that only
        // appears at particular sizes or offsets cannot hide.
        var margins = new Win32Window.ShadowMargins(7, 0, 7, 7);

        Core.Geometry.Rect[] rectangles =
        [
            new(0, 0, 100, 100),
            new(-1920, 0, 1920, 1080),
            new(1920, -300, 2560, 1440),
            new(37, 91, 613, 409),
        ];

        foreach (Core.Geometry.Rect rect in rectangles)
        {
            Assert.Equal(
                rect,
                WindowCommitter.Shrink(WindowCommitter.Expand(rect, margins), margins));
        }
    }

    [Fact]
    public void PlacingGrowsAndMeasuringShrinks()
    {
        // Pins the direction. Swapping the two would still round-trip, and would still
        // pass every test above, while placing every window under its own shadow.
        var margins = new Win32Window.ShadowMargins(7, 0, 7, 7);
        var rect = new Core.Geometry.Rect(300, 200, 640, 480);

        Core.Geometry.Rect grown = WindowCommitter.Expand(rect, margins);
        Core.Geometry.Rect shrunk = WindowCommitter.Shrink(rect, margins);

        Assert.Equal(new Core.Geometry.Rect(293, 200, 654, 487), grown);
        Assert.Equal(new Core.Geometry.Rect(307, 200, 626, 473), shrunk);
    }

    [Fact]
    public void AWindowWithNoShadowIsLeftAlone()
    {
        // The common case on a remote session or a plain tool window. Compensation
        // must be exactly a no-op, not an approximate one.
        var margins = new Win32Window.ShadowMargins(0, 0, 0, 0);
        var rect = new Core.Geometry.Rect(10, 20, 30, 40);

        Assert.Equal(rect, WindowCommitter.Expand(rect, margins));
        Assert.Equal(rect, WindowCommitter.Shrink(rect, margins));
    }

    [Fact]
    public void AnInvalidWindowReportsNothing()
    {
        // Compensation must degrade to doing nothing rather than throwing: it runs on
        // the layout path for every window.
        Assert.True(Win32Window.GetShadowMargins(0).IsEmpty);
        Assert.True(Win32Window.GetShadowMargins(0x1).IsEmpty);
    }

    [Fact]
    public void MeasuringIsStable()
    {
        // Measured once and cached by the committer, so it has to give the same
        // answer twice - a window whose margins changed between layouts would drift.
        using var window = new TestWindow();

        Assert.Equal(
            Win32Window.GetShadowMargins(window.Handle),
            Win32Window.GetShadowMargins(window.Handle));
    }

    [Fact]
    public void TheVisibleFrameIsInsideTheWindowRectangle()
    {
        // The property the whole compensation rests on. Stated directly so that if
        // Windows ever reverses it, this fails rather than the layout quietly going
        // wrong by fourteen pixels.
        using var window = new TestWindow();

        Core.Geometry.Rect outer = Win32Window.GetBounds(window.Handle);
        Win32Window.ShadowMargins margins = Win32Window.GetShadowMargins(window.Handle);

        Assert.True(margins.Left + margins.Right < outer.Width);
        Assert.True(margins.Top + margins.Bottom < outer.Height);
    }
}
