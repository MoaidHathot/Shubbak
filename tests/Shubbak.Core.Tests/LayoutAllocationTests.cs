using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

public sealed class LayoutAllocationTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(80)]
    public void RepeatedMixedNestedArrangementsAllocateNothingAndPreserveTheTree(int count)
    {
        var root = new RootNode();
        var monitor = TreeBuilder.Monitor();
        var workspace = new WorkspaceNode("1");
        root.AddMonitor(monitor);
        monitor.AddWorkspace(workspace);
        var nested = new ContainerNode(FibonacciLayout.Horizontal);
        workspace.Add(nested);
        for (int i = 0; i < count; i++)
        {
            var window = TreeBuilder.Window($"{i}");
            if (i % 3 == 0) window.State = WindowState.Floating;
            nested.Add(window);
        }
        Node[] nodes = [.. nested.Children];
        double[] ratios = [.. nodes.Select(n => n.SizeRatio)];
        var engine = new LayoutEngine();
        for (int i = 0; i < 1000; i++) engine.Arrange(root, ArrangeOptions.Default);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) engine.Arrange(root, ArrangeOptions.Default);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        for (int i = 0; i < count; i++)
        {
            Assert.Same(nested, nodes[i].Parent);
            Assert.Equal(ratios[i], nodes[i].SizeRatio);
            Assert.Same(nodes[i], nested.Children[i]);
        }
    }

    [Fact]
    public void InverseResizeAllocatesNothingAndNeverMutatesLiveRatios()
    {
        WindowNode first = TreeBuilder.Window("first"), second = TreeBuilder.Window("second"), floating = TreeBuilder.Window("floating");
        floating.State = WindowState.Floating;
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [first, second, floating]);
        MonitorNode monitor = TreeBuilder.Monitor();
        monitor.AddWorkspace(workspace);
        var engine = new LayoutEngine();
        engine.Arrange(workspace, ArrangeOptions.Default);
        double original = first.SizeRatio;
        for (int i = 0; i < 200; i++) LayoutEngine.RatioForExtent(workspace, first, Axis.Horizontal, 300, LayoutOptions.Default);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) LayoutEngine.RatioForExtent(workspace, first, Axis.Horizontal, 300, LayoutOptions.Default);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(original, first.SizeRatio);
        Assert.Same(workspace, first.Parent);
    }

    [Fact]
    public void LayoutAndInverseSizingObserveTheOriginalTreeEvenDuringTheCalculation()
    {
        WindowNode first = TreeBuilder.Window("first"), second = TreeBuilder.Window("second"), floating = TreeBuilder.Window("floating");
        floating.State = WindowState.Floating;
        WorkspaceNode workspace = TreeBuilder.Workspace(children: [first, second, floating]);
        MonitorNode monitor = TreeBuilder.Monitor();
        monitor.AddWorkspace(workspace);
        double ratio = first.SizeRatio;
        int observations = 0;
        workspace.Layout = new ObservingLayout(() =>
        {
            observations++;
            Assert.Same(workspace, first.Parent);
            Assert.Same(workspace, second.Parent);
            Assert.Equal(ratio, first.SizeRatio);
            Assert.Equal(3, workspace.Children.Count);
        });
        new LayoutEngine().Arrange(workspace, ArrangeOptions.Default);
        LayoutEngine.RatioForExtent(workspace, first, Axis.Horizontal, 300, LayoutOptions.Default);
        Assert.True(observations >= 3);
    }

    private sealed class ObservingLayout(Action observe) : ILayout
    {
        public string Name => "observe";
        public Axis? PrimaryAxis => Axis.Horizontal;
        public void Arrange(ReadOnlySpan<double> weights, Rect area, in LayoutOptions options, Span<Rect> destination)
        {
            observe();
            SplitLayout.Horizontal.Arrange(weights, area, in options, destination);
        }
        public int ResolveInsertIndex(ContainerNode container, Node? reference) => container.Count;
        public Node? Navigate(ContainerNode container, Node from, Direction direction) => null;
    }
}
