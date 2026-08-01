using System.Diagnostics;
using Shubbak.Config;
using Shubbak.Core.Animation;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Shubbak.Core.Commands;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.Wm;

/// <summary>
/// The Shubbak daemon: wires the platform layer to the state machine.
/// </summary>
/// <remarks>
/// <para>
/// This is the only class that talks to both sides, and it is deliberately thin.
/// Everything it does is translate: a WinEvent becomes a
/// <see cref="WindowManager"/> call, a keystroke becomes a
/// <see cref="WmCommand"/>, and the resulting placements become one
/// <c>DeferWindowPos</c> transaction.
/// </para>
/// <para>
/// All of it runs on a single thread with a message pump. Both hook kinds require
/// one, and single-threading removes the need for locks around the tree - which
/// matters, because a tree mutated concurrently with an arrange pass would produce
/// corrupt geometry that is essentially impossible to reproduce.
/// </para>
/// </remarks>
public sealed class WmDaemon : IDisposable
{
    private readonly WindowManager _wm;
    private readonly CommandExecutor _executor;
    private readonly BindingTable _bindings = new();
    private readonly WindowCommitter _committer = new();
    private readonly MessageLoop _loop = new();
    private readonly AnimationEngine _animation = new();

    private readonly Dictionary<nint, WindowNode> _managed = [];
    private readonly HashSet<nint> _ignored = [];

    private WinEventSource? _winEvents;
    private KeyboardSource? _keyboard;

    private ShubbakConfig _config = ShubbakConfig.Default;
    private string? _configPath;

    private bool _layoutDirty;
    private bool _disposed;

    private Session? _session;
    private readonly HashSet<int> _claimedSessionEntries = [];
    private bool _restoring;

    private long _lastSessionSaveTicks;

    /// <summary>
    /// Whether the log level came from the command line, and so must not be
    /// overridden by config.
    /// </summary>
    private readonly bool _logLevelFromCommandLine =
        Environment.GetCommandLineArgs().Contains("--log-level", StringComparer.Ordinal);

    private long _lastTickTicks;

    private readonly WinEventNotification[] _eventScratch = new WinEventNotification[256];
    private readonly KeyEvent[] _keyScratch = new KeyEvent[64];

    /// <summary>
    /// Per-frame animation output. Pre-allocated because the tick path must not
    /// allocate (docs/adr/0001-language-choice.md, constraint 2).
    /// </summary>
    private AnimationFrame[] _frameScratch = new AnimationFrame[128];

    /// <summary>Reused across frames so committing never allocates either.</summary>
    private readonly List<Placement> _commitScratch = [];

    private IpcServer? _ipc;

    /// <summary>
    /// Work handed in by IPC threads, to be run on the daemon thread.
    /// </summary>
    /// <remarks>
    /// The tree must never be touched from a pipe thread: an arrange pass running
    /// concurrently with a mutation would observe a half-changed tree and produce
    /// geometry that is essentially impossible to reproduce or debug.
    /// </remarks>
    private readonly Queue<Action> _inbox = new();
    private readonly Lock _inboxGate = new();

    /// <summary>Where the session is stored; null uses the default location.</summary>
    private readonly string? _sessionPath;

    public WmDaemon(string? sessionPath = null)
    {
        _sessionPath = sessionPath;
        _wm = new WindowManager();
        _executor = new CommandExecutor(_wm);
    }

    /// <summary>Raised for every state change, for the IPC server to forward.</summary>
    public event Action<IReadOnlyList<WmEvent>>? EventsProduced;

    public WindowManager Manager => _wm;

    public ShubbakConfig Config => _config;

    /// <summary>Starts the daemon and pumps messages until <see cref="Stop"/>.</summary>
    public void Run(string? configPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Before any window or monitor is touched: without it Windows reports
        // virtualised coordinates on scaled displays and every computed rectangle
        // lands in the wrong place.
        MonitorSource.EnableDpiAwareness();

        _configPath = configPath;
        LoadConfig(configPath, initial: true);

        SyncMonitors();
        AdoptExistingWindows();

        _winEvents = new WinEventSource();
        _winEvents.Start();

        _keyboard = new KeyboardSource();
        _keyboard.Start(_bindings.IsBound);

        _ipc = new IpcServer();
        _ipc.Start(new WmDaemonIpc(this).HandleAsync);

        RunStartupCommands();

        _layoutDirty = true;
        _loop.Tick += OnTick;

        Log.Info(LogCategory.Wm, $"started: {_managed.Count} windows adopted, " +
            $"{_wm.Root.Monitors.Count} monitors, {_config.Keybindings.Count} keybindings, " +
            $"{_config.Rules.Count} rules");

        _loop.Run(TimeSpan.FromMilliseconds(8));

        // A clean shutdown is the one chance to record the arrangement exactly as
        // the user left it, rather than as it was up to thirty seconds earlier.
        if (_managed.Count > 0) SessionStore.Save(_wm.Root, _sessionPath);

        RestoreConcealedWindows();
    }

