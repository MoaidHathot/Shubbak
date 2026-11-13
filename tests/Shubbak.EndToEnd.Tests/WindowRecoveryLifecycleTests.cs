using System.Diagnostics;
using System.Text.Json;
using Shubbak.Core.Wm;
using Shubbak.Ipc;
using Shubbak.Native;
using Shubbak.TestWindow;

namespace Shubbak.EndToEnd.Tests;

public sealed partial class WindowManagerSmokeTests
{
    private void ConfigureRecovery(string hideMethod = "hide") => File.WriteAllText(_config, $$"""
        general {
            default-layout "splith"
            reload-on-save #false
            hide-method "{{hideMethod}}"
        }
        animation { enabled #false; }
        workspaces {
            workspace "current" monitor="0"
            workspace "chat" monitor="0"
        }
        rules {
            rule "only test-owned windows" {
                match { !process "Shubbak.TestWindow"; }
                do { ignore; }
            }
        }
        """);

    private void SaveRecoverySession(params (nint Handle, string Workspace)[] placements)
    {
        RememberedWindow[] windows = [.. placements.Select(placement =>
        {
            var identity = Win32Window.BuildIdentity(placement.Handle);
            return new RememberedWindow(identity.ProcessName, identity.ClassName, 0,
                placement.Workspace, [], false, "Tiling", placement.Handle, identity.ProcessId);
        })];
        var session = new Session(1, DateTimeOffset.Now, windows);
        File.WriteAllText(Path.Combine(_state, "session.json"),
            JsonSerializer.Serialize(session, SessionJsonContext.Default.Session));
    }

    private Process StartRecoveryDaemon()
    {
        Process daemon = StartDaemon();
        WaitFor(() => IpcClient.IsServerRunning(_pipe), StartupTimeout, "the recovery test daemon", daemon);
        Assert.Equal("pong", Send("ping", string.Empty).Data);
        return daemon;
    }

    private void Command(string command)
    {
        IpcResponse response = Send("command", command);
        Assert.True(response.Ok, $"{command} was refused: {response.Error}");
    }

    private void AssertManaged(params nint[] handles)
    {
        long[] actual = [.. Query("windows", IpcJsonContext.Default.IReadOnlyListWindowInfo)
            .Select(w => w.Handle).Order()];
        Assert.Equal(handles.Select(h => (long)h).Order(), actual);
    }

    private void AssertWorkspaceCount(string name, int count) => Assert.Equal(count,
        Assert.Single(Query("state", IpcJsonContext.Default.StateSnapshot).Workspaces, w => w.Name == name).WindowCount);

    private void StopRecoveryDaemon(Process daemon)
    {
        (int exit, string output) = RunCli("stop", StopTimeout);
        Assert.True(exit == 0, $"shubbak stop exited {exit}: {output}");
        WaitForExit(daemon, "the recovery test daemon to exit");
    }

    [Fact]
    public void HiddenHelpersStayUnmanagedAcrossReloadResumeAndRestart()
    {
        ConfigureRecovery();
        using var window = new ControlledWindow(withHiddenHelper: true);
        SaveRecoverySession((window.Handle, "chat"));
        Process daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == window.Handle && w.Width > 0, "the remembered chat window");
        AssertManaged(window.Handle);
        Command("focus --workspace current");
        WaitFor(() => WindowCommitter.IsConcealed(window.Handle), ManageTimeout, "the inactive chat window", daemon);

        // Stale monitor views must not override the live workspace on resume either.
        MonitorInfoDto monitor = Assert.Single(Query("state", IpcJsonContext.Default.StateSnapshot).Monitors,
            m => m.ActiveWorkspace == "current");
        Session saved = SessionStore.Load(Path.Combine(_state, "session.json"))!;
        File.WriteAllText(Path.Combine(_state, "session.json"), JsonSerializer.Serialize(
            saved with { Monitors = [new RememberedMonitor(monitor.DeviceId, "chat", true, monitor.DevicePath)] },
            SessionJsonContext.Default.Session));

