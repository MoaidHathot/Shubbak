using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// What the layout says about windows that are not on screen, and about windows
/// that overlap one another.
/// </summary>
/// <remarks>
/// Both were reported as the same symptom: windows appearing stacked on top of one
/// another until the workspace was visited or a window was touched.
/// </remarks>
public sealed class ConcealedAndStackedWindowTests
{
    private static WindowManager Create() =>
        WmFixture.Create(monitors: 1, workspaceNames: ["1", "2"]);

    [Fact]
    public void AWindowOnAnInactiveWorkspaceStillGetsARectangle()
    {
        // Already true of the engine, and pinned here as the property the platform
        // layer has to honour: it discarded these rectangles, so such a window kept
        // whatever position it had before Shubbak started.
        WindowManager wm = Create();

        wm.FocusWorkspace("2");
        WindowNode hidden = wm.Open("hidden");

        wm.FocusWorkspace("1");
        wm.Open("shown");

        Placement placement = Assert.Single(
            wm.ComputePlacements(), p => ReferenceEquals(p.Window, hidden));

        Assert.False(placement.Visible);
        Assert.False(placement.Rect.IsEmpty);
    }

    [Fact]
    public void ATiledWindowIsNeverRaised()
    {
        // Tiles do not overlap, so their stacking is the user's business. Raising them
        // would reorder windows for no reason every time the layout ran.
        WindowManager wm = Create();

        wm.Open("a");
        wm.Open("b");

        Assert.All(wm.ComputePlacements(), p => Assert.False(p.Raise));
    }

    [Fact]
    public void AFullscreenWindowIsRaisedOverItsSiblings()
    {
        // "Fills its workspace, covering siblings" was the documented behaviour and
        // was never implemented: nothing set stacking, so a fullscreen window could
        // sit behind the very window it was supposed to be covering.
        WindowManager wm = Create();

        wm.Open("behind");
        WindowNode front = wm.Open("front");

        wm.SetWindowState(front, WindowState.Fullscreen);

        Placement placement = Assert.Single(
            wm.ComputePlacements(), p => ReferenceEquals(p.Window, front));

        Assert.True(placement.Raise);
    }

    [Fact]
    public void InMonocleOnlyTheFocusedWindowIsRaised()
    {
        // Monocle gives every window the whole area, so stacking is the entire
        // difference between the window you asked for and the one you get.
        WindowManager wm = Create();

        wm.Open("a");
        WindowNode b = wm.Open("b");

        wm.SetLayout(LayoutRegistry.Resolve("monocle"));
        wm.FocusWindow(b);

        IReadOnlyList<Placement> placements = wm.ComputePlacements();

        Placement raised = Assert.Single(placements, p => p.Raise);
        Assert.Same(b, raised.Window);
    }

    [Theory]
    [InlineData("monocle", true)]
    [InlineData("splith", false)]
    [InlineData("splitv", false)]
    [InlineData("fibonacci", false)]
    [InlineData("grid", false)]
    [InlineData("master-left", false)]
    public void OnlyLayoutsWhoseWindowsOverlapSaySo(string name, bool overlaps)
    {
        // The signal the engine acts on. Every tiling layout must answer false, or
        // stacking would be churned on every pass for no reason.
        ILayout layout = LayoutRegistry.Resolve(name);

        Assert.Equal(overlaps, layout.Overlaps);
    }
}
