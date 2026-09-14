using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Dropping a dragged window into a workspace whose layout decides the geometry.
/// </summary>
/// <remarks>
/// <para>
/// Manual split is the one layout in which the tree <i>is</i> the layout, so a drop
/// across a split's axis means "nest here" and is honoured by wrapping the target in a
/// new split - which <see cref="DragTests"/> covers. Every other layout arranges its
/// children itself from their order: the spiral, the grid and master-stack each place
/// a child by its index and nothing else. A drop into one of those can only choose the
/// order, and the layout does the rest.
/// </para>
/// <para>
/// It did not. A drop beside a window in a fibonacci workspace wrapped the target in a
/// manual split whose axis came from whichever edge the cursor was nearest, so the
/// workspace grew a hand-made split inside an automatic layout - and when the
/// workspace had held one window, flattening then replaced the workspace's own layout
/// with that split. Dragging a second window onto a <c>fibonacci-v</c> monitor turned
/// it into <c>splitv</c> or <c>splith</c> depending on where the mouse happened to be,
/// and the layout the user had chosen for that monitor was gone.
/// </para>
/// </remarks>
public sealed class DragIntoAutomaticLayoutTests
{
    private static WindowManager TwoMonitors(ILayout secondLayout)
    {
        var wm = new WindowManager();
        wm.AddMonitor(TreeBuilder.Monitor("\\\\.\\DISPLAY1", x: 0, width: 1000, height: 800));
        wm.AddMonitor(TreeBuilder.Monitor("\\\\.\\DISPLAY2", x: 1000, width: 1000, height: 800));

        wm.AddWorkspace(new WorkspaceNode("1"), wm.Root.Monitors[0]);
        wm.AddWorkspace(new WorkspaceNode("2", secondLayout), wm.Root.Monitors[1]);

        return wm;
    }

    private static WorkspaceNode Second(WindowManager wm) => wm.Root.Monitors[1].Workspaces[0];

    [Theory]
    [InlineData(1500, 40)]    // top edge of the tile filling the second monitor
    [InlineData(1500, 760)]   // bottom edge
    [InlineData(1020, 400)]   // left edge
    [InlineData(1980, 400)]   // right edge
    public void DroppingOntoAnotherMonitorKeepsThatMonitorsLayout(int x, int y)
    {
        // The report: a monitor set to fibonacci-v, a window dragged onto it, and the
        // result was a manual split - stacked or side by side according to where the
        // mouse was released, never the spiral that was asked for.
        WindowManager wm = TwoMonitors(FibonacciLayout.Vertical);

        wm.ActivateWorkspace(Second(wm));
        WindowNode resident = wm.Open("resident");

        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);
        WindowNode dragged = wm.Open("dragged");
        wm.ComputePlacements();

        WmResult result = wm.DropWindow(dragged, x, y);

        Assert.True(result.Succeeded, result.RejectionReason);

        WorkspaceNode destination = Second(wm);
        Assert.Same(destination, dragged.Workspace);

        // The layout the user chose for that monitor is still the layout, and the two
        // windows are its children - not a split it never asked for.
        Assert.Same(FibonacciLayout.Vertical, destination.Layout);
        Assert.Equal(2, destination.Count);
        Assert.All(destination.Children, child => Assert.IsType<WindowNode>(child));

