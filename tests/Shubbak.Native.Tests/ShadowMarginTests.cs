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
