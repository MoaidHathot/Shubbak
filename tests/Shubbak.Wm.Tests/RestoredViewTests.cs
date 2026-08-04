using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// Which workspace is shown once the saved session has been restored.
/// </summary>
/// <remarks>
/// <para>
/// Restoring the view puts each monitor back on the workspace it was showing last
/// time. That is right, and on its own it is not enough: Shubbak is almost always
/// started from a terminal, and the session usually remembers some other workspace
/// as the active one - so the first thing the window manager did was switch away
/// from the window the user had just typed into.
/// </para>
/// <para>
/// From the outside that reads as having lost the terminal, or as the thing having
/// crashed and taken the desktop with it. The session is a memory of last time; the
/// foreground window is where the user is now. When they disagree, now wins.
/// </para>
/// </remarks>
public sealed class RestoredViewTests
{
    private static int s_handles;

    private static WindowNode Window(string name = "terminal")
    {
        int handle = 0x1000 + Interlocked.Increment(ref s_handles);

        return new WindowNode(handle, new WindowIdentity
        {
            Title = name,
            ProcessName = name,
            ClassName = $"{name}Class",
        });
    }

    private static WorkspaceNode Workspace(string name) => new(name, SplitLayout.Horizontal);

    private static MonitorNode Monitor(string deviceId = "\\\\.\\DISPLAY1", int x = 0)
    {
        var bounds = new Rect(x, 0, 1920, 1080);

        return new MonitorNode(deviceId, bounds, bounds) { IsPrimary = x == 0 };
    }

    /// <summary>A monitor showing the first workspace, with the rest behind it.</summary>
    private static MonitorNode Showing(params string[] workspaces)
    {
        MonitorNode monitor = Monitor();

        foreach (string name in workspaces) monitor.AddWorkspace(Workspace(name));

        var root = new RootNode();
        root.AddMonitor(monitor);

        return monitor;
    }

    private static WindowNode PlaceOn(MonitorNode monitor, string workspace, string name = "terminal")
    {
        WindowNode window = Window(name);

        monitor.FindWorkspace(workspace)!.Add(window);
        return window;
    }

    [Fact]
    public void AWindowOnAHiddenWorkspaceDragsTheViewToIt()
    {
        // The reported bug, exactly: the terminal the user launched Shubbak from ends
        // up on a workspace the restored view is not showing, so the desktop switches
        // away from it the moment the window manager starts.
        MonitorNode monitor = Showing("1", "2");
        WindowNode terminal = PlaceOn(monitor, "2");

        WorkspaceNode? target = WmDaemon.WorkspaceToKeepInView(terminal);

        Assert.NotNull(target);
        Assert.Equal("2", target.Name);
    }

    [Fact]
    public void AWindowAlreadyOnTheShownWorkspaceChangesNothing()
    {
        // The session and the desktop agree. Overriding a perfectly good restored
        // view would be its own bug.
        MonitorNode monitor = Showing("1", "2");
        WindowNode terminal = PlaceOn(monitor, "1");

        Assert.Null(WmDaemon.WorkspaceToKeepInView(terminal));
    }

    [Fact]
    public void NothingInFrontChangesNothing()
    {
        // No foreground window at all, which is the ordinary state during startup
        // before anything has been focused.
        Assert.Null(WmDaemon.WorkspaceToKeepInView(null));
    }

    [Fact]
    public void AWindowShubbakDoesNotManageChangesNothing()
    {
        // An unmanaged window sits on no workspace, so there is no view that would
        // show it and nothing to switch to. The session's choice stands.
        Assert.Null(WmDaemon.WorkspaceToKeepInView(Window("unmanaged")));
    }

    [Fact]
    public void AWindowOnAWorkspaceNoMonitorHasTakenChangesNothing()
    {
        // A workspace the config declares but that no monitor is hosting yet. It
        // cannot be displayed, so switching to it would show the user nothing.
        WorkspaceNode orphan = Workspace("9");
        WindowNode window = Window();

        orphan.Add(window);

        Assert.Null(WmDaemon.WorkspaceToKeepInView(window));
    }

    [Fact]
    public void EachMonitorIsJudgedAgainstItsOwnView()
    {
        // Two monitors, each showing its own workspace. A window in front on the
        // second must not be measured against what the first is showing.
        MonitorNode first = Monitor("\\\\.\\DISPLAY1");
        MonitorNode second = Monitor("\\\\.\\DISPLAY2", x: 1920);

        first.AddWorkspace(Workspace("1"));
        second.AddWorkspace(Workspace("2"));
        second.AddWorkspace(Workspace("3"));

        var root = new RootNode();
        root.AddMonitor(first);
        root.AddMonitor(second);

        // On the second monitor's shown workspace: nothing to do.
        WindowNode onShown = Window("shown");
        second.FindWorkspace("2")!.Add(onShown);

        Assert.Null(WmDaemon.WorkspaceToKeepInView(onShown));

        // On the second monitor's hidden workspace: that monitor switches, not the first.
        WindowNode onHidden = Window("hidden");
        second.FindWorkspace("3")!.Add(onHidden);

        WorkspaceNode? target = WmDaemon.WorkspaceToKeepInView(onHidden);

        Assert.NotNull(target);
        Assert.Equal("3", target.Name);
        Assert.Same(second, target.Monitor);

        // And the first monitor is untouched by any of it.
        Assert.Equal("1", first.ActiveWorkspace!.Name);
    }
}