    /// <summary>
    /// Brings every concealed window back before exiting.
    /// </summary>
    /// <remarks>
    /// Without this, every window on an inactive workspace is left off screen when
    /// Shubbak stops - process still running, nothing in Alt+Tab or the taskbar, and
    /// no way for the user to reach it. Cloaking makes that recoverable on the next
    /// run, but leaving the desktop as it was found is the correct behaviour.
    /// </remarks>
    private void RestoreConcealedWindows()
    {
        int restored = _committer.RestoreAll();

        if (restored > 0)
            Log.Info(LogCategory.Window, $"restored {restored} concealed window(s) on shutdown");
    }

    public void Stop() => _loop.Stop();

    // ---- the tick ----------------------------------------------------------

    private void OnTick()
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            double deltaMs = _lastTickTicks == 0
                ? 0
                : (now - _lastTickTicks) * 1000.0 / Stopwatch.Frequency;
            _lastTickTicks = now;

            DrainKeyboard();
            DrainWindowEvents();
            DrainInbox();

            if (_layoutDirty)
            {
                _layoutDirty = false;
                ApplyLayout();
            }

            if (_animation.IsAnimating) AdvanceAnimation(deltaMs);

            MaybeSaveSession(now);
        }
        catch (Exception ex)
        {
            // A daemon that dies leaves every managed window stranded, so the tick
            // never propagates. Anything unexpected is logged and the loop carries on.
            Log.Error(LogCategory.Wm, "tick failed", ex);
        }
    }

    private void DrainKeyboard()
    {
        if (_keyboard is null) return;

        int count = _keyboard.Drain(_keyScratch, _keyScratch.Length);

        for (int i = 0; i < count; i++)
        {
            KeyEvent key = _keyScratch[i];
            if (!key.IsKeyDown) continue;

            Keybinding? binding = _bindings.Resolve(key.VirtualKey, key.Modifiers);

            if (binding is null)
            {
                // Reaching here means the hook claimed the keystroke but nothing
                // resolved it - almost always a non-pass-through binding mode
                // swallowing keys, which is worth being able to see.
                if (Log.IsEnabled(LogLevel.Trace))
                    Log.Trace(LogCategory.Hook,
                        $"swallowed vk=0x{key.VirtualKey:X2} mods={key.Modifiers} (no binding)");

                continue;
            }

            if (Log.IsEnabled(LogLevel.Debug))
                Log.Debug(LogCategory.Hook, $"{binding.Key.Display} -> {Describe(binding.Commands)}");

            Execute(binding.Commands);
        }
    }

    private static string Describe(IReadOnlyList<WmCommand> commands) =>
        string.Join("; ", commands.Select(c => c.Name));

    private void DrainWindowEvents()
    {
        if (_winEvents is null) return;

        int count = _winEvents.Drain(_eventScratch, _eventScratch.Length);

        for (int i = 0; i < count; i++)
            HandleWindowEvent(_eventScratch[i]);
    }

    /// <summary>Runs work queued by IPC threads, on the daemon thread.</summary>
    private void DrainInbox()
    {
        while (true)
        {
            Action? work;

            lock (_inboxGate)
            {
                if (_inbox.Count == 0) return;
                work = _inbox.Dequeue();
            }

            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.Ipc, "request handler failed", ex);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the daemon thread and awaits its result.
    /// </summary>
    internal Task<T> InvokeAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_inboxGate)
        {
            _inbox.Enqueue(() =>
            {
                try { completion.SetResult(work()); }
                catch (Exception ex) { completion.SetException(ex); }
            });
        }

        return completion.Task;
    }

    private void HandleWindowEvent(WinEventNotification notification)
    {
        nint handle = notification.Handle;

        // LOCATIONCHANGE is excluded from tracing on purpose. S4 measured 122 of
        // them per second from a single dragged window; logging them would drown
        // everything else and slow the very thing being diagnosed.
        if (notification.Kind != WinEventKind.LocationChanged && Log.IsEnabled(LogLevel.Trace))
        {
            Log.Trace(LogCategory.Window,
                $"{notification.Kind} 0x{handle:X} \"{Truncate(Win32Window.GetTitle(handle), 48)}\"");
        }

        switch (notification.Kind)
        {
            case WinEventKind.Created:
            case WinEventKind.Shown:
            case WinEventKind.Uncloaked:
                TryManage(handle);
                break;

            case WinEventKind.Destroyed:
            case WinEventKind.Cloaked:
                TryUnmanage(handle);
                break;

            case WinEventKind.Hidden:
                // A hidden window may simply be on a workspace we just switched
                // away from - which we hid ourselves. Only unmanage windows that
                // have genuinely gone.
                if (!Win32Window.Exists(handle)) TryUnmanage(handle);
                break;

            case WinEventKind.TitleChanged:
                if (_managed.TryGetValue(handle, out WindowNode? titled))
                    Publish(_wm.UpdateTitle(titled, Win32Window.GetTitle(handle)));
                break;

            case WinEventKind.Foreground:
                if (_managed.TryGetValue(handle, out WindowNode? focused))
                {
                    if (!ReferenceEquals(_wm.FocusedWindow, focused))
                        Publish(_wm.FocusWindow(focused));
                }
                else
                {
                    TryManage(handle);
                }
                break;

            case WinEventKind.MinimiseStart:
                if (_managed.TryGetValue(handle, out WindowNode? minimising))
                    Publish(_wm.SetWindowState(minimising, WindowState.Minimised));
                break;

            case WinEventKind.MinimiseEnd:
                if (_managed.TryGetValue(handle, out WindowNode? restoring))
                    Publish(_wm.SetWindowState(restoring, WindowState.Tiling));
                break;

            case WinEventKind.MoveSizeEnd:
                HandleUserMove(handle);
                break;

            case WinEventKind.LocationChanged:
                // The firehose: S4 measured 122/s from a single dragged window, and
                // every move we make ourselves echoes back here. Ignored entirely -
                // MoveSizeEnd is the event that actually carries intent.
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Handles the user finishing a drag or resize with the mouse.
    /// </summary>
    /// <remarks>
    /// For a floating window the new geometry is simply adopted. For a tiled window
    /// the move is undone by the next layout pass - dragging a tile is a request to
    /// swap it, which needs hit-testing against the tree and is P2 work. Snapping it
    /// back is the honest behaviour meanwhile; leaving it displaced would look like
    /// the window manager had lost track of it.
    /// </remarks>
    private void HandleUserMove(nint handle)
    {
        if (!_managed.TryGetValue(handle, out WindowNode? window)) return;

        if (window.State == WindowState.Floating)
        {
            window.FloatingRect = Win32Window.GetBounds(handle);
            _committer.Forget(handle);
            return;
        }

        _layoutDirty = true;
    }

    // ---- window lifecycle --------------------------------------------------

    private void TryManage(nint handle)
    {
        if (_managed.ContainsKey(handle) || _ignored.Contains(handle)) return;

        ManageDecision decision = WindowFilter.Evaluate(handle);

        if (!decision.Manageable)
        {
            // At trace level this is the answer to "why is that window floating?",
            // recorded as it happens rather than reconstructed afterwards.
            if (Log.IsEnabled(LogLevel.Trace))
                Log.Trace(LogCategory.Window,
                    $"skip 0x{handle:X} \"{Truncate(Win32Window.GetTitle(handle), 40)}\": {decision.Explain()}");

            return;
        }

        var attributes = ToAttributes(handle);

        if (ShouldIgnore(attributes))
        {
            // Remembered, so the same window is not re-evaluated on every one of the
            // many events it will generate over its lifetime.
            _ignored.Add(handle);

            Log.Debug(LogCategory.Rule,
                $"ignoring 0x{handle:X} \"{Truncate(attributes.Title, 40)}\" ({attributes.ProcessName})");

            return;
        }

        var window = new WindowNode(handle, Win32Window.BuildIdentity(handle))
        {
            State = WindowFilter.InitialStateFor(handle, _config.InitialWindowState),
        };

        // A saved session wins during the initial adoption pass, so a restart puts
        // windows back where they were rather than piling them onto whichever
        // workspace happens to be active.
        WorkspaceNode? workspace = (_restoring ? RestoredWorkspaceFor(window) : null) ?? WorkspaceFor(handle);

        _managed[handle] = window;
        Publish(_wm.ManageWindow(window, workspace));

        Log.Info(LogCategory.Window,
            $"managed 0x{handle:X} \"{Truncate(attributes.Title, 40)}\" ({attributes.ProcessName}) " +
            $"-> workspace {window.Workspace?.Name ?? "?"}");

        ApplyRules(window, attributes, RuleTrigger.OnManage);

        _layoutDirty = true;
    }

    private void TryUnmanage(nint handle)
    {
        _ignored.Remove(handle);

        if (!_managed.Remove(handle, out WindowNode? window)) return;

        _committer.Forget(handle);
        _animation.Remove(window.Handle);
        Publish(_wm.UnmanageWindow(window));

        Log.Info(LogCategory.Window,
            $"unmanaged 0x{handle:X} \"{Truncate(window.Identity.Title, 40)}\"");

        _layoutDirty = true;
    }

    /// <summary>
    /// Builds a diagnostic report describing the live window manager.
    /// </summary>
    /// <remarks>
    /// Must be called on the daemon thread: it walks the tree.
    /// </remarks>
    internal string BuildDiagnosticReport(string reason)
    {
        var report = new DiagnosticReport(reason).AddEnvironment();

        report.AddSection("Window manager", string.Join('\n', new[]
        {
            $"- **Monitors**: {_wm.Root.Monitors.Count}",
            $"- **Workspaces**: {_wm.Root.AllWorkspaces().Count()}",
            $"- **Managed windows**: {_managed.Count}",
            $"- **Ignored windows**: {_ignored.Count}",
            $"- **Focused**: {_wm.FocusedWindow?.Identity.Title ?? "(none)"}",
            $"- **Binding mode**: {_wm.BindingMode ?? "(default)"}",
            $"- **Paused**: {_wm.IsPaused}",
            $"- **Animating**: {_animation.ActiveCount}",
            $"- **Keybindings**: {_config.Keybindings.Count}",
            $"- **Rules**: {_config.Rules.Count}",
            $"- **IPC clients**: {_ipc?.ClientCount ?? 0}",
        }));

        report.AddCodeSection("Window tree", DescribeTree());

        if (_configPath is not null && File.Exists(_configPath))
        {
            try
            {
                report.AddCodeSection($"Config ({_configPath})", File.ReadAllText(_configPath), "kdl");
            }
            catch (IOException ex)
            {
                report.AddSection("Config", $"(could not be read: {ex.Message})");
            }
        }

        return report.AddRecentLog().AddFooter().ToString();
    }

    /// <summary>
    /// Renders the tree as indented text.
    /// </summary>
    /// <remarks>
    /// A drawing of the tree is worth far more than the same facts as JSON when the
    /// question is "why is this window the wrong size?" - the nesting is the answer,
    /// and nesting is what an indented rendering shows at a glance.
    /// </remarks>
    private string DescribeTree()
    {
        var output = new System.Text.StringBuilder();

        foreach (MonitorNode monitor in _wm.Root.Monitors)
        {
            output.AppendLine(
                $"monitor {monitor.DeviceId}{(monitor.IsPrimary ? " (primary)" : "")} " +
                $"{monitor.Bounds} work={monitor.WorkArea} dpi={monitor.Dpi}");

            foreach (WorkspaceNode workspace in monitor.Workspaces)
            {
                output.AppendLine(
                    $"  workspace \"{workspace.Name}\"{(workspace.IsActive ? " [active]" : "")} " +
                    $"layout={workspace.Layout.Name} {workspace.Rect}");

                foreach (Node child in workspace.Children) DescribeNode(child, output, depth: 2);
            }
        }

        return output.Length == 0 ? "(empty)" : output.ToString();
    }

    private void DescribeNode(Node node, System.Text.StringBuilder output, int depth)
    {
        string indent = new(' ', depth * 2);

        switch (node)
        {
            case WindowNode window:
                output.AppendLine(
                    $"{indent}window 0x{window.Handle:X} \"{Truncate(window.Identity.Title, 40)}\" " +
                    $"({window.Identity.ProcessName}) {window.State} " +
                    $"ratio={window.SizeRatio:F3} {window.Rect}" +
                    $"{(ReferenceEquals(window, _wm.FocusedWindow) ? " [focused]" : "")}");
                break;

            case ContainerNode container:
                output.AppendLine(
                    $"{indent}container layout={container.Layout.Name} " +
                    $"ratio={container.SizeRatio:F3} {container.Rect}");

                foreach (Node child in container.Children) DescribeNode(child, output, depth + 1);
                break;

            default:
                break;
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "\u2026");

    /// <summary>
    /// Places a new window on the workspace of the monitor it appeared on.
    /// </summary>
    /// <remarks>
    /// Using the window's own monitor rather than the focused one matters on
    /// multi-monitor setups: an application launched onto a secondary display should
    /// stay there rather than teleporting to wherever focus happened to be.
    /// </remarks>
    private WorkspaceNode? WorkspaceFor(nint handle)
    {
        MonitorInfo? info = MonitorSource.ForWindow(handle);
        if (info is null) return null;

        MonitorNode? monitor = _wm.Root.FindMonitor(info.Value.DeviceId);
        return monitor?.ActiveWorkspace;
    }

    /// <summary>
    /// The workspace a window should be restored to from the saved session.
    /// </summary>
    /// <remarks>
    /// Only consulted while adopting the windows that existed at startup. Applying
    /// it later would mean a window opened an hour into a session could be yanked
    /// onto a workspace the user was not looking at.
    /// </remarks>
    private WorkspaceNode? RestoredWorkspaceFor(WindowNode window)
    {
        if (_session is null) return null;

        RememberedWindow? remembered = SessionStore.Match(_session, window.Identity, _claimedSessionEntries);
        if (remembered is null) return null;

        WorkspaceNode? workspace = _wm.Root.FindWorkspace(remembered.Workspace);
        if (workspace is null) return null;

        foreach (string tag in remembered.Tags) window.AddTagForRestore(tag);
        window.IsSticky = remembered.Sticky;

        if (Enum.TryParse(remembered.State, out WindowState state) && state != WindowState.Minimised)
            window.State = state;

        Log.Debug(LogCategory.Window,
            $"restored \"{Truncate(window.Identity.Title, 32)}\" to workspace {workspace.Name}");

        return workspace;
    }

    /// <summary>Brings windows that already exist under management at startup.</summary>
    private void AdoptExistingWindows()
    {
        _session = SessionStore.Load(_sessionPath);

        if (_session is not null)
        {
            Log.Info(LogCategory.Wm,
                $"session loaded: {_session.Windows.Count} remembered windows from {_session.SavedAt:g}");
        }

        _restoring = true;

        try
        {
            foreach (nint handle in Win32Window.EnumerateTopLevel()) TryManage(handle);
        }
        finally
        {
            // Restoration applies only to the initial adoption pass. A window opened
            // later must land where the user is, not where an old session says.
            _restoring = false;
            _session = null;
            _claimedSessionEntries.Clear();
        }
    }

    // ---- rules -------------------------------------------------------------

    private static WindowAttributes ToAttributes(nint handle)
    {
        uint processId = Win32Window.GetProcessId(handle);
        string? path = Win32Window.GetProcessPath(processId);

        return new WindowAttributes(
            Win32Window.GetTitle(handle),
            Win32Window.GetClassName(handle),
            path is null ? string.Empty : Path.GetFileNameWithoutExtension(path),
            path);
    }

    private bool ShouldIgnore(WindowAttributes attributes)
    {
        foreach (WindowRule rule in _config.Rules)
        {
            if (rule.Trigger != RuleTrigger.OnManage) continue;
            if (!rule.Matches(attributes, _config.Apps)) continue;

            foreach (WmCommand command in rule.Commands)
                if (command is IgnoreCommand) return true;
        }

        return false;
    }

    private void ApplyRules(WindowNode window, WindowAttributes attributes, RuleTrigger trigger)
    {
        foreach (WindowRule rule in _config.Rules)
        {
            if (rule.Trigger != trigger) continue;
            if (!rule.Matches(attributes, _config.Apps)) continue;

            // Rules act on the window they matched, so focus is moved there first.
            // Otherwise `move --workspace 5` in a rule would move whatever the user
            // happened to be looking at.
            WindowNode? previous = _wm.FocusedWindow;

            Publish(_wm.FocusWindow(window));
            Execute(rule.Commands.Where(c => c is not IgnoreCommand));

            if (previous is not null && !ReferenceEquals(previous, window) && previous.Workspace is not null)
                Publish(_wm.FocusWindow(previous));
        }
    }

    // ---- commands ----------------------------------------------------------

    private void Execute(IEnumerable<WmCommand> commands)
    {
        foreach (CommandOutcome outcome in _executor.ExecuteAll(commands))
        {
            Publish(outcome.Result);
            PerformHostAction(outcome);

            if (outcome.Events.Count > 0) _layoutDirty = true;
        }
    }

    /// <summary>Runs one command, for IPC. Must be called on the daemon thread.</summary>
    internal CommandOutcome RunCommand(WmCommand command)
    {
        CommandOutcome outcome = _executor.Execute(command);

        Publish(outcome.Result);
        PerformHostAction(outcome);

        if (outcome.Events.Count > 0) _layoutDirty = true;

        return outcome;
    }

    /// <summary>
    /// Explains how Shubbak sees a window.
    /// </summary>
    /// <remarks>
    /// Reports every matchable attribute, whether the window is manageable and why
    /// not if it is not, and which configured rules match. This is the answer to
    /// "why is this window not being tiled?", which is otherwise diagnosed only by
    /// trial and error.
    /// </remarks>
    internal string Inspect(nint handle)
    {
        var report = new System.Text.StringBuilder();

        WindowAttributes attributes = ToAttributes(handle);
        ManageDecision decision = WindowFilter.Evaluate(handle);
        Rect bounds = Win32Window.GetBounds(handle);

        report.AppendLine($"handle       0x{handle:X}");
        report.AppendLine($"title        {attributes.Title}");
        report.AppendLine($"class        {attributes.ClassName}");
        report.AppendLine($"process      {attributes.ProcessName}");
        report.AppendLine($"path         {attributes.ProcessPath ?? "(unreadable - elevated process?)"}");
        report.AppendLine($"rect         {bounds}");
        report.AppendLine($"style        0x{Win32Window.GetStyleBits(handle):X8}");
        report.AppendLine($"ex-style     0x{Win32Window.GetExStyleBits(handle):X8}");
        report.AppendLine($"visible      {Win32Window.IsVisible(handle)}");
        report.AppendLine($"cloaked      {Win32Window.GetCloakState(handle)}");
        report.AppendLine($"minimised    {Win32Window.IsMinimised(handle)}");
        report.AppendLine();

        report.AppendLine($"manageable   {(decision.Manageable ? "yes" : "no")} - {decision.Explain()}");

        if (_managed.TryGetValue(handle, out WindowNode? window))
        {
            report.AppendLine($"managed      yes");
            report.AppendLine($"  node       #{window.Id}");
            report.AppendLine($"  state      {window.State}");
            report.AppendLine($"  workspace  {window.Workspace?.Name ?? "(none)"}");
            report.AppendLine($"  focused    {ReferenceEquals(window, _wm.FocusedWindow)}");
        }
        else
        {
            report.AppendLine($"managed      no{(_ignored.Contains(handle) ? " (excluded by a rule)" : "")}");
        }

        report.AppendLine();
        report.AppendLine("rules");

        if (_config.Rules.Count == 0)
        {
            report.AppendLine("  (none configured)");
        }
        else
        {
            foreach (WindowRule rule in _config.Rules)
            {
                bool matched = rule.Matches(attributes, _config.Apps);
                report.AppendLine($"  [{(matched ? "x" : " ")}] {rule.Name} (line {rule.Span.Start.Line})");
            }
        }

        // Listing apps separately is what turns "my rule does not fire" into a
        // one-glance diagnosis: the rule is fine, the app definition is what missed.
        if (_config.Apps.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("apps");

            foreach ((string name, AppDefinition app) in _config.Apps)
            {
                bool matched = app.Matches(attributes);
                report.AppendLine($"  [{(matched ? "x" : " ")}] {name}");

                if (!matched)
                {
                    foreach (WindowMatcher matcher in app.Matchers)
                    {
                        bool ok = matcher.Matches(attributes.Get(matcher.Target));
                        if (!ok) report.AppendLine($"        failed: {matcher}");
                    }
                }
            }
        }

        return report.ToString();
    }

    private void PerformHostAction(CommandOutcome outcome)
    {
        switch (outcome.Action)
        {
            case HostAction.CloseFocusedWindow:
                if (_wm.FocusedWindow is { } window) WindowActions.Close((nint)window.Handle);
                break;

            case HostAction.ShellExecute:
                if (outcome.Payload is { } commandLine) ShellExecute(commandLine);
                break;

            case HostAction.ReloadConfig:
                LoadConfig(_configPath, initial: false);
                _layoutDirty = true;
                break;

            case HostAction.Redraw:
                // Forget every cached rectangle so the next pass re-applies all of
                // them, even the ones already thought to be correct.
                foreach (nint handle in _managed.Keys) _committer.Forget(handle);
                _layoutDirty = true;
                break;

            case HostAction.Exit:
                Log.Info(LogCategory.Wm, "exit requested");
                Stop();
                break;

            case HostAction.None:
            default:
                break;
        }
    }

    /// <summary>Runs a command through the shell, detached.</summary>
    private static void ShellExecute(string commandLine)
    {
        try
        {
            (string file, string arguments) = SplitCommandLine(commandLine);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                CreateNoWindow = true,
            };

            process.Start();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Command, $"shell-exec failed: {commandLine}", ex);
        }
    }

    private static (string File, string Arguments) SplitCommandLine(string commandLine)
    {
        commandLine = commandLine.Trim();

        if (commandLine.StartsWith('"'))
        {
            int close = commandLine.IndexOf('"', 1);
            if (close > 0)
                return (commandLine[1..close], commandLine[(close + 1)..].Trim());
        }

        int space = commandLine.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? (commandLine, string.Empty)
            : (commandLine[..space], commandLine[(space + 1)..]);
    }

    // ---- layout ------------------------------------------------------------

    private void ApplyLayout()
    {
        IReadOnlyList<Placement> placements = _wm.ComputePlacements();

        _commitScratch.Clear();

        foreach (Placement placement in placements)
        {
            nint handle = (nint)placement.Window.Handle;

            // Hidden windows are never animated: moving something the user cannot
            // see is wasted work, and it would keep the animation engine busy for
            // every window on every inactive workspace.
            if (!placement.Visible)
            {
                _animation.Remove(placement.Window.Handle);
                _commitScratch.Add(placement);
                continue;
            }

            // Where the window is now: mid-flight position if it is already moving,
            // otherwise its real position on screen.
            Rect current = _animation.TryGetCurrent(placement.Window.Handle, out Rect inFlight)
                ? inFlight
                : Win32Window.GetBounds(handle);

            AnimationKind kind = current.IsEmpty ? AnimationKind.WindowOpen : AnimationKind.WindowMove;

            if (_animation.Retarget(placement.Window.Handle, current, placement.Rect, kind))
            {
                // Animated: the tick loop will drive it. Visibility still has to be
                // applied now, which Commit does for the placements it is given.
                continue;
            }

            _commitScratch.Add(placement);
        }

        if (_commitScratch.Count > 0)
        {
            int moved = _committer.Commit(_commitScratch, static p => (nint)p.Window.Handle);

            if (moved > 0 && Log.IsEnabled(LogLevel.Debug))
                Log.Debug(LogCategory.Layout,
                    $"placed {moved}/{placements.Count} windows, {_animation.ActiveCount} animating");
        }

        // Focus is applied after geometry: focusing a window that is about to move
        // makes it flash at its old position first.
        if (_wm.FocusedWindow is { } focused &&
            Win32Window.GetForeground() != (nint)focused.Handle)
        {
            WindowActions.Focus((nint)focused.Handle);
        }
    }

    /// <summary>
    /// Periodically writes the session, so a crash or a power cut does not lose the
    /// arrangement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Time-based rather than change-based. Saving on every change would write the
    /// file dozens of times while dragging a window, and the arrangement genuinely
    /// worth preserving is the settled one, not every intermediate state.
    /// </para>
    /// <para>
    /// Skipped while windows are animating, so a save never lands mid-transition and
    /// records geometry that was never really the layout.
    /// </para>
    /// </remarks>
    private void MaybeSaveSession(long now)
    {
        if (_animation.IsAnimating) return;

        const double IntervalMs = 30_000;

        if (_lastSessionSaveTicks != 0 &&
            (now - _lastSessionSaveTicks) * 1000.0 / Stopwatch.Frequency < IntervalMs)
        {
            return;
        }

        _lastSessionSaveTicks = now;

        if (_managed.Count > 0) SessionStore.Save(_wm.Root, _sessionPath);
    }

    /// <summary>Drives one frame of in-flight window motion.</summary>
    private void AdvanceAnimation(double deltaMs)
    {
        if (_frameScratch.Length < _animation.ActiveCount)
            _frameScratch = new AnimationFrame[Math.Max(_animation.ActiveCount * 2, 128)];

        int count = _animation.Tick(deltaMs, _frameScratch);
        if (count == 0) return;

        // One atomic transaction per frame, exactly as in the S2 spike: the
        // unbatched alternative dropped 33-42% of frames at 144 Hz.
        _committer.CommitFrame(_frameScratch.AsSpan(0, count));
    }

    // ---- monitors ----------------------------------------------------------

    private void SyncMonitors()
    {
        IReadOnlyList<MonitorInfo> current = MonitorSource.Enumerate();

        foreach (MonitorInfo info in current)
        {
            MonitorNode? existing = _wm.Root.FindMonitor(info.DeviceId);

            if (existing is null)
            {
                var monitor = new MonitorNode(info.DeviceId, info.Bounds, info.WorkArea, info.Dpi)
                {
                    IsPrimary = info.IsPrimary,
                };

                Publish(_wm.AddMonitor(monitor));
            }
            else
            {
                Publish(_wm.UpdateMonitor(existing, info.Bounds, info.WorkArea, info.Dpi));
            }
        }

        // Monitors that vanished have their workspaces migrated, never discarded:
        // undocking must not strand every window on a display that came back.
        foreach (MonitorNode monitor in _wm.Root.Monitors.ToArray())
        {
            if (!current.Any(m => string.Equals(m.DeviceId, monitor.DeviceId, StringComparison.OrdinalIgnoreCase)))
                Publish(_wm.RemoveMonitor(monitor));
        }

        CreateConfiguredWorkspaces();
    }

    private void CreateConfiguredWorkspaces()
    {
        foreach (WorkspaceConfig declared in _config.Workspaces)
        {
            if (_wm.Root.FindWorkspace(declared.Name) is not null) continue;

            ILayout? layout = declared.Layout is not null &&
                              LayoutRegistry.TryResolve(declared.Layout, out ILayout resolved)
                ? resolved
                : null;

            var workspace = new WorkspaceNode(declared.Name, layout)
            {
                DisplayName = declared.DisplayName,
                PreferredMonitorIndex = declared.BindToMonitor,
                IsTransient = false,
            };

            _wm.AddWorkspace(workspace);
        }
    }

    // ---- config ------------------------------------------------------------

    private void LoadConfig(string? path, bool initial)
    {
        if (path is null)
        {
            Log.Warn(LogCategory.Config, "no config file found; using defaults");
            _bindings.Load(_config);
            return;
        }

        ConfigLoadResult result = ConfigLoader.LoadFile(path);
        string source = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        foreach (Diagnostic diagnostic in result.Diagnostics)
            Console.Error.Write(diagnostic.Render(source, path));

        if (result.HasErrors && !initial)
        {
            // Keeping the previous config is the safe failure mode: a typo must not
            // leave a running desktop with no keybindings.
            Log.Error(LogCategory.Config, "config has errors; keeping the previously loaded configuration");
            return;
        }

        _config = result.Config;
        _wm.Options = _config.ToWmOptions();
        _animation.Options = _config.Animation;
        _committer.UseCloaking = _config.UseCloaking;
        _bindings.Load(_config);

        ApplyLoggingConfig(initial);

        if (!initial)
        {
            CreateConfiguredWorkspaces();
            Log.Info(LogCategory.Config, $"reloaded: {_config.Keybindings.Count} keybindings, {_config.Rules.Count} rules");
        }
    }

    /// <summary>
    /// Applies the config's logging settings.
    /// </summary>
    /// <remarks>
    /// Command line flags win. Someone who launched with <c>--log-level trace</c> is
    /// mid-investigation, and having a config reload silently drop them back to
    /// <c>info</c> would throw away exactly the detail they were collecting.
    /// </remarks>
    private void ApplyLoggingConfig(bool initial)
    {
        if (_logLevelFromCommandLine) return;

        if (Log.Level != _config.LogLevel)
        {
            Log.Level = _config.LogLevel;
            Log.Info(LogCategory.Config, $"log level set to {_config.LogLevel} by config");
        }

        if (initial && _config.LogFile is { Length: > 0 } file && Log.FilePath is null)
        {
            try
            {
                Log.OpenFile(file);
                Log.Info(LogCategory.Config, $"logging to {Log.FilePath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(LogCategory.Config, $"could not open log file '{file}'", ex);
            }
        }
    }

    private void RunStartupCommands()
    {
        foreach (string command in _config.StartupCommands)
        {
            const string Prefix = "shell-exec ";

            if (command.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                ShellExecute(command[Prefix.Length..]);
            else
                ShellExecute(command);
        }
    }

    // ---- plumbing ----------------------------------------------------------

    private void Publish(WmResult result)
    {
        if (result.Events.Count == 0) return;

        foreach (WmEvent wmEvent in result.Events)
        {
            // Binding mode lives in two places: the state machine, which reports it,
            // and the lookup table, which enforces it.
            if (wmEvent is BindingModeChanged mode) _bindings.SetMode(mode.Mode);
            if (wmEvent is CommandRejected rejected)
                Log.Debug(LogCategory.Command, $"rejected {rejected.Command}: {rejected.Reason}");

            _ipc?.Publish(wmEvent.Topic, StateProjection.Payload(wmEvent, _wm));
        }

        _layoutDirty = true;
        EventsProduced?.Invoke(result.Events);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Belt and braces: Run() restores on its way out, but Dispose is also
        // reached after an exception or a Ctrl+C that unwound differently. Restoring
        // twice is harmless; restoring never is not.
        try
        {
            RestoreConcealedWindows();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Window, "could not restore concealed windows", ex);
        }

        _keyboard?.Dispose();
        _winEvents?.Dispose();
        if (_ipc is not null) _ipc.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
