using Shubbak.Core.Diagnostics;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

/// <summary>
/// The window tree as it appears in <c>shubbak diagnose</c>.
/// </summary>
/// <remarks>
/// This is the section of the report people paste into a bug report, and the one
/// that answers "why is this window the wrong size?" - the nesting is the answer.
/// It lived inside the daemon as two private methods and had no test at all, so
/// nothing would have noticed it rendering the wrong shape.
/// </remarks>
public sealed class TreeRendererTests
{
    [Fact]
    public void AnEmptyTreeSaysSoRatherThanRenderingNothing()
    {
        // A blank section reads as the report being broken. An empty desktop is a
        // real state and has to look different from a failure.
        Assert.Equal("(empty)", TreeRenderer.Render(new RootNode(), focused: null));
    }

    [Fact]
    public void MonitorsWorkspacesAndWindowsNestByIndentation()
    {
        MonitorNode monitor = TreeBuilder.Monitor();
        WorkspaceNode workspace = TreeBuilder.Workspace("1");
        WindowNode window = TreeBuilder.Window("notepad");

        monitor.AddWorkspace(workspace);
        workspace.Add(window);
        RootNode root = TreeBuilder.Root(monitor);

        string[] lines = TreeRenderer.Render(root, focused: null)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("monitor ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  workspace ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("    window ", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ContainersDeepenTheIndentForTheirChildren()
    {
        // The nesting is the whole point: a window two containers down has to look
        // two containers down.
        MonitorNode monitor = TreeBuilder.Monitor();
        WindowNode inner = TreeBuilder.Window("inner");
        ContainerNode nested = TreeBuilder.Column(inner);
        WorkspaceNode workspace = TreeBuilder.Workspace("1", SplitLayout.Horizontal, nested);

        monitor.AddWorkspace(workspace);
        RootNode root = TreeBuilder.Root(monitor);

        string[] lines = TreeRenderer.Render(root, focused: null)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        string container = Assert.Single(lines, l => l.Contains("container", StringComparison.Ordinal));
        string windowLine = Assert.Single(lines, l => l.Contains("window ", StringComparison.Ordinal));

        Assert.StartsWith("    container", container, StringComparison.Ordinal);
        Assert.StartsWith("      window", windowLine, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFocusedWindowIsMarked()
    {
        MonitorNode monitor = TreeBuilder.Monitor();
        WindowNode first = TreeBuilder.Window("first");
        WindowNode second = TreeBuilder.Window("second");
        WorkspaceNode workspace = TreeBuilder.Workspace("1");

        monitor.AddWorkspace(workspace);
        workspace.Add(first);
        workspace.Add(second);
        RootNode root = TreeBuilder.Root(monitor);

        string rendered = TreeRenderer.Render(root, focused: second);

        Assert.Single(rendered.Split('\n'), l => l.Contains("[focused]", StringComparison.Ordinal));
        Assert.Contains("\"second\" (second) Tiling", rendered, StringComparison.Ordinal);

        // Focus is passed in rather than read from the tree, so asking for none is a
        // legitimate question and must not mark anything.
        Assert.DoesNotContain("[focused]", TreeRenderer.Render(root, focused: null), StringComparison.Ordinal);
    }

    [Fact]
    public void ALongTitleIsTruncatedSoTheColumnsLineUp()
    {
        MonitorNode monitor = TreeBuilder.Monitor();
        WorkspaceNode workspace = TreeBuilder.Workspace("1");
        var window = new WindowNode(0x1234, new WindowIdentity
        {
            Title = new string('x', 200),
            ProcessName = "test",
            ClassName = "TestClass",
        });

        monitor.AddWorkspace(workspace);
        workspace.Add(window);
        RootNode root = TreeBuilder.Root(monitor);

        string rendered = TreeRenderer.Render(root, focused: null);

        Assert.DoesNotContain(new string('x', 41), rendered, StringComparison.Ordinal);
        Assert.Contains("\u2026", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void RatiosAreRenderedInvariantlyWhateverTheMachineIsSetTo()
    {
        // A report saying ratio=0,750 because the reporter's machine is German is a
        // difference nobody reading it wants to have to notice.
        MonitorNode monitor = TreeBuilder.Monitor();
        WorkspaceNode workspace = TreeBuilder.Workspace("1");
        WindowNode window = TreeBuilder.Window();

        monitor.AddWorkspace(workspace);
        workspace.Add(window);
        RootNode root = TreeBuilder.Root(monitor);

        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("de-DE");

            string rendered = TreeRenderer.Render(root, focused: null);

            Assert.Contains("ratio=1.000", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("ratio=1,000", rendered, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void EveryMonitorAndWorkspaceAppears()
    {
        MonitorNode first = TreeBuilder.Monitor("\\\\.\\DISPLAY1");
        MonitorNode second = TreeBuilder.Monitor("\\\\.\\DISPLAY2", x: 1920);

        first.AddWorkspace(TreeBuilder.Workspace("1"));
        first.AddWorkspace(TreeBuilder.Workspace("2"));
        second.AddWorkspace(TreeBuilder.Workspace("3"));

        RootNode root = TreeBuilder.Root(first, second);

        string rendered = TreeRenderer.Render(root, focused: null);

        Assert.Contains("DISPLAY1", rendered, StringComparison.Ordinal);
        Assert.Contains("DISPLAY2", rendered, StringComparison.Ordinal);
        Assert.Equal(3, rendered.Split('\n').Count(l => l.TrimStart().StartsWith("workspace ", StringComparison.Ordinal)));

        // Exactly one monitor is primary in this arrangement, and it is marked.
        Assert.Single(rendered.Split('\n'), l => l.Contains("(primary)", StringComparison.Ordinal));
    }
}
