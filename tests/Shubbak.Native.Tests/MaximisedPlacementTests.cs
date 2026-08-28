namespace Shubbak.Native.Tests;

/// <summary>
/// Whether a maximised window is unmaximised before it is tiled.
/// </summary>
/// <remarks>
/// <para>
/// The compositor draws a maximised window on the assumption that it fills the
/// monitor: the shadow is suppressed and part of the frame is deliberately placed off
/// the top of the screen. Move one to half the screen without clearing
/// <c>WS_MAXIMIZE</c> and that frame is no longer off the screen - it is a black strip
/// along the top - and the focus border is drawn around a shape that is not the
/// window.
/// </para>
/// <para>
/// This was reported against Windows Calculator and Settings, which reopen maximised.
/// They were adopted as tiling, handed a tile rectangle, and left flagged: Shubbak
/// reported <c>state: tiling</c> for a window whose style still had bit 24 set. A
/// window that had never been maximised, in the same layout, looked correct.
/// </para>
/// <para>
/// Held at the committer rather than at adoption on purpose. Adoption is one of the
/// ways in; Win+Up, a double-clicked title bar and an application maximising itself
/// are others, and by then the drift watch has expired and
/// <c>EVENT_OBJECT_LOCATIONCHANGE</c> is deliberately not subscribed.
/// </para>
/// </remarks>
public sealed class MaximisedPlacementTests
{
    private static Core.Tree.WindowIdentity Identity => new()
    {
        ProcessName = "test",
        ClassName = "ShubbakNativeTestWindow",
        Title = "Shubbak test window",
    };

    private static Core.Layouts.Placement Place(Core.Tree.WindowNode node, Core.Geometry.Rect rect) =>
        new(node, rect, Visible: true);

    private static void Maximise(TestWindow window)
    {
        WindowActions.Maximise(window.Handle);
        TestWindow.PumpUntil(() => Win32Window.IsMaximised(window.Handle));

        Assert.True(
            Win32Window.IsMaximised(window.Handle),
            "the test window would not maximise, so the rest of this proves nothing");
    }

    [Fact]
    public void PlacingAMaximisedWindowClearsTheFlag()
    {
        using var window = new TestWindow();
        var committer = new WindowCommitter();
        var node = new Core.Tree.WindowNode(window.Handle, Identity);

        Maximise(window);

        committer.Commit(
            [Place(node, new Core.Geometry.Rect(240, 180, 720, 540))],
            static p => (nint)p.Window.Handle);

        TestWindow.PumpUntil(() => !Win32Window.IsMaximised(window.Handle));

        Assert.False(Win32Window.IsMaximised(window.Handle));
    }

    [Fact]
    public void AMaximisedWindowStillLandsWhereItWasSent()
    {
        // Clearing the flag is half the job. SW_RESTORE on its own would put the window
        // back at whatever it occupied before it was maximised, which is why this goes
        // through SetWindowPlacement carrying the destination with it.
        using var window = new TestWindow();
        var committer = new WindowCommitter();
        var node = new Core.Tree.WindowNode(window.Handle, Identity);

        Maximise(window);

        var rect = new Core.Geometry.Rect(240, 180, 720, 540);

        committer.Commit([Place(node, rect)], static p => (nint)p.Window.Handle);

        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) == rect);

        Assert.Equal(rect, WindowCommitter.VisibleBounds(window.Handle));
        Assert.False(Win32Window.IsMaximised(window.Handle));
    }

    [Fact]
    public void AnOrdinaryWindowIsPlacedExactlyAsBefore()
    {
        // The guard must cost nothing behavioural for the overwhelmingly common case.
        // A stray restore on every placement would be its own bug.
        using var window = new TestWindow();
        var committer = new WindowCommitter();
        var node = new Core.Tree.WindowNode(window.Handle, Identity);

        var rect = new Core.Geometry.Rect(300, 200, 640, 480);

        Assert.Equal(1, committer.Commit([Place(node, rect)], static p => (nint)p.Window.Handle));

        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) == rect);

        Assert.Equal(rect, WindowCommitter.VisibleBounds(window.Handle));
        Assert.False(Win32Window.IsMaximised(window.Handle));
    }

    [Fact]
    public void UnmaximisingPutsTheWindowWhereItIsTold()
    {
        // The mechanism on its own, away from the committer, because the committer
        // expands the rectangle by the shadow margins before handing it over and that
        // arithmetic would otherwise be the only thing under test here.
        using var window = new TestWindow();

        Maximise(window);

        var target = new Core.Geometry.Rect(160, 120, 800, 600);

        Assert.True(WindowActions.Unmaximise(window.Handle, target));

        TestWindow.PumpUntil(() => !Win32Window.IsMaximised(window.Handle));

        Assert.False(Win32Window.IsMaximised(window.Handle));
        Assert.Equal(target, Win32Window.GetBounds(window.Handle));
    }

    [Fact]
    public void UnmaximisingAWindowThatIsNotMaximisedLeavesItWhereItIs()
    {
        // The committer only calls this when IsMaximised says so, but the two are
        // separate calls and a window can be restored in between.
        using var window = new TestWindow();

        var target = new Core.Geometry.Rect(200, 150, 700, 500);

        Assert.True(WindowActions.Unmaximise(window.Handle, target));

        TestWindow.PumpUntil(() => Win32Window.GetBounds(window.Handle) == target);

        Assert.Equal(target, Win32Window.GetBounds(window.Handle));
        Assert.False(Win32Window.IsMaximised(window.Handle));
    }

    [Fact]
    public void UnmaximisingADeadWindowIsRefusedRatherThanThrowing()
    {
        // It runs inside the commit loop, and a window closing underneath a layout pass
        // is ordinary rather than exceptional.
        Assert.False(WindowActions.Unmaximise(0, new Core.Geometry.Rect(0, 0, 100, 100)));
    }
}
