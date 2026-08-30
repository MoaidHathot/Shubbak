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
        //
        // Note what this does and does not catch. WINDOWPLACEMENT is expressed in
        // workspace coordinates, so the conversion this now performs is the identity
        // on a desktop with nothing docked at the top or the left - which is most
        // build agents, and is why the missing conversion went unnoticed for as long
        // as it did. Run the same test with Taj on the top edge and the window used to
        // arrive a bar's height low. The arithmetic itself is pinned below, where it
        // does not depend on what the machine happens to have docked.
        using var window = new TestWindow();

        Maximise(window);

        var target = new Core.Geometry.Rect(160, 120, 800, 600);

        Assert.True(WindowActions.Unmaximise(window.Handle, target));

        TestWindow.PumpUntil(() => !Win32Window.IsMaximised(window.Handle));

        Assert.False(Win32Window.IsMaximised(window.Handle));
        Assert.Equal(target, Win32Window.GetBounds(window.Handle));
    }

    [Fact]
    public void AScreenRectangleIsConvertedToWorkspaceCoordinates()
    {
        // Measured, not guessed: with Taj reserving 34 pixels along the top, a window
        // asked through SetWindowPlacement to restore to y=400 arrived at y=434.
        // rcNormalPosition is not in screen coordinates, and the documented symptom of
        // pretending otherwise is a window that creeps down the screen.
        var screen = new Core.Geometry.Rect(500, 400, 600, 400);

        Assert.Equal(
            new Core.Geometry.Rect(500, 366, 600, 400),
            WindowActions.ToWorkspace(screen, (0, 34)));
    }

    [Fact]
    public void ConvertingMovesTheOriginAndNothingElse()
    {
        // Size is untouched: the offset is where workspace (0,0) sits, not a scale.
        var screen = new Core.Geometry.Rect(100, 100, 640, 480);
        Core.Geometry.Rect converted = WindowActions.ToWorkspace(screen, (12, 34));

        Assert.Equal(640, converted.Width);
        Assert.Equal(480, converted.Height);
        Assert.Equal(88, converted.X);
        Assert.Equal(66, converted.Y);
    }

    [Fact]
    public void ADesktopWithNothingDockedConvertsToItself()
    {
        // The common case, and the reason this was invisible: a taskbar along the
        // bottom leaves workspace (0,0) at screen (0,0), so the conversion is the
        // identity and a missing one costs nothing.
        var screen = new Core.Geometry.Rect(240, 180, 720, 540);

        Assert.Equal(screen, WindowActions.ToWorkspace(screen, (0, 0)));
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
