using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for a window the application has taken full-screen by itself: the
/// geometric test that recognises one, and what the layout engine then does with it.
/// </summary>
/// <remarks>
/// The reported case, from which all of this follows: a video played full-screen in
/// Firefox, then another window clicked. The click changes focus, focus re-runs the
/// layout, and the layout put the browser back in its tile while the browser still
/// believed it was full-screen - leaving a window that was neither.
/// </remarks>
public sealed class NativeFullscreenTests
{
    private static readonly Rect Monitor = new(0, 0, 1920, 1080);

    // ---- the geometric test ------------------------------------------------

    [Fact]
    public void AWindowExactlyTheSizeOfTheMonitorIsFullScreen()
    {
        Assert.True(NativeFullscreen.CoversMonitor(Monitor, Monitor));
    }

    [Fact]
    public void AWindowLargerThanTheMonitorIsFullScreen()
    {
        // Some applications overshoot deliberately, to be sure nothing shows around
        // the edge. Covering is the question, not matching.
        Assert.True(NativeFullscreen.CoversMonitor(new Rect(-8, -8, 1936, 1096), Monitor));
    }

    [Fact]
    public void APixelOfRoundingIsStillFullScreen()
    {
        // A DPI conversion that lands a pixel short is not a window declining to be
        // full-screen.
        Assert.True(NativeFullscreen.CoversMonitor(new Rect(1, 1, 1918, 1078), Monitor));
    }

    [Fact]
    public void AWindowShortOfTheMonitorIsNot()
    {
        Assert.False(NativeFullscreen.CoversMonitor(new Rect(0, 0, 1920, 1000), Monitor));
    }

    [Fact]
    public void AMaximisedWindowIsNotMistakenForFullScreen()
    {
        // The case the tolerance has to clear, and the reason it is two pixels rather
        // than something generous. A maximised window stops at the work area, which
        // the bar has already taken a strip out of - so it is short of the monitor by
        // the height of the bar, and must stay a maximised window.
        var maximised = new Rect(0, 34, 1920, 1046);

        Assert.False(NativeFullscreen.CoversMonitor(maximised, Monitor));
    }

    [Fact]
    public void AMaximisedWindowOnABareDesktopDefeatsTheGeometricTest()
    {
        // Measured, and the reason the daemon asks a second question. The compositor
        // draws a maximised window oversized: its frame is deliberately put off the
        // edge of the screen, eleven pixels a side on the display this was measured
        // on. So it overhangs the work area - and on a desktop with an auto-hiding
        // taskbar and nothing docked at the top, the work area is the whole panel and
        // the overhang covers the monitor exactly.
        //
        // Geometry cannot tell these apart, and this test says so rather than
        // pretending otherwise. What separates them is IsZoomed, which the watch asks
        // of any window that gets this far.
        var maximisedOnABareDesktop = new Rect(-11, -11, 1942, 1102);

        Assert.True(NativeFullscreen.CoversMonitor(maximisedOnABareDesktop, Monitor));
    }

