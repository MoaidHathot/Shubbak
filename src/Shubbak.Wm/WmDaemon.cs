using System.Diagnostics;
using Shubbak.Config;
using Shubbak.Core.Animation;
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

    public WmDaemon()
    {
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

        Log($"Shubbak started. {_managed.Count} windows adopted, " +
            $"{_wm.Root.Monitors.Count} monitors, {_config.Keybindings.Count} keybindings.");

        _loop.Run(TimeSpan.FromMilliseconds(8));
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
        }
        catch (Exception ex)
        {
            // A daemon that dies leaves every managed window stranded, so the tick
            // never propagates. Anything unexpected is logged and the loop carries on.
            Log($"error in tick: {ex}");
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
            if (binding is null) continue;

            Execute(binding.Commands);
        }
    }

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
                Log($"error handling IPC request: {ex.Message}");
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
        if (!decision.Manageable) return;

        var attributes = ToAttributes(handle);

        if (ShouldIgnore(attributes))
        {
            // Remembered, so the same window is not re-evaluated on every one of the
            // many events it will generate over its lifetime.
            _ignored.Add(handle);
            return;
        }

        var window = new WindowNode(handle, Win32Window.BuildIdentity(handle))
        {
            State = WindowFilter.InitialStateFor(handle, _config.InitialWindowState),
        };

        WorkspaceNode? workspace = WorkspaceFor(handle);

        _managed[handle] = window;
        Publish(_wm.ManageWindow(window, workspace));

        ApplyRules(window, attributes, RuleTrigger.OnManage);

        _layoutDirty = true;
    }

    private void TryUnmanage(nint handle)
    {
        _ignored.Remove(handle);

        if (!_managed.Remove(handle, out WindowNode? window)) return;

        _committer.Forget(handle);
        Publish(_wm.UnmanageWindow(window));

        _layoutDirty = true;
    }

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

    /// <summary>Brings windows that already exist under management at startup.</summary>
    private void AdoptExistingWindows()
    {
        foreach (nint handle in Win32Window.EnumerateTopLevel())
            TryManage(handle);
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
        report.AppendLine($"cloaked      {Win32Window.IsCloaked(handle)}");
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
                Log("exit requested");
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
            Log($"shell-exec failed: {commandLine}: {ex.Message}");
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
            _committer.Commit(_commitScratch, static p => (nint)p.Window.Handle);

        // Focus is applied after geometry: focusing a window that is about to move
        // makes it flash at its old position first.
        if (_wm.FocusedWindow is { } focused &&
            Win32Window.GetForeground() != (nint)focused.Handle)
        {
            WindowActions.Focus((nint)focused.Handle);
        }
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
            Log("no config file; using defaults");
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
            Log("config has errors; keeping the previously loaded configuration");
            return;
        }

        _config = result.Config;
        _wm.Options = _config.ToWmOptions();
        _animation.Options = _config.Animation;
        _bindings.Load(_config);

        if (!initial)
        {
            CreateConfiguredWorkspaces();
            Log($"config reloaded: {_config.Keybindings.Count} keybindings, {_config.Rules.Count} rules");
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
            if (wmEvent is CommandRejected rejected) Log($"{rejected.Command}: {rejected.Reason}");

            _ipc?.Publish(wmEvent.Topic, StateProjection.Payload(wmEvent, _wm));
        }

        _layoutDirty = true;
        EventsProduced?.Invoke(result.Events);
    }

    private static void Log(string message) =>
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _keyboard?.Dispose();
        _winEvents?.Dispose();
        if (_ipc is not null) _ipc.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
