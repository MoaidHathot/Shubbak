using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Resizing in the automatic layouts.
/// </summary>
/// <remarks>
/// <para>
/// <c>resize</c> looked for an ancestor whose <c>PrimaryAxis</c> matched, and the
/// spiral, the grid and monocle have none - so in the layout most people start in,
/// <c>alt+u</c> and <c>alt+p</c> did nothing but log "No container splits along
/// Horizontal". The spiral honours every ratio it is given; only the question was
/// wrong. The grid and monocle genuinely cannot, and now say why.
/// </para>
/// <para>
/// Master-stack stays as its author decided: the divider moves, the stack shares its
/// space equally, and a resize across the divider is refused with that reason.
/// </para>
/// </remarks>
public sealed class ResizeInAutomaticLayoutsTests
{
    private static WindowManager Spiral(int windows, out WindowNode[] opened)
    {
        WindowManager wm = WmFixture.Create();
        wm.SetLayout(FibonacciLayout.Horizontal);

        opened = new WindowNode[windows];
        for (int i = 0; i < windows; i++) opened[i] = wm.Open($"w{i}");

        wm.Arrange();
        return wm;
    }

    [Fact]
    public void TheFirstWindowOfASpiralWidensWhenAsked()
    {
        WindowManager wm = Spiral(2, out WindowNode[] w);
        wm.FocusWindow(w[0]);

        int before = w[0].Rect.Width;

        WmResult result = wm.Resize(Axis.Horizontal, 0.1);

        Assert.True(result.Succeeded, result.RejectionReason);
        Assert.True(result.Has<ContainerResized>());

        wm.Arrange();
        Assert.True(w[0].Rect.Width > before, $"{w[0].Rect.Width} should exceed {before}");
        Assert.True(w[1].Rect.Width < 1920 - before, "the neighbour gave the space up");
    }

    [Fact]
    public void ASpiralWindowCutOnTheOtherAxisStillGrows()
    {
        // The second window of a spiral is cut top/bottom from the remainder of the
        // first; widening it has to move the first divider, and its own weight does
        // both. Growing it grows it - which is all a person pressing the key wants.
        WindowManager wm = Spiral(3, out WindowNode[] w);
        wm.FocusWindow(w[1]);

        Rect before = w[1].Rect;

        Assert.True(wm.Resize(Axis.Horizontal, 0.1).Succeeded);
        wm.Arrange();

        Assert.True(w[1].Rect.Area > before.Area, "the window did not grow");

        Assert.True(wm.Resize(Axis.Vertical, 0.1).Succeeded);
        wm.Arrange();
    }

    [Fact]
    public void ShrinkingASpiralWindowGivesTheSpaceToTheRest()
    {
        WindowManager wm = Spiral(3, out WindowNode[] w);
        wm.FocusWindow(w[0]);

        int before = w[0].Rect.Width;

        Assert.True(wm.Resize(Axis.Horizontal, -0.1).Succeeded);
        wm.Arrange();

        Assert.True(w[0].Rect.Width < before);
    }

    [Fact]
    public void AloneOnAWorkspaceThereIsNothingToTakeSpaceFrom()
    {
        WindowManager wm = Spiral(1, out _);

        WmResult result = wm.Resize(Axis.Horizontal, 0.1);

        Assert.False(result.Succeeded);
        Assert.Contains("alone", result.RejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGridRefusesAndNamesItself()
    {
        WindowManager wm = WmFixture.Create();
        wm.SetLayout(GridLayout.Instance);
        wm.Open("a");
        wm.Open("b");
        wm.Arrange();

        WmResult result = wm.Resize(Axis.Horizontal, 0.1);

        Assert.False(result.Succeeded);
        Assert.Contains("grid", result.RejectionReason!, StringComparison.Ordinal);
        Assert.Contains("fibonacci", result.RejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MonocleRefusesAndSaysWhy()
    {
        WindowManager wm = WmFixture.Create();
        wm.SetLayout(MonocleLayout.Instance);
        wm.Open("a");
        wm.Open("b");
        wm.Arrange();

        WmResult result = wm.Resize(Axis.Vertical, 0.1);

        Assert.False(result.Succeeded);
        Assert.Contains("Monocle", result.RejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MasterStackMovesTheDividerAndRefusesAcrossIt()
    {
        WindowManager wm = WmFixture.Create();
        wm.SetLayout(MasterStackLayout.Left);
        WindowNode master = wm.Open("master");
        wm.Open("s1");
        wm.Open("s2");
        wm.Arrange();

        wm.FocusWindow(master);
        int before = master.Rect.Width;

        Assert.True(wm.Resize(Axis.Horizontal, 0.1).Succeeded);
        wm.Arrange();
        Assert.True(master.Rect.Width > before);

        WmResult across = wm.Resize(Axis.Vertical, 0.1);

        Assert.False(across.Succeeded);
        Assert.Contains("share their space equally", across.RejectionReason!, StringComparison.Ordinal);
        Assert.Contains("--width", across.RejectionReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLayoutsSayWhatTheyResize()
    {
        static bool Resizes(ILayout layout, Axis axis) => layout.Resizes(axis);
        static string? Why(ILayout layout, Axis axis) => layout.WhyNotResizable(axis);

        Assert.True(Resizes(FibonacciLayout.Horizontal, Axis.Horizontal));
        Assert.True(Resizes(FibonacciLayout.Horizontal, Axis.Vertical));
        Assert.True(Resizes(SplitLayout.Horizontal, Axis.Horizontal));
        Assert.False(Resizes(SplitLayout.Horizontal, Axis.Vertical));
        Assert.True(Resizes(MasterStackLayout.Top, Axis.Vertical));
        Assert.False(Resizes(MasterStackLayout.Top, Axis.Horizontal));
        Assert.False(Resizes(GridLayout.Instance, Axis.Horizontal));
        Assert.False(Resizes(MonocleLayout.Instance, Axis.Vertical));

        Assert.Null(Why(FibonacciLayout.Horizontal, Axis.Vertical));
        Assert.NotNull(Why(SplitLayout.Vertical, Axis.Horizontal));
    }

    [Fact]
    public void DraggingAnEdgeInASpiralResizesToo()
    {
        WindowManager wm = Spiral(2, out WindowNode[] w);

        Rect current = w[0].Rect;
        var wider = new Rect(current.X, current.Y, current.Width + 200, current.Height);

        Assert.True(wm.ResizeFromDrag(w[0], wider).Succeeded);
        wm.Arrange();

        Assert.True(w[0].Rect.Width > current.Width);
    }
}