        // And they are arranged as fibonacci-v arranges two windows: the first divide
        // is top/bottom, whichever edge the cursor was nearest.
        wm.ComputePlacements();
        Assert.Equal(1000, dragged.Rect.Width);
        Assert.Equal(1000, resident.Rect.Width);
        Assert.True(
            dragged.Rect.Bottom <= resident.Rect.Top || resident.Rect.Bottom <= dragged.Rect.Top,
            $"the two should be stacked: dragged={dragged.Rect} resident={resident.Rect}");
    }

    [Fact]
    public void TheSideOfTheDropStillDecidesTheOrder()
    {
        // The mouse cannot pick the geometry in an automatic layout, but it can still
        // pick the slot: dropped on the leading edge, the window comes before the
        // target and takes the bigger tile; on the trailing edge, after it.
        WindowManager wm = TwoMonitors(FibonacciLayout.Vertical);

        wm.ActivateWorkspace(Second(wm));
        WindowNode resident = wm.Open("resident");

        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);
        WindowNode dragged = wm.Open("dragged");
        wm.ComputePlacements();

        wm.DropWindow(dragged, 1500, 40);   // top edge: before

        Assert.Equal([dragged, resident], Second(wm).Children.Cast<WindowNode>());

        wm.ComputePlacements();
        Assert.True(dragged.Rect.Bottom <= resident.Rect.Top, "dropped above, so it should sit higher");
    }

    private static (WindowManager Wm, WindowNode A, WindowNode B, WindowNode C) ThreeIn(ILayout layout)
    {
        var wm = new WindowManager();
        wm.AddMonitor(TreeBuilder.Monitor(width: 1000, height: 800));
        wm.AddWorkspace(new WorkspaceNode("1", layout));
        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);

        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");
        WindowNode c = wm.Open("c");
        wm.ComputePlacements();

        return (wm, a, b, c);
    }

    public static TheoryData<string, string, string, string[]> SpiralDrops() => new()
    {
        // dragged, target, edge, expected order. Fibonacci: a takes the left half, b
        // the top-right quarter, c the bottom-right.
        { "c", "a", "left", ["c", "a", "b"] },     // before the biggest tile: c is now the biggest
        { "c", "a", "right", ["a", "c", "b"] },    // after it: c takes a's place in the spiral's second slot
        { "a", "c", "right", ["b", "c", "a"] },    // a was before c, so the index shifts when a leaves
        { "a", "b", "right", ["b", "a", "c"] },
        { "c", "b", "top", ["a", "c", "b"] },      // a cross-axis edge in the old code's terms: still just an order
    };

    [Theory]
    [MemberData(nameof(SpiralDrops))]
    public void ADropInsideASpiralReordersItRatherThanNestingASplit(string dragged, string target, string edge, string[] expected)
    {
        (WindowManager wm, WindowNode a, WindowNode b, WindowNode c) = ThreeIn(FibonacciLayout.Horizontal);
        Dictionary<string, WindowNode> byName = new() { ["a"] = a, ["b"] = b, ["c"] = c };

        WindowNode window = byName[dragged];
        Rect tile = byName[target].Rect;
        (int x, int y) = edge switch
        {
            "left" => (tile.Left + 5, tile.CenterY),
            "right" => (tile.Right - 5, tile.CenterY),
            "top" => (tile.CenterX, tile.Top + 5),
            _ => (tile.CenterX, tile.Bottom - 5),
        };

        WmResult result = wm.DropWindow(window, x, y);

        Assert.True(result.Succeeded, result.RejectionReason);

        WorkspaceNode workspace = wm.FocusedWorkspace!;
        Assert.Same(FibonacciLayout.Horizontal, workspace.Layout);
        Assert.Equal(expected, workspace.Children.Cast<WindowNode>().Select(w => w.Identity.Title));
    }

    [Fact]
    public void ADropInsideAGridReordersIt()
    {
        (WindowManager wm, WindowNode a, WindowNode b, WindowNode c) = ThreeIn(GridLayout.Instance);

        wm.DropWindow(c, a.Rect.Left + 5, a.Rect.CenterY);   // before a

        WorkspaceNode workspace = wm.FocusedWorkspace!;
        Assert.Same(GridLayout.Instance, workspace.Layout);
        Assert.Equal([c, a, b], workspace.Children.Cast<WindowNode>());
    }

    [Fact]
    public void ADropAcrossTheMasterStackAxisMovesWithinTheStack()
    {
        // Master on the left, b and c stacked on the right. Dropping c on b's top edge
        // is across the layout's axis, which used to wrap b in a manual column inside
        // the stack. The stack already is a column: c simply goes above b.
        (WindowManager wm, WindowNode a, WindowNode b, WindowNode c) = ThreeIn(MasterStackLayout.Left);

        wm.DropWindow(c, b.Rect.CenterX, b.Rect.Top + 5);

        WorkspaceNode workspace = wm.FocusedWorkspace!;
        Assert.Same(MasterStackLayout.Left, workspace.Layout);
        Assert.Equal([a, c, b], workspace.Children.Cast<WindowNode>());
        Assert.All(workspace.Children, child => Assert.IsType<WindowNode>(child));

        wm.ComputePlacements();
        Assert.True(c.Rect.Bottom <= b.Rect.Top, $"c should be above b: c={c.Rect} b={b.Rect}");
        Assert.Equal(800, a.Rect.Height);   // still the master, still full height
    }

    [Fact]
    public void ADropOntoTheMasterLeadingEdgeMakesTheDroppedWindowTheMaster()
    {
        (WindowManager wm, WindowNode a, WindowNode b, WindowNode c) = ThreeIn(MasterStackLayout.Left);

        wm.DropWindow(c, a.Rect.Left + 5, a.Rect.CenterY);

        Assert.Equal([c, a, b], wm.FocusedWorkspace!.Children.Cast<WindowNode>());

        wm.ComputePlacements();
        Assert.Equal(800, c.Rect.Height);
    }
}
