using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.EndToEnd.Tests;

/// <summary>
/// The window manager, started as a user starts it, against a real window belonging
/// to another process.
/// </summary>
/// <remarks>
/// <para>
/// One scenario, end to end: <c>shubbak-wm.exe</c> starts with a config, a state
/// directory and a pipe of its own, comes up, notices a window another process opened,
/// runs the config's rule over it, tiles it into the workspace, answers about it over
/// the pipe, and on <c>shubbak stop</c> saves its session, gives the window back
/// uncloaked, and leaves. Every one of those has a unit test somewhere; none of the
/// unit tests could tell if the pieces stopped fitting together, and that is the class
/// of regression this project has had - a hook moved to the wrong thread, a pipe that
/// was not listening when <c>Start</c> returned - whose symptom was "the desktop is
/// broken" and whose tests were all green. The first run of this test found one: a
/// rule's <c>tile</c> refused because an <em>unmanaged</em> window held the foreground.
/// </para>
/// <para>
/// Isolation is real, not hoped for. <c>SHUBBAK_STATE_DIR</c> points the daemon's
/// session, log and crash reports at a directory of the test's own - setting
/// <c>LOCALAPPDATA</c> does not, since the runtime asks the shell for that folder -
/// and <c>SHUBBAK_INSTANCE</c> gives it a pipe and mutexes of its own, so the command
/// line started with the same variable finds this daemon and no other. It still
/// refuses to run beside a window manager of the user's: two would fight over the
/// test window on the desktop, and any result would measure the fight.
/// </para>
/// <para>
/// Every wait is bounded and says what it was waiting for when it gives up; every
/// process this starts has its output drained rather than inherited, so a leaked one
/// cannot hold the test host's pipes open and wedge <c>dotnet test</c>; and every one
/// is ended in <c>Dispose</c> whatever happened in between, the daemon first.
/// </para>
/// <para>
/// Marked <c>Requires=Foreground</c>, the trait the focus-sink tests carry, so the
/// ARM64 job - whose runner image has a sign-in surface in front of the desktop -
/// leaves it out as it leaves them out. The x64 job runs it on every push.
/// </para>
/// </remarks>
[Collection("the desktop")]
[Trait("Requires", "Foreground")]
public sealed class WindowManagerSmokeTests : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ManageTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly string _instance = $"e2e-{Guid.NewGuid():N}"[..20];
    private readonly string _state;
    private readonly string _config;
    private readonly string _pipe;
    private readonly List<Process> _started = [];
    private readonly StringBuilder _daemonOutput = new();

    public WindowManagerSmokeTests()
    {
        FailIfAWindowManagerIsRunning();

        _state = Path.Combine(Path.GetTempPath(), $"shubbak-{_instance}");
        Directory.CreateDirectory(_state);

        _config = Path.Combine(_state, "shubbak.kdl");
        _pipe = IpcProtocol.PipeNameForInstance(_instance);

        File.WriteAllText(_config, TestConfig);
    }

    /// <summary>
    /// The smallest config that proves the pieces fit: one workspace, animation off so
    /// a placement is where the window is and not where it is going, a rule that tiles
    /// the test window - a dialog, which the filter would float - and a rule that leaves
    /// every other window alone, so a developer's desktop is not rearranged for the
    /// duration. No keybindings, so the hook forwards every key; no startup commands, so
    /// no bar, palette or watcher is started.
    /// </summary>
    private const string TestConfig = """
        general {
            default-layout "splith"
            reload-on-save #false
        }

        animation { enabled #false }

        workspaces {
            workspace "e2e"
        }

        rules {
            rule "leave everything else alone" {
                match { !process "winver" }
                do { ignore }
            }

            rule "tile the test window" {
                match { process "winver" }
                do { tile }
            }
        }
        """;

    // ---- the scenario ----------------------------------------------------------------

    [Fact]
    public void TheWindowManagerStartsTilesAForeignWindowAndGivesItBackOnStop()
    {
        Process daemon = StartDaemon();

        WaitFor(() => IpcClient.IsServerRunning(_pipe), StartupTimeout, "the window manager's pipe to appear", daemon);

        Assert.Equal("pong", Send("ping", string.Empty).Data);

        // The daemon's own account of the desktop, for the monitor the window lands on.
        StateSnapshot before = Query("state", IpcJsonContext.Default.StateSnapshot);
        Assert.NotEmpty(before.Monitors);

        Process winver = StartWinver();

        // The rule has to run over it first, so this waits for the verdict the rule
        // gives rather than for the window to appear in the list. Before the rule fix
        // this is where the test stopped: managed, floating, and a refusal in the log
        // about whatever unmanaged window had the foreground.
        //
        // And for the layout pass after the verdict, which is what gives the window its
        // rectangle: the state says "tiling" the moment the rule has spoken, a tick
        // before the window is placed, and on a fast machine the list was read in
        // between - a tiled window of width zero, one run in three.
        WindowInfo tiled = WaitForWindow(
            w => string.Equals(w.ProcessName, "winver", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(w.State, "tiling", StringComparison.OrdinalIgnoreCase) &&
                 w.Width > 0 && w.Height > 0,
            "winver's window to be managed, tiled by the rule, and placed");

        // One tiled window on a workspace fills the work area, less the gaps: its
        // rectangle lies inside whichever monitor it landed on and is most of it.
        MonitorInfoDto monitor = before.Monitors.FirstOrDefault(m => Contains(m, tiled))
            ?? throw new Xunit.Sdk.XunitException($"the tiled window at ({tiled.X},{tiled.Y} {tiled.Width}x{tiled.Height}) is on no monitor the daemon knows: {Describe(before.Monitors)}");

        Assert.True(tiled.Width > monitor.Width / 2, $"tiled width {tiled.Width} is not most of the monitor's {monitor.Width}");
        Assert.True(tiled.Height > monitor.Height / 2, $"tiled height {tiled.Height} is not most of the monitor's {monitor.Height}");

        // Visible and uncloaked while it is the displayed workspace's only window.
        Assert.True(Win32Window.IsVisible((nint)tiled.Handle), "the tiled window is not visible");
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState((nint)tiled.Handle));

        // Said in the tree as well as the list: the workspace has one window on it.
        StateSnapshot during = Query("state", IpcJsonContext.Default.StateSnapshot);
        WorkspaceInfo workspace = Assert.Single(during.Workspaces, ws => ws.Name == "e2e");
        Assert.True(workspace.HasWindows, "the workspace does not report a window");
        Assert.Equal(1, workspace.WindowCount);

        // And a command over the pipe does what a key would.
        Assert.True(Send("command", "toggle-floating").Ok);
        WaitForWindow(w => w.Handle == tiled.Handle && w.State.Equals("floating", StringComparison.OrdinalIgnoreCase), "the window to float on command");
        Assert.True(Send("command", "toggle-floating").Ok);
        WaitForWindow(w => w.Handle == tiled.Handle && w.State.Equals("tiling", StringComparison.OrdinalIgnoreCase), "the window to tile again on command");

        // The way a user stops it. The daemon saves its session, gives the windows back
        // and leaves; the command line waits for that and says so.
        (int exit, string output) = RunCli("stop", StopTimeout);
        Assert.True(exit == 0, $"shubbak stop exited {exit}: {output}");
        Assert.Contains("the window manager has stopped", output, StringComparison.Ordinal);

        WaitFor(() => daemon.HasExited, StopTimeout, "the window manager process to exit after stop", null);
        Assert.True(daemon.ExitCode == 0, $"the daemon exited with {daemon.ExitCode}; it printed: {_daemonOutput}");

        // The window was given back: still there, still visible, not cloaked.
        Assert.False(winver.HasExited, "winver exited during the test");
        Assert.True(Win32Window.IsVisible((nint)tiled.Handle), "the window is not visible after the window manager stopped");
        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState((nint)tiled.Handle));

        // And the daemon left what it promises to leave, where it was told to: a
        // session naming the window, and a log that says it managed it and had no
        // errors doing so.
        string session = Path.Combine(_state, "session.json");
        Assert.True(File.Exists(session), $"no session was saved to {_state}");
        Assert.Contains("winver", File.ReadAllText(session), StringComparison.OrdinalIgnoreCase);

        string log = ReadShared(Path.Combine(_state, "shubbak.log"));
        Assert.Contains("(winver)", log, StringComparison.Ordinal);
        Assert.DoesNotContain("is not managed", log, StringComparison.Ordinal);

        string[] errors = [.. log.Split('\n').Where(l => l.Contains(" ERR ", StringComparison.Ordinal))];
        Assert.True(errors.Length == 0, $"the daemon logged {errors.Length} error(s):\n{string.Join('\n', errors)}");
    }

    [Fact]
    public void AConfigThatDoesNotParseIsSaidAndTheDaemonStillComesUp()
    {
        // A file that will not parse never replaces the one that is running - and at
        // startup there is none running, so the daemon comes up on what the loader could
        // read, says so, and is still there to be asked. A daemon that died on a stray
        // brace would take every concealed window with it.
        File.WriteAllText(_config, "general { default-layout \"splith\" \n");

        Process daemon = StartDaemon();

        WaitFor(() => IpcClient.IsServerRunning(_pipe), StartupTimeout, "the window manager's pipe to appear on a broken config", daemon);
        Assert.Equal("pong", Send("ping", string.Empty).Data);

        (int exit, string output) = RunCli("stop", StopTimeout);
        Assert.True(exit == 0, $"shubbak stop exited {exit}: {output}");
        WaitFor(() => daemon.HasExited, StopTimeout, "the window manager to exit", null);

        string log = ReadShared(Path.Combine(_state, "shubbak.log"));
        Assert.Contains("SHB", log, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDaemonNamesItsOwnConfigFileAndSaysWhenTheDiskHasMovedOn()
    {
        // The daemon was started with --config, pointing at a file no search order
        // finds - which is exactly the case a client resolving the path for itself gets
        // wrong, and the reason the daemon is the one to ask.
        Process daemon = StartDaemon();
        WaitFor(() => IpcClient.IsServerRunning(_pipe), StartupTimeout, "the window manager's pipe to appear", daemon);

        ConfigFileInfo file = Query("config-path", IpcJsonContext.Default.ConfigFileInfo);
        Assert.Equal(_config, file.Path);
        Assert.False(file.Stale, "a file just loaded is reported as changed since");

        // And its text: the whole file, as it is on disk.
        IpcResponse text = Send("query", "config");
        Assert.True(text.Ok, $"query config was refused: {text.Error}");
        Assert.Equal(TestConfig, text.Data);

        // A save the daemon did not follow - reload-on-save is off in this file - is
        // what stale is for: the file and the running configuration no longer agree.
        File.WriteAllText(_config, TestConfig + "\n// a comment the daemon has not read\n");
        Assert.True(Query("config-path", IpcJsonContext.Default.ConfigFileInfo).Stale, "an edited file is not reported as changed");

        // A reload the daemon refuses is announced as one. The file stays stale,
        // because what is running is still the file before it.
        File.WriteAllText(_config, "general { default-layout \"splith\" \n");
        ConfigReloadNotice refused = ReloadAndHear();
        Assert.Equal(_config, refused.Path);
        Assert.False(refused.Accepted, "a reload of a file that does not parse was announced as accepted");
        Assert.True(Query("config-path", IpcJsonContext.Default.ConfigFileInfo).Stale, "a refused file is not reported as changed");
        Assert.Equal("pong", Send("ping", string.Empty).Data);

        // A good file, reloaded, lands - and the two agree again.
        File.WriteAllText(_config, TestConfig);
        ConfigReloadNotice landed = ReloadAndHear();
        Assert.Equal(_config, landed.Path);
        Assert.True(landed.Accepted, "a reload of a file that parses was announced as refused");
        Assert.False(Query("config-path", IpcJsonContext.Default.ConfigFileInfo).Stale, "a reloaded file is reported as changed since");

        (int exit, string output) = RunCli("stop", StopTimeout);
        Assert.True(exit == 0, $"shubbak stop exited {exit}: {output}");
        WaitFor(() => daemon.HasExited, StopTimeout, "the window manager to exit", null);
    }

    // ---- starting things -------------------------------------------------------------

    private Process StartDaemon()
    {
        var info = new ProcessStartInfo(Beside("shubbak-wm.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,

            // Redirected and drained, never inherited. A child that inherits the test
            // host's standard handles and outlives it keeps `dotnet test` waiting for an
            // end of stream that never comes; that is how the first run of this test
            // hung a build for as long as it was allowed to.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        info.ArgumentList.Add("--config");
        info.ArgumentList.Add(_config);
        info.ArgumentList.Add("--log-file");
        info.ArgumentList.Add("--log-level");
        info.ArgumentList.Add("debug");

        Isolate(info);

        Process daemon = Process.Start(info) ?? throw new InvalidOperationException("could not start shubbak-wm.exe");

        daemon.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_daemonOutput) _daemonOutput.AppendLine(e.Data); };
        daemon.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_daemonOutput) _daemonOutput.AppendLine(e.Data); };
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();
        daemon.StandardInput.Close();

        _started.Add(daemon);
        return daemon;
    }

    /// <summary>The whole of the isolation: where the daemon keeps state, and what it calls its pipe.</summary>
    private void Isolate(ProcessStartInfo info)
    {
        info.Environment[Core.ShubbakPaths.StateDirectoryVariable] = _state;
        info.Environment[IpcProtocol.InstanceVariable] = _instance;

        // And no config from the environment, so the explicit one is the only one.
        info.Environment.Remove("XDG_CONFIG_HOME");
    }

    private Process StartWinver()
    {
        // Through the shell, so it inherits none of this process's handles; it has
        // nothing to say and nowhere it should say it.
        Process winver = Process.Start(new ProcessStartInfo("winver.exe") { UseShellExecute = true })
            ?? throw new InvalidOperationException("could not start winver.exe");

        _started.Add(winver);
        return winver;
    }

    /// <summary>Runs the command line against the daemon, bounded, and gathers what it printed.</summary>
    private (int Exit, string Output) RunCli(string arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(Beside("shubbak.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        Isolate(info);

        using Process cli = Process.Start(info) ?? throw new InvalidOperationException("could not start shubbak.exe");
        cli.StandardInput.Close();

        Task<string> stdout = cli.StandardOutput.ReadToEndAsync();
        Task<string> stderr = cli.StandardError.ReadToEndAsync();

        if (!cli.WaitForExit((int)timeout.TotalMilliseconds))
        {
            End(cli);
            throw new TimeoutException($"shubbak {arguments} did not finish within {timeout.TotalSeconds:F0}s");
        }

        if (!Task.WaitAll([stdout, stderr], timeout))
            throw new TimeoutException($"shubbak {arguments} exited but its output never ended");

        return (cli.ExitCode, stdout.Result + stderr.Result);
    }

    private static string Beside(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, file);

        if (!File.Exists(path))
            throw new FileNotFoundException($"{file} is not beside the tests; the project references that should put it there are missing.", path);

        return path;
    }

    // ---- asking the daemon -----------------------------------------------------------

    private IpcResponse Send(string method, string payload)
    {
        using var cancel = new CancellationTokenSource(RequestTimeout);

        try
        {
            return SendAsync(method, payload, cancel.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"the daemon did not answer '{method}' within {RequestTimeout.TotalSeconds:F0}s");
        }
    }

    private async Task<IpcResponse> SendAsync(string method, string payload, CancellationToken token)
    {
        await using var client = new IpcClient { PipeName = _pipe };
        await client.ConnectAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        return await client.SendAsync(method, payload, token).ConfigureAwait(false);
    }

    private T Query<T>(string what, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        IpcResponse response = Send("query", what);
        Assert.True(response.Ok, $"query {what} was refused: {response.Error}");

        return JsonSerializer.Deserialize(response.Data!, type)
            ?? throw new InvalidOperationException($"query {what} answered null");
    }

    /// <summary>
    /// Asks the daemon to re-read its file, as the key does, and returns what it
    /// announced about the outcome.
    /// </summary>
    /// <remarks>
    /// Subscribed before the command is sent, so the announcement cannot slip past in
    /// between; the command goes over a second connection, since a subscribed one
    /// carries nothing else.
    /// </remarks>
    private ConfigReloadNotice ReloadAndHear()
    {
        using var cancel = new CancellationTokenSource(RequestTimeout);

        try
        {
            return ReloadAndHearAsync(cancel.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"the daemon did not announce config.reloaded within {RequestTimeout.TotalSeconds:F0}s. It printed: {_daemonOutput}");
        }
    }

    private async Task<ConfigReloadNotice> ReloadAndHearAsync(CancellationToken token)
    {
        await using var listener = new IpcClient { PipeName = _pipe };
        await listener.ConnectAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        await listener.BeginSubscriptionAsync("config.reloaded", token).ConfigureAwait(false);

        IpcResponse sent = await SendAsync("command", "wm-reload-config", token).ConfigureAwait(false);
        Assert.True(sent.Ok, $"wm-reload-config was refused: {sent.Error}");

        await foreach (IpcEvent raised in listener.ReadEventsAsync(token).ConfigureAwait(false))
        {
            if (raised.Topic == "config.reloaded") return ConfigReloadNotice.Parse(raised.Data);
        }

        throw new InvalidOperationException("the event stream ended before config.reloaded was announced");
    }

    private WindowInfo WaitForWindow(Func<WindowInfo, bool> wanted, string what)
    {
        WindowInfo? found = null;
        IReadOnlyList<WindowInfo> last = [];

        WaitFor(() =>
        {
            last = Query("windows", IpcJsonContext.Default.IReadOnlyListWindowInfo);
            found = last.FirstOrDefault(wanted);
            return found is not null;
        }, ManageTimeout, what, null, () => $"the daemon lists: [{string.Join("; ", last.Select(w => $"{w.ProcessName} [{w.State}] {w.Title}"))}]. It printed: {_daemonOutput}");

        return found!;
    }

    private static bool Contains(MonitorInfoDto monitor, WindowInfo window)
    {
        int centreX = window.X + window.Width / 2;
        int centreY = window.Y + window.Height / 2;

        return centreX >= monitor.X && centreX < monitor.X + monitor.Width &&
               centreY >= monitor.Y && centreY < monitor.Y + monitor.Height;
    }

    private static string Describe(IReadOnlyList<MonitorInfoDto> monitors) =>
        string.Join(", ", monitors.Select(m => $"{m.DeviceId} ({m.X},{m.Y} {m.Width}x{m.Height})"));

    /// <summary>Reads a file the daemon may still hold open for writing.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ---- waiting, always bounded -----------------------------------------------------

    /// <summary>
    /// Polls until the condition holds or the time is up, and fails naming what it was
    /// waiting for - and, when a process was given, whether that process is still there.
    /// </summary>
    private void WaitFor(Func<bool> condition, TimeSpan timeout, string what, Process? watched, Func<string>? detail = null)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;

            if (watched is { HasExited: true })
                throw new InvalidOperationException($"while waiting for {what}, the daemon exited with code {watched.ExitCode}. It printed: {_daemonOutput}");

            Thread.Sleep(50);
        }

        string extra = detail is null ? string.Empty : $" {detail()}";
        throw new TimeoutException($"gave up after {timeout.TotalSeconds:F0}s waiting for {what}.{extra}");
    }

    // ---- guards and cleanup ----------------------------------------------------------

    private static void FailIfAWindowManagerIsRunning()
    {
        if (Process.GetProcessesByName("shubbak-wm").Length == 0) return;

        throw new InvalidOperationException(
            "shubbak-wm is running. This test starts a window manager of its own, and two " +
            "would fight over every window on the desktop - any result would be measuring the " +
            "fight. Stop it (shubbak stop) and run again.");
    }

    /// <summary>Ends a process and everything it started, and never throws: cleanup has nowhere to put an exception.</summary>
    private static void End(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // A descendant that could not be ended, or a process already gone: the plain
            // kill below is the fallback for the first and a no-op for the second.
        }

        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception)
        {
        }

        try
        {
            process.WaitForExit(5000);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        // Whatever the test did or failed to do, nothing it started outlives it. The
        // daemon first, in the order started, so a window manager left mid-scenario does
        // not go on arranging the desktop; then the window it was managing.
        foreach (Process process in _started)
        {
            End(process);
            process.Dispose();
        }

        // Anything the killed daemon left concealed is given back by the same code the
        // command line's `restore` runs, so a failed test does not strand a window.
        try
        {
            List<WindowRecovery.Candidate> cloaked = WindowRecovery.FindCloaked();
            if (cloaked.Count > 0) WindowRecovery.Revive(cloaked);
        }
        catch (Exception)
        {
            // Best effort; a test's cleanup is not the place to fail.
        }

        try { Directory.Delete(_state, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// One window manager at a time: the tests in this collection each start the real
/// daemon, and two would collide on the desktop.
/// </summary>
[CollectionDefinition("the desktop", DisableParallelization = true)]
public sealed class OneDesktopAtATime;
