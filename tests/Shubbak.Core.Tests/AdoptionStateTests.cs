using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// What survives a window being taken under management, and what a new workspace
/// starts out as.
/// </summary>
public sealed class AdoptionStateTests
{
    private static WindowManager Create(WmOptions? options = null) =>
        WmFixture.Create(options: options, monitors: 1, workspaceNames: ["1"]);

    [Theory]
    [InlineData(WindowState.Minimised)]
    [InlineData(WindowState.Floating)]
    [InlineData(WindowState.Maximised)]
    [InlineData(WindowState.Fullscreen)]
    public void AStateTheCallerAlreadyDeterminedIsKept(WindowState state)
    {
        // Adoption used to overwrite this with the configured default. A window
        // detected as minimised was handed a tile it could not fill - revealing a
        // minimised window is correctly refused - so the layout had a hole in it with
        // whatever lay behind showing through.
        WindowManager wm = Create();

        WindowNode window = TreeBuilder.Window("restored");
        wm.ManageWindow(window, workspace: null, state: state);

        Assert.Equal(state, window.State);
    }

    [Fact]
    public void ANewWindowStillTakesTheConfiguredState()
    {
        // Nothing determined it, so configuration decides - which is what an ordinary
        // newly opened window wants.
        WindowManager wm = Create(new WmOptions { InitialWindowState = WindowState.Floating });

        WindowNode window = TreeBuilder.Window("new");
        wm.ManageWindow(window);

        Assert.Equal(WindowState.Floating, window.State);
    }

    [Fact]
    public void AMinimisedWindowIsNotPlacedOnScreen()
    {
        // The consequence that was actually visible. It holds a place in the tree but
        // must not be shown, or it occupies a tile it cannot draw into.
        WindowManager wm = Create();

        wm.Open("ordinary");

        WindowNode minimised = TreeBuilder.Window("minimised");
        wm.ManageWindow(minimised, workspace: null, state: WindowState.Minimised);

        Placement placement = Assert.Single(
            wm.ComputePlacements(), p => ReferenceEquals(p.Window, minimised));

        Assert.False(placement.Visible);
    }

    [Fact]
    public void ANewWorkspaceUsesTheConfiguredDefaultLayout()
    {
        // The config key was read and then never consulted, so every workspace was a
        // horizontal split whatever the file said.
        var wm = new WindowManager(new WmOptions { DefaultLayout = LayoutRegistry.Resolve("grid") });

        wm.AddMonitor(TreeBuilder.Monitor(@"\\.\DISPLAY1", x: 0, width: 1920, height: 1080));

        var workspace = new WorkspaceNode("1");
        Assert.True(wm.AddWorkspace(workspace).Succeeded);

        Assert.Equal("grid", workspace.Layout.Name);
    }

    [Fact]
    public void AWorkspaceThatAlreadyHasALayoutKeepsIt()
    {
        // A layout chosen deliberately - by a restored session, or by a workspace
        // declaration - outranks the default.
        var wm = new WindowManager(new WmOptions { DefaultLayout = LayoutRegistry.Resolve("grid") });

        wm.AddMonitor(TreeBuilder.Monitor(@"\\.\DISPLAY1", x: 0, width: 1920, height: 1080));

        var workspace = new WorkspaceNode("1") { Layout = LayoutRegistry.Resolve("monocle") };
        wm.AddWorkspace(workspace);

        Assert.Equal("monocle", workspace.Layout.Name);
    }

    [Fact]
    public void WithNoDefaultConfiguredTheRegistryDefaultStands()
    {
        WindowManager wm = Create();
        WorkspaceNode workspace = wm.Root.Monitors[0].Workspaces[0];

        Assert.Same(LayoutRegistry.Default, workspace.Layout);
    }
}