        for (int i = 0; i < 3; i++)
        {
            // Reload discards the startup set-aside cache, as it did in the recorded
            // Teams failure. The later resume must not replay that old session file.
            Assert.True(ReloadAndHear().Accepted);
            Command("wm-suspend");
            Command("wm-resume");
            AssertManaged(window.Handle);
            AssertWorkspaceCount("chat", 1);
            Assert.True(Assert.Single(Query("state", IpcJsonContext.Default.StateSnapshot).Workspaces,
                w => w.Name == "current").Active);
            Assert.False(Win32Window.IsVisible(window.HelperHandle));
        }

        StopRecoveryDaemon(daemon);
        Assert.Equal(window.Handle, Assert.Single(SessionStore.Load(Path.Combine(_state, "session.json"))!.Windows).Handle);
        daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == window.Handle && w.Width > 0, "the restarted chat window");
        AssertManaged(window.Handle);
        AssertWorkspaceCount("chat", 1);
        Assert.False(Win32Window.IsVisible(window.HelperHandle));
        StopRecoveryDaemon(daemon);
    }

    [Fact]
    public void ResumeAdoptsATrayWindowShownWhileSuspendedIntoTheLiveWorkspace()
    {
        ConfigureRecovery();
        using var window = new ControlledWindow(withHiddenHelper: true);
        SaveRecoverySession((window.Handle, "chat"));
        Process daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == window.Handle && w.Width > 0, "the original chat window");
        Command("focus --workspace current");
        Command("wm-suspend");
        window.SetVisible(true, helper: true);
        Command("wm-resume");

        WaitForWindow(w => w.Handle == window.HelperHandle && w.Width > 0, "the tray window shown during suspension");
        AssertManaged(window.Handle, window.HelperHandle);
        AssertWorkspaceCount("chat", 1);
        AssertWorkspaceCount("current", 1);
        StopRecoveryDaemon(daemon);
    }

    [Fact]
    public void ResumeReleasesClosedAndTrayHiddenWindowsAndSavesTheEmptySession()
    {
        ConfigureRecovery();
        using var hidden = new ControlledWindow();
        using var closed = new ControlledWindow();
        SaveRecoverySession((hidden.Handle, "current"), (closed.Handle, "current"));
        Process daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == hidden.Handle && w.Width > 0, "the window that will hide");
        WaitForWindow(w => w.Handle == closed.Handle && w.Width > 0, "the window that will close");

        Command("wm-suspend");
        hidden.SetVisible(false);
        closed.Dispose();
        Command("wm-resume");

        AssertManaged();
        Assert.False(Win32Window.IsVisible(hidden.Handle));
        StopRecoveryDaemon(daemon);
        Assert.Empty(SessionStore.Load(Path.Combine(_state, "session.json"))!.Windows);
        daemon = StartRecoveryDaemon();
        AssertManaged();
        Assert.False(Win32Window.IsVisible(hidden.Handle));
        StopRecoveryDaemon(daemon);
    }

    [Fact]
    public void StoppingWhileSuspendedDoesNotSaveATrayWindowForResurrection()
    {
        ConfigureRecovery();
        using var window = new ControlledWindow();
        SaveRecoverySession((window.Handle, "current"));
        Process daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == window.Handle && w.Width > 0, "the window before suspension");

        Command("wm-suspend");
        window.SetVisible(false);
        StopRecoveryDaemon(daemon);

        Assert.Empty(SessionStore.Load(Path.Combine(_state, "session.json"))!.Windows);
        Assert.False(Win32Window.IsVisible(window.Handle));
        daemon = StartRecoveryDaemon();
        AssertManaged();
        Assert.False(Win32Window.IsVisible(window.Handle));
        StopRecoveryDaemon(daemon);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ResumeKeepsNativeWindowStateChangesAndTheStateToRestore(bool floating, bool maximise)
    {
        ConfigureRecovery();
        using var window = new ControlledWindow();
        Process daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == window.Handle && w.Width > 0, "the window before minimisation");
        Command($"focus-window {window.Handle}");
        if (floating) Command("float");

        Command("wm-suspend");
        if (maximise) WindowActions.Maximise(window.Handle);
        else WindowActions.Minimise(window.Handle);
        WaitFor(() => maximise ? Win32Window.IsMaximised(window.Handle) : Win32Window.IsMinimised(window.Handle),
            ManageTimeout, "the user's native state change", daemon);
        Command("wm-resume");
        WaitForWindow(w => w.Handle == window.Handle && w.State == (maximise ? "maximised" : "minimised"),
            "the preserved native window state");
        Assert.True(maximise ? Win32Window.IsMaximised(window.Handle) : Win32Window.IsMinimised(window.Handle));

        Command("wm-suspend");
        WindowActions.Restore(window.Handle);
        WaitFor(() => !Win32Window.IsMinimised(window.Handle) && !Win32Window.IsMaximised(window.Handle),
            ManageTimeout, "the user's restoration", daemon);
        Command("wm-resume");
        WaitForWindow(w => w.Handle == window.Handle && w.State == (floating ? "floating" : "tiling"),
            "the original base state after restoration");
        StopRecoveryDaemon(daemon);
    }

    [Fact]
    public void StartupKeepsANativeMinimisedWindowOutOfTheTilingFlow()
    {
        ConfigureRecovery();
        using var window = new ControlledWindow();
        SaveRecoverySession((window.Handle, "current"));
        WindowActions.Minimise(window.Handle);
        WaitFor(() => Win32Window.IsMinimised(window.Handle), ManageTimeout, "the window to minimise before startup", null);

        Process daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == window.Handle && w.State == "minimised", "the native minimisation to survive restoration");
        Assert.True(Win32Window.IsMinimised(window.Handle));
        StopRecoveryDaemon(daemon);
    }

    [Fact]
    public void ResumeKeepsAFloatingWindowWhereItMovedWhileSuspended()
    {
        MonitorSource.EnableDpiAwareness();
        ConfigureRecovery();
        using var window = new ControlledWindow();
        Process daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == window.Handle && w.Width > 0, "the floating test window");
        Command($"focus-window {window.Handle}");
        Command("float");
        WindowInfo floated = WaitForWindow(w => w.Handle == window.Handle && w.State == "floating", "the window to float");
        Command("wm-suspend");

        WindowInfo wanted = floated with { X = 150, Y = 150, Width = 500, Height = 350 };
        new WindowCommitter().CommitOne(window.Handle, new Core.Geometry.Rect(wanted.X, wanted.Y, wanted.Width, wanted.Height));
        WaitFor(() => At(window.Handle, wanted), ManageTimeout,
            "the user's move while suspended", daemon,
            () => $"actual {WindowCommitter.VisibleBounds(window.Handle)}, wanted {wanted}");
        // A DPI-unaware foreign window rounds at 150% scaling (observed +1 px).
        // Resume must keep where it actually landed, rather than the ideal request.
        var actual = WindowCommitter.VisibleBounds(window.Handle);
        Command("wm-resume");
        WindowInfo moved = WaitForWindow(w => w.Handle == window.Handle && w.X == actual.X && w.Y == actual.Y &&
            w.Width == actual.Width && w.Height == actual.Height, "the floating window's new placement");
        Assert.True(At(window.Handle, moved));
        StopRecoveryDaemon(daemon);
    }

    [Fact]
    public void ResumeReconcilesTheTitleAndTheUsersForegroundWindow()
    {
        ConfigureRecovery();
        using var window = new ControlledWindow(withHiddenHelper: true);
        window.SetVisible(true, helper: true);
        Process daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == window.Handle && w.Width > 0, "the first focus test window");
        WaitForWindow(w => w.Handle == window.HelperHandle && w.Width > 0, "the second focus test window");
        Command($"focus-window {window.HelperHandle}");
        WaitFor(() => Win32Window.GetForeground() == window.HelperHandle, ManageTimeout, "focus before suspension", daemon);

        Command("wm-suspend");
        window.Rename();
        Assert.True(WindowActions.Focus(window.Handle));
        WaitFor(() => Win32Window.GetForeground() == window.Handle, ManageTimeout, "the user's foreground change", daemon);
        Command("wm-resume");

        WaitForWindow(w => w.Handle == window.Handle && w.Focused && w.Title == ControlledWindow.RenamedTitle,
            "the title and foreground changes missed during suspension");
        Assert.Equal(window.Handle, Win32Window.GetForeground());
        StopRecoveryDaemon(daemon);
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("cloak")]
    public void CrashRecoveryKeepsTheRealWindowAndLeavesItsHelperHidden(string hideMethod)
    {
        ConfigureRecovery(hideMethod);
        using var window = new ControlledWindow(withHiddenHelper: true);
        if (hideMethod == "cloak")
            WaitFor(() => Win32ApplicationView.HasView(window.Handle), ManageTimeout, "the shell to catalogue the window before cloaking", null);
        SaveRecoverySession((window.Handle, "chat"));
        Process daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == window.Handle && w.Width > 0, "the window before the crash");
        Command("focus --workspace current");
        WaitFor(() => WindowCommitter.IsConcealed(window.Handle), ManageTimeout, "the inactive chat window to be concealed", daemon);
        if (hideMethod == "cloak")
        {
            Assert.True(Win32Window.IsVisible(window.Handle));
            Assert.Equal(Win32Window.CloakState.Shell, Win32Window.GetCloakState(window.Handle));
        }
        else Assert.False(Win32Window.IsVisible(window.Handle));

        // Only our isolated daemon is killed. Its window and saved state survive,
        // exercising the actual recovery boundary rather than a synthetic verdict.
        End(daemon);
        WaitForExit(daemon, "the deliberately killed daemon");
        daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == window.Handle && w.Width > 0, "the real window after crash recovery");
        AssertManaged(window.Handle);
        AssertWorkspaceCount("chat", 1);
        Assert.False(Win32Window.IsVisible(window.HelperHandle));

        // Recovery must transfer concealment ownership, not merely add a tree node.
        // Otherwise a later resume mistakes our still-hidden window for a tray hide.
        Command("wm-suspend");
        Command("wm-resume");
        AssertManaged(window.Handle);

        Command("focus --workspace chat");
        WaitFor(() => !WindowCommitter.IsConcealed(window.Handle), ManageTimeout, "the recovered chat window to become visible", daemon);
        Assert.False(Win32Window.IsVisible(window.HelperHandle));
        Command("focus --workspace current");
        WaitFor(() => WindowCommitter.IsConcealed(window.Handle), ManageTimeout, "the recovered window to be concealed again", daemon);
        StopRecoveryDaemon(daemon);
        WaitFor(() => !WindowCommitter.IsConcealed(window.Handle), ManageTimeout, "clean exit to give back the recovered window", null);
    }

    [Fact]
    public void StartupMatchesSurvivingHandlesBeforeNewWindowsOfTheSameApplication()
    {
        ConfigureRecovery();
        using var original = new ControlledWindow(withHiddenHelper: true);
        original.SetVisible(false);
        using var newcomer = new ControlledWindow();
        SaveRecoverySession((original.Handle, "chat"));

        Process daemon = StartRecoveryDaemon();
        WaitForWindow(w => w.Handle == original.Handle && w.Width > 0, "the concealed original window");
        WaitForWindow(w => w.Handle == newcomer.Handle && w.Width > 0, "the new visible window");
        AssertManaged(original.Handle, newcomer.Handle);
        // The newcomer follows normal placement (the focused workspace by default).
        // What must not depend on enumeration order is the original window's claim.
        Assert.Equal("chat", Assert.Single(
            Query("all-windows", IpcJsonContext.Default.IReadOnlyListWindowCandidate),
            w => w.Handle == original.Handle).Workspace);
        Assert.False(Win32Window.IsVisible(original.HelperHandle));
        StopRecoveryDaemon(daemon);
    }
}
