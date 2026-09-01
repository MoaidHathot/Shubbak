using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// When pressing a workspace's key bounces to the previous workspace, and when it
/// does not.
/// </summary>
/// <remarks>
/// <para>
/// With toggle-workspace-on-refocus enabled, pressing the key of the workspace you are
/// already on takes you back to the one before. The tests that matter are the cases that
/// look similar but are not.
/// </para>
/// <para>
/// A workspace displayed on a monitor you are not currently on: pressing its key means
/// "go there". Bouncing instead sent the user to a third workspace they had never asked
/// for, and only the workspaces that happened to be displayed on the other monitors
/// showed the fault - which is why it looked as though particular workspace names were
/// cursed.
/// </para>
/// <para>
/// And the move key aimed at the workspace the window is already on, which is not a
/// re-focus at all. It says so itself now, because <c>move --workspace N --focus</c> is
/// one command; as a pair of commands the second half could not tell the difference.
/// </para>
/// </remarks>
public sealed class WorkspaceRefocusToggleTests
{
    private static WindowManager Create(bool toggle = true)
    {
        var wm = new WindowManager(new WmOptions { ToggleWorkspaceOnRefocus = toggle });

        wm.AddMonitor(TreeBuilder.Monitor(@"\\.\DISPLAY1", x: 0, width: 1920, height: 1080));
        wm.AddMonitor(TreeBuilder.Monitor(@"\\.\DISPLAY2", x: 1920, width: 1920, height: 1080));

        return wm;
    }

    private static WorkspaceNode Add(WindowManager wm, string name, int monitor)
    {
        var workspace = new WorkspaceNode(name);
        wm.AddWorkspace(workspace, wm.Root.Monitors[monitor]);
        return workspace;
    }

    [Fact]
    public void PressingTheKeyOfTheWorkspaceYouAreOnBouncesBack()
    {
        WindowManager wm = Create();
        Add(wm, "1", 0);
        Add(wm, "2", 0);

        wm.FocusWorkspace("1");
        wm.FocusWorkspace("2");

        wm.FocusWorkspace("2");

        Assert.Equal("1", wm.FocusedWorkspace!.Name);
    }

    [Fact]
    public void PressingTheKeyOfAWorkspaceOnAnotherMonitorGoesThere()
    {
        // The reported bug. Workspace 3 sits displayed on the second monitor; the user
        // is on the first. Pressing its key must land on 3, not bounce the second
        // monitor to whatever it showed previously.
        WindowManager wm = Create();
        Add(wm, "1", 0);
        Add(wm, "2", 1);
        Add(wm, "3", 1);

        // Give the second monitor a previous workspace, so a bounce has somewhere to
        // go and the test can tell the two behaviours apart.
        wm.FocusWorkspace("2");
        wm.FocusWorkspace("3");

        // Now go back to the first monitor and press 3's key.
        wm.FocusWorkspace("1");
        Assert.Same(wm.Root.Monitors[0], wm.FocusedMonitor);

        wm.FocusWorkspace("3");

        Assert.Equal("3", wm.FocusedWorkspace!.Name);
        Assert.Same(wm.Root.Monitors[1], wm.FocusedMonitor);
    }

    [Fact]
    public void MoveThenFocusLandsOnTheWorkspaceMovedTo()
    {
        // What "alt+shift+3" runs. Both halves must agree on the destination; the
        // user pressed it and arrived at 1.
        WindowManager wm = Create();
        Add(wm, "1", 0);
        Add(wm, "2", 1);
        Add(wm, "3", 1);

        wm.FocusWorkspace("2");
        wm.FocusWorkspace("3");

        wm.FocusWorkspace("1");
        WindowNode window = wm.Open("terminal");

        wm.MoveToWorkspace("3", focus: true);

        Assert.Equal("3", wm.FocusedWorkspace!.Name);
        Assert.Equal("3", window.Workspace!.Name);
        Assert.Same(window, wm.FocusedWindow);
    }

    [Fact]
    public void MovingToTheWorkspaceYouAreOnDoesNothingAtAll()
    {
        // alt+shift+1 pressed by mistake while already on 1. The window has nowhere
        // to go, so nothing goes anywhere - and in particular the screen does not.
        //
        // This was the whole reason --focus is part of the move command. As two
        // commands on one key, the move did nothing and the trailing `focus
        // --workspace 1` was an ordinary re-focus, which the toggle answered by
        // bouncing to 2: a key about moving a window moved no window and switched
        // workspace instead.
        WindowManager wm = Create();
        Add(wm, "1", 0);
        Add(wm, "2", 0);

        wm.FocusWorkspace("1");
        wm.FocusWorkspace("2");
        wm.FocusWorkspace("1");

        WindowNode window = wm.Open("terminal");

        wm.MoveToWorkspace("1", focus: true);

        Assert.Equal("1", window.Workspace!.Name);
        Assert.Equal("1", wm.FocusedWorkspace!.Name);
    }

    [Fact]
    public void TheKeyThatOnlySwitchesStillBouncesAfterOneThatOnlyMoves()
    {
        // The mistaken press must not eat the bounce that follows it. alt+shift+1 on
        // 1 does nothing; alt+1 straight after still means "take me back to 2".
        WindowManager wm = Create();
        Add(wm, "1", 0);
        Add(wm, "2", 0);

        wm.FocusWorkspace("1");
        wm.FocusWorkspace("2");
        wm.FocusWorkspace("1");

        wm.Open("terminal");
        wm.MoveToWorkspace("1", focus: true);

        wm.FocusWorkspace("1");

        Assert.Equal("2", wm.FocusedWorkspace!.Name);
    }

    [Fact]
    public void BouncingStillWorksOnASecondMonitorYouAreAlreadyOn()
    {
        // The fix must not disable the toggle away from the first monitor.
        WindowManager wm = Create();
        Add(wm, "1", 0);
        Add(wm, "2", 1);
        Add(wm, "3", 1);

        wm.FocusWorkspace("2");
        wm.FocusWorkspace("3");
        Assert.Same(wm.Root.Monitors[1], wm.FocusedMonitor);

        wm.FocusWorkspace("3");

        Assert.Equal("2", wm.FocusedWorkspace!.Name);
    }

    [Fact]
    public void TheToggleIsOffWhenTheOptionIsOff()
    {
        WindowManager wm = Create(toggle: false);
        Add(wm, "1", 0);
        Add(wm, "2", 0);

        wm.FocusWorkspace("1");
        wm.FocusWorkspace("2");
        wm.FocusWorkspace("2");

        Assert.Equal("2", wm.FocusedWorkspace!.Name);
    }
}
