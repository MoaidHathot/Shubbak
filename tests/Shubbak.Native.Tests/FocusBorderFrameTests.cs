namespace Shubbak.Native.Tests;

/// <summary>
/// Whether the focus border moves a window's frame.
/// </summary>
/// <remarks>
/// It matters because shadow compensation is cached: the committer measures a window's
/// invisible margin once and thereafter compares what Windows reports against what it
/// applied, to decide whether the window already sits where it was put. If setting a
/// border changed the frame, that comparison would fail on every focus change, the
/// window would be repositioned, and the user would see a jump every time focus moved.
/// </remarks>
public sealed class FocusBorderFrameTests
{
    [Fact]
    public void SettingABorderDoesNotMoveTheFrame()
    {
        using var window = new TestWindow();

        Win32Window.ShadowMargins before = Win32Window.GetShadowMargins(window.Handle);
        Core.Geometry.Rect boundsBefore = Win32Window.GetBounds(window.Handle);

        WindowActions.SetBorderColour(window.Handle, 0x8D, 0xBC, 0xFF);
        TestWindow.PumpUntil(() => false, 150);

        Assert.Equal(before, Win32Window.GetShadowMargins(window.Handle));
        Assert.Equal(boundsBefore, Win32Window.GetBounds(window.Handle));
    }

    [Fact]
    public void ClearingABorderDoesNotMoveTheFrame()
    {
        using var window = new TestWindow();

        WindowActions.SetBorderColour(window.Handle, 0x8D, 0xBC, 0xFF);
        TestWindow.PumpUntil(() => false, 150);

        Win32Window.ShadowMargins bordered = Win32Window.GetShadowMargins(window.Handle);
        Core.Geometry.Rect boundsBordered = Win32Window.GetBounds(window.Handle);

        WindowActions.ClearBorderColour(window.Handle);
        TestWindow.PumpUntil(() => false, 150);

        Assert.Equal(bordered, Win32Window.GetShadowMargins(window.Handle));
        Assert.Equal(boundsBordered, Win32Window.GetBounds(window.Handle));
    }

    [Fact]
    public void RepeatedBorderChangesLeaveTheFrameWhereItWas()
    {
        // The pattern a user produces by moving focus back and forth. If any of these
        // shifted the frame, the layout would chase it.
        using var window = new TestWindow();

        Core.Geometry.Rect original = Win32Window.GetBounds(window.Handle);

        for (int i = 0; i < 5; i++)
        {
            WindowActions.SetBorderColour(window.Handle, 0x8D, 0xBC, 0xFF);
            WindowActions.ClearBorderColour(window.Handle);
        }

        TestWindow.PumpUntil(() => false, 200);

        Assert.Equal(original, Win32Window.GetBounds(window.Handle));
    }
}