    [Fact]
    public void AWindowOnAnotherMonitorIsNotFullScreenOnThisOne()
    {
        var second = new Rect(1920, 0, 1920, 1080);

        Assert.True(NativeFullscreen.CoversMonitor(second, second));
        Assert.False(NativeFullscreen.CoversMonitor(second, Monitor));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 0, 1920, 0)]
    [InlineData(0, 0, 0, 1080)]
    public void AWindowWithNoExtentIsNotFullScreen(int x, int y, int width, int height)
    {
        // Minimised, being created, or gone. Guessing here would mean handing the
        // display to a window that is disappearing.
        Assert.False(NativeFullscreen.CoversMonitor(new Rect(x, y, width, height), Monitor));
    }

    [Fact]
    public void AMonitorThatHasNotBeenReadYetAnswersNo()
    {
        Assert.False(NativeFullscreen.CoversMonitor(Monitor, Rect.Empty));
    }

    // ---- what the layout engine does with it -------------------------------

    private static (RootNode Root, MonitorNode Monitor) Setup(params Node[] children)
    {
        WorkspaceNode workspace = TreeBuilder.Workspace(children: children);
        MonitorNode monitor = TreeBuilder.Monitor(width: 1920, height: 1080);

        // A bar along the top, as Taj reserves through the appbar API.
        monitor.WorkArea = new Rect(0, 34, 1920, 1046);
        monitor.AddWorkspace(workspace);

        return (TreeBuilder.Root(monitor), monitor);
    }

    [Fact]
    public void AFullScreenWindowIsGivenTheWholeMonitorAndRaised()
    {
        WindowNode video = TreeBuilder.Window("video");
        WindowNode other = TreeBuilder.Window("other");

        (RootNode root, _) = Setup(video, other);
        video.IsNativeFullscreen = true;

        var options = new ArrangeOptions(OuterGap: Gaps.All(5), InnerGap: 10);
        Placement placement = new LayoutEngine()
            .Arrange(root, options)
            .Single(p => p.Window == video);

        // The whole panel, bar strip included, and no gaps - a full-screen window that
        // honoured them would not be full-screen.
        Assert.Equal(new Rect(0, 0, 1920, 1080), placement.Rect);

        // Raised, or it would be the right size and still underneath the bar.
        Assert.True(placement.Raise);
    }

    [Fact]
    public void ItsSiblingsDoNotMove()
    {
        // The whole reason this is a flag rather than a WindowState. Watching a video
        // must not rearrange the rest of the workspace, and stopping watching it must
        // not rearrange the workspace back.
        WindowNode video = TreeBuilder.Window("video");
        WindowNode other = TreeBuilder.Window("other");

        (RootNode root, _) = Setup(video, other);

        var options = new ArrangeOptions(OuterGap: Gaps.All(5), InnerGap: 10);
        var engine = new LayoutEngine();

        Rect before = engine.Arrange(root, options).Single(p => p.Window == other).Rect;

        video.IsNativeFullscreen = true;
        Rect during = engine.Arrange(root, options).Single(p => p.Window == other).Rect;

        video.IsNativeFullscreen = false;
        Rect after = engine.Arrange(root, options).Single(p => p.Window == other).Rect;

        Assert.Equal(before, during);
        Assert.Equal(before, after);
    }

    [Fact]
    public void ItKeepsItsTileAndReturnsToIt()
    {
        WindowNode video = TreeBuilder.Window("video");
        WindowNode other = TreeBuilder.Window("other");

        (RootNode root, _) = Setup(video, other);

        var options = new ArrangeOptions(OuterGap: Gaps.All(5), InnerGap: 10);
        var engine = new LayoutEngine();

        Rect tile = engine.Arrange(root, options).Single(p => p.Window == video).Rect;

        video.IsNativeFullscreen = true;
        _ = engine.Arrange(root, options);

        video.IsNativeFullscreen = false;
        Placement back = engine.Arrange(root, options).Single(p => p.Window == video);

        // Exactly the tile it left, because the tile was never given away.
        Assert.Equal(tile, back.Rect);
        Assert.False(back.Raise);
    }

    [Fact]
    public void AFloatingWindowMayAlsoGoFullScreenAndKeepsItsRememberedPosition()
    {
        WindowNode video = TreeBuilder.Window("video");

        (RootNode root, _) = Setup(video);
        video.State = WindowState.Floating;
        video.FloatingRect = new Rect(300, 200, 800, 600);

        var engine = new LayoutEngine();

        video.IsNativeFullscreen = true;
        Placement full = engine.Arrange(root, ArrangeOptions.Default).Single();

        Assert.Equal(new Rect(0, 0, 1920, 1080), full.Rect);
        Assert.True(full.Raise);

        video.IsNativeFullscreen = false;
        Placement back = engine.Arrange(root, ArrangeOptions.Default).Single();

        // The position the user chose, untouched throughout.
        Assert.Equal(new Rect(300, 200, 800, 600), back.Rect);
        Assert.Equal(new Rect(300, 200, 800, 600), video.FloatingRect);
    }

    [Fact]
    public void ItSurvivesBeingNestedInsideAContainer()
    {
        // The substitution happens inside the tiling recursion, so a window several
        // splits deep has to reach it too.
        WindowNode video = TreeBuilder.Window("video");
        WindowNode sibling = TreeBuilder.Window("sibling");
        WindowNode outer = TreeBuilder.Window("outer");

        ContainerNode inner = TreeBuilder.Column(video, sibling);

        (RootNode root, _) = Setup(outer, inner);
        video.IsNativeFullscreen = true;

        Placement placement = new LayoutEngine()
            .Arrange(root, ArrangeOptions.Default)
            .Single(p => p.Window == video);

        Assert.Equal(new Rect(0, 0, 1920, 1080), placement.Rect);
        Assert.True(placement.Raise);
    }

    [Fact]
    public void AWindowOnAnInactiveWorkspaceIsStillMarkedInvisible()
    {
        // Full-screen decides the rectangle, never whether the window is on screen.
        // A workspace nobody is looking at stays that way.
        WindowNode video = TreeBuilder.Window("video");
        WindowNode elsewhere = TreeBuilder.Window("elsewhere");

        WorkspaceNode active = TreeBuilder.Workspace("1", children: elsewhere);
        WorkspaceNode hidden = TreeBuilder.Workspace("2", children: video);

        MonitorNode monitor = TreeBuilder.Monitor(width: 1920, height: 1080);
        monitor.AddWorkspace(active);
        monitor.AddWorkspace(hidden);

        video.IsNativeFullscreen = true;

        Placement placement = new LayoutEngine()
            .Arrange(TreeBuilder.Root(monitor), ArrangeOptions.Default)
            .Single(p => p.Window == video);

        Assert.False(placement.Visible);
        Assert.Equal(new Rect(0, 0, 1920, 1080), placement.Rect);
    }

    // ---- the interaction with a deliberate state change --------------------

    [Fact]
    public void AskingForAStateEndsTheObservation()
    {
        // Otherwise pressing the fullscreen binding on a window that had taken itself
        // full-screen would leave the flag set behind the new state, and toggling back
        // to tiling would hand it the monitor again for no reason anyone asked for.
        WindowManager wm = WmFixture.Create();
        WindowNode video = wm.Open("video");

        video.IsNativeFullscreen = true;

        wm.SetWindowState(video, WindowState.Floating);

        Assert.False(video.IsNativeFullscreen);
    }
}
