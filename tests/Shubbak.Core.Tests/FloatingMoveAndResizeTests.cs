using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Moving and resizing a window that is not in the tiling flow.
/// </summary>
/// <remarks>
/// Both were refused outright: an untiled window could be moved nowhere and resized
/// not at all, so the keyboard stopped working on it entirely and the mouse was the
/// only way to change it. GlazeWM moves and resizes floating windows directly, and
/// so does this now.
/// </remarks>
public sealed class FloatingMoveAndResizeTests
{
    private static WindowManager Create()
    {
        var wm = new WindowManager();

        wm.AddMonitor(TreeBuilder.Monitor(@"\\.\DISPLAY1", x: 0, width: 1920, height: 1080));
        wm.AddWorkspace(new WorkspaceNode("1"));
        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);

        return wm;
    }

    private static WindowNode Floating(WindowManager wm, Rect at)
    {
        WindowNode window = wm.Open("floating");
        wm.SetFocusedWindowState(WindowState.Floating);
        window.FloatingRect = at;
        return window;
    }

    [Theory]
    [InlineData(Direction.Left)]
    [InlineData(Direction.Right)]
    [InlineData(Direction.Up)]
    [InlineData(Direction.Down)]
    public void MovingNudgesItInThatDirection(Direction direction)
    {
        WindowManager wm = Create();
        WindowNode window = Floating(wm, new Rect(800, 400, 400, 300));

        Assert.True(wm.MoveDirection(direction).Succeeded);

        Rect moved = window.FloatingRect!.Value;

        switch (direction)
        {
            case Direction.Left: Assert.True(moved.X < 800); break;
            case Direction.Right: Assert.True(moved.X > 800); break;
            case Direction.Up: Assert.True(moved.Y < 400); break;
            case Direction.Down: Assert.True(moved.Y > 400); break;
        }
    }

    [Fact]
    public void MovingDoesNotChangeTheSize()
    {
        WindowManager wm = Create();
        WindowNode window = Floating(wm, new Rect(800, 400, 400, 300));

        wm.MoveDirection(Direction.Right);

        Assert.Equal(400, window.FloatingRect!.Value.Width);
        Assert.Equal(300, window.FloatingRect!.Value.Height);
    }

    [Fact]
    public void ResizingWiderKeepsThePosition()
    {
        WindowManager wm = Create();
        WindowNode window = Floating(wm, new Rect(800, 400, 400, 300));

        Assert.True(wm.Resize(Axis.Horizontal, 0.1).Succeeded);

        Rect resized = window.FloatingRect!.Value;

        Assert.True(resized.Width > 400, $"expected wider than 400, got {resized.Width}");
        Assert.Equal(800, resized.X);
        Assert.Equal(300, resized.Height);
    }

    [Fact]
    public void ResizingTallerOnlyAffectsHeight()
    {
        WindowManager wm = Create();
        WindowNode window = Floating(wm, new Rect(800, 400, 400, 300));

        wm.Resize(Axis.Vertical, 0.1);

        Rect resized = window.FloatingRect!.Value;

        Assert.True(resized.Height > 300);
        Assert.Equal(400, resized.Width);
    }

    [Fact]
    public void ItCannotBeShrunkOutOfExistence()
    {
        // Repeatedly shrinking must stop at something usable rather than reaching a
        // zero-area window, which cannot be grabbed to undo the mistake.
        WindowManager wm = Create();
        WindowNode window = Floating(wm, new Rect(800, 400, 400, 300));

        for (int i = 0; i < 40; i++) wm.Resize(Axis.Horizontal, -0.1);

        Assert.True(window.FloatingRect!.Value.Width > 0);
    }

    [Fact]
    public void TheStepScalesWithTheMonitor()
    {
        // A fixed pixel step travels a very different visible distance on a laptop
        // panel and on a 4K monitor.
        WindowManager small = Create();
        WindowNode a = Floating(small, new Rect(100, 100, 200, 200));
        small.MoveDirection(Direction.Right);
        int smallStep = a.FloatingRect!.Value.X - 100;

        var large = new WindowManager();
        large.AddMonitor(TreeBuilder.Monitor(@"\\.\DISPLAY1", x: 0, width: 3840, height: 2160));
        large.AddWorkspace(new WorkspaceNode("1"));
        large.ActivateWorkspace(large.Root.Monitors[0].Workspaces[0]);

        WindowNode b = Floating(large, new Rect(100, 100, 200, 200));
        large.MoveDirection(Direction.Right);
        int largeStep = b.FloatingRect!.Value.X - 100;

        Assert.True(largeStep > smallStep, $"{largeStep} should exceed {smallStep}");
    }

    [Fact]
    public void ATiledWindowStillResizesItsContainer()
    {
        // The tiling behaviour must be untouched: this is a new branch, not a
        // replacement.
        WindowManager wm = Create();

        WindowNode a = wm.Open("a");
        wm.Open("b");
        wm.FocusWindow(a);

        Assert.True(wm.Resize(Axis.Horizontal, 0.1).Succeeded);
        Assert.Null(a.FloatingRect);
    }
}
