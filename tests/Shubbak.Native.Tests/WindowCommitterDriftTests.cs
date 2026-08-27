namespace Shubbak.Native.Tests;

/// <summary>
/// Whether a window that has moved itself is put back.
/// </summary>
/// <remarks>
/// <para>
/// Applications reposition their own windows. Firefox restores the geometry it
/// remembered from last time a moment after its window appears - after the window
/// manager has placed it - and Windows announces that only through
/// <c>EVENT_OBJECT_LOCATIONCHANGE</c>, which Shubbak does not subscribe to and will
/// not, for the reasons written up in <c>WinEventSource</c>.
/// </para>
/// <para>
/// The committer skips a window whose target has not changed since it was last placed,
/// which is what keeps a relayout to one <c>SetWindowPos</c> instead of dozens. That
/// skip used to be judged on the target alone, so a window that had wandered was
/// skipped on every subsequent pass for ever - the target had not changed, so as far
/// as the committer was concerned there was nothing to do. The window stayed on the
/// wrong monitor until the user pressed <c>wm-redraw</c>, which cures it only because
/// it forgets every cached rectangle.
/// </para>
/// <para>
/// Both halves are held here: a window that has not moved must still be skipped, or
/// the twitch that the exact comparison caused comes back.
/// </para>
/// </remarks>
public sealed class WindowCommitterDriftTests
{
    private static Core.Tree.WindowIdentity Identity => new()
    {
        ProcessName = "test",
        ClassName = "ShubbakNativeTestWindow",
        Title = "Shubbak test window",
    };

    private static Core.Layouts.Placement Place(Core.Tree.WindowNode node, Core.Geometry.Rect rect) =>
        new(node, rect, Visible: true);

    /// <summary>
    /// The skip has to keep working, or every layout moves every window.
    /// </summary>
    /// <remarks>
    /// This is the regression the drift check could reintroduce. A focus change runs a
    /// layout, so a committer that re-moves everything on every pass is a visible jump
    /// each time focus moves.
    /// </remarks>
    [Fact]
    public void AWindowThatHasNotMovedIsStillSkipped()
    {
        using var window = new TestWindow();
        var committer = new WindowCommitter();
        var node = new Core.Tree.WindowNode(window.Handle, Identity);

        var rect = new Core.Geometry.Rect(240, 180, 720, 540);

        Assert.Equal(1, committer.Commit([Place(node, rect)], static p => (nint)p.Window.Handle));

        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) == rect);

        // Same target, window untouched: nothing to do.
        Assert.Equal(0, committer.Commit([Place(node, rect)], static p => (nint)p.Window.Handle));
        Assert.Equal(0, committer.Commit([Place(node, rect)], static p => (nint)p.Window.Handle));
    }

    /// <summary>
    /// A window that has adjusted its own size a little is not chased.
    /// </summary>
    /// <remarks>
    /// A terminal snapping to whole character cells never lands exactly where it was
    /// put. Treating that as displacement is what made the previous attempt at this
    /// unusable.
    /// </remarks>
    [Fact]
    public void ASmallSelfAdjustmentIsNotChased()
    {
        using var window = new TestWindow();
        var committer = new WindowCommitter();
        var node = new Core.Tree.WindowNode(window.Handle, Identity);

        var rect = new Core.Geometry.Rect(240, 180, 720, 540);

        committer.Commit([Place(node, rect)], static p => (nint)p.Window.Handle);
        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) == rect);

        // The window nudges itself, the way an application rounding to its own grid
        // does. Well inside the tolerance.
        window.MoveTo(rect.X + 9, rect.Y + 6);
        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle).X != rect.X);

        Assert.Equal(0, committer.Commit([Place(node, rect)], static p => (nint)p.Window.Handle));
    }

    /// <summary>
    /// The reported bug: a window that relocated itself is put back.
    /// </summary>
    [Fact]
    public void AWindowThatMovedItselfFarAwayIsPutBack()
    {
        using var window = new TestWindow();
        var committer = new WindowCommitter();
        var node = new Core.Tree.WindowNode(window.Handle, Identity);

        var rect = new Core.Geometry.Rect(240, 180, 720, 540);

        committer.Commit([Place(node, rect)], static p => (nint)p.Window.Handle);
        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) == rect);

        // The window takes itself somewhere else entirely, as an application restoring
        // its remembered position does.
        window.MoveTo(rect.X + 900, rect.Y + 700);
        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle).X != rect.X);

        // Same target as before, so the old skip would have discarded this.
        Assert.Equal(1, committer.Commit([Place(node, rect)], static p => (nint)p.Window.Handle));

        TestWindow.PumpUntil(() => WindowCommitter.VisibleBounds(window.Handle) == rect);
        Assert.Equal(rect, WindowCommitter.VisibleBounds(window.Handle));
    }
}
