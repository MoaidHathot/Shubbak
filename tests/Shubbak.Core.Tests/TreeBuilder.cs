using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

/// <summary>
/// Helpers for building trees compactly, so tests read as a description of the
/// arrangement under test rather than as setup boilerplate.
/// </summary>
internal static class TreeBuilder
{
    private static int s_counter;

    public static WindowNode Window(string? name = null)
    {
        int n = Interlocked.Increment(ref s_counter);
        name ??= $"win{n}";

        return new WindowNode(0x1000 + n, new WindowIdentity
        {
            ProcessName = name,
            ClassName = $"{name}Class",
            Title = name,
            ProcessId = 1000 + n,
        });
    }

    public static ContainerNode Container(ILayout layout, params Node[] children)
    {
        var container = new ContainerNode(layout);
        foreach (Node child in children) container.Add(child);
        return container;
    }

    public static ContainerNode Row(params Node[] children) =>
        Container(SplitLayout.Horizontal, children);

    public static ContainerNode Column(params Node[] children) =>
        Container(SplitLayout.Vertical, children);

    public static WorkspaceNode Workspace(string name = "1", ILayout? layout = null, params Node[] children)
    {
        var workspace = new WorkspaceNode(name, layout ?? SplitLayout.Horizontal);
        foreach (Node child in children) workspace.Add(child);
        return workspace;
    }

    /// <summary>A 1920x1080 monitor at the origin with no reserved space.</summary>
    public static MonitorNode Monitor(
        string deviceId = "\\\\.\\DISPLAY1",
        int x = 0,
        int y = 0,
        int width = 1920,
        int height = 1080)
    {
        var bounds = new Rect(x, y, width, height);
        return new MonitorNode(deviceId, bounds, bounds) { IsPrimary = x == 0 && y == 0 };
    }

    public static RootNode Root(params MonitorNode[] monitors)
    {
        var root = new RootNode();
        foreach (MonitorNode monitor in monitors) root.AddMonitor(monitor);
        return root;
    }

    /// <summary>
    /// Arranges <paramref name="workspace"/> on a monitor of the given size and
    /// returns each window's rectangle keyed by title.
    /// </summary>
    public static Dictionary<string, Rect> ArrangeToMap(
        WorkspaceNode workspace,
        ArrangeOptions? options = null,
        int width = 1920,
        int height = 1080)
    {
        MonitorNode monitor = Monitor(width: width, height: height);
        monitor.AddWorkspace(workspace);
        _ = Root(monitor);

        var engine = new LayoutEngine();
        IReadOnlyList<Placement> placements =
            engine.Arrange(workspace, options ?? ArrangeOptions.Default);

        return placements.ToDictionary(p => p.Window.Identity.Title, p => p.Rect, StringComparer.Ordinal);
    }
}
