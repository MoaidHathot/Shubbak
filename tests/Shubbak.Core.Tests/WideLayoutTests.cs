using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

public sealed class WideLayoutTests
{
    [Theory]
    [InlineData(128)]
    [InlineData(256)]
    public void WideContainersKeepNormalizedSharesAndVisibleTiles(int count)
    {
        var workspace = new WorkspaceNode("wide");
        var area = new Rect(0, 0, count * 64, 800);
        var monitor = new MonitorNode("wide", area, area);
        monitor.AddWorkspace(workspace);
        for (int i = 0; i < count; i++) workspace.Add(TreeBuilder.Window(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.InRange(workspace.Children.Sum(n => n.SizeRatio), 0.999999, 1.000001);
        IReadOnlyList<Placement> placements = new LayoutEngine().Arrange(workspace, ArrangeOptions.Default);
        Assert.All(placements, p => Assert.True(p.Rect.Width > 0));
        Assert.Equal(area.Width, placements.Sum(p => p.Rect.Width));
    }

    [Fact]
    public void ResizingBeyondOneHundredSiblingsDoesNotInvertTheClampBounds()
    {
        var workspace = new WorkspaceNode("wide");
        var area = new Rect(0, 0, 8192, 800);
        var monitor = new MonitorNode("wide", area, area);
        monitor.AddWorkspace(workspace);
        for (int i = 0; i < 128; i++) workspace.Add(TreeBuilder.Window(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var engine = new LayoutEngine();
        engine.Arrange(workspace, ArrangeOptions.Default);
        Node first = workspace.Children[0];
        double? ratio = LayoutEngine.RatioForExtent(workspace, first, Axis.Horizontal, 80, LayoutOptions.Default);
        Assert.NotNull(ratio);
        workspace.SetChildRatio(first, ratio.Value);
        Assert.Equal(80, engine.Arrange(workspace, ArrangeOptions.Default)[0].Rect.Width);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(128)]
    public void NormalizingLopsidedSharesReservesTheFloorWithoutAddingExtraMass(int count)
    {
        var container = new ContainerNode();
        for (int i = 0; i < count; i++) container.Add(TreeBuilder.Window(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var random = new Random(42);
        var ratios = new double[count];
        for (int pass = 0; pass < 100; pass++)
        {
            for (int i = 0; i < count; i++) ratios[i] = i % 3 == 0 ? 0 : random.NextDouble();
            container.SetRatios(ratios);
            Assert.InRange(container.Children.Sum(n => n.SizeRatio), 0.999999, 1.000001);
            Assert.All(container.Children, n => Assert.True(n.SizeRatio >= Node.MinimumShare(count)));
        }
    }
}
