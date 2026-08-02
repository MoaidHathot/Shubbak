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

    /// <summary>Window geometry captured when a mouse drag started.</summary>
    private readonly Dictionary<nint, Rect> _dragOrigin = [];

    /// <summary>The window currently wearing the focused border.</summary>
    private WindowNode? _borderedWindow;
    private long _lastBorderRefreshTicks;

    private WinEventSource? _winEvents;
    private KeyboardSource? _keyboard;

    private ShubbakConfig _config = ShubbakConfig.Default;
    private string? _configPath;

    private bool _layoutDirty;
    private bool _disposed;

    private Session? _session;
    private readonly HashSet<int> _claimedSessionEntries = [];
    private bool _restoring;
    private int _revived;
    private int _revivalBudget;

    private long _lastSessionSaveTicks;
    private long _lastMonitorSyncTicks;

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

        // Log writing is buffered onto its own thread, so the last lines - the ones
        // that say why we are stopping - are still in the queue at this point.
        Log.Flush();
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

            MaybeSyncMonitors(now);
            MaybeRefreshFocusBorder(now);
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

    /// <summary>
    /// Decides whether a hidden window has actually gone away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applications that close to the system tray hide their window and keep running,
    /// so it is still a valid handle. Keeping it managed means the next layout reveals
    /// it, the window reappears, and the close button looks broken - which is exactly
    /// what WhatsApp does.
    /// </para>
    /// <para>
    /// The trap is that <c>EVENT_OBJECT_HIDE</c> also arrives when a window is
    /// minimised or destroyed, and komorebi's handler carries a comment saying so.
    /// Unmanaging on every hide would therefore drop a window the moment the user
    /// minimised it. Minimising is checked first for that reason: it has its own event
    /// and its own state, and this must not usurp either.
    /// </para>
    /// <para>
    /// komorebi is more cautious still - it unmanages only for applications on a
    /// curated tray list. That is more accurate and costs the user a config entry per
    /// application. Treating any foreign hide as a departure is the opposite trade:
    /// nothing to configure, and a window that hides itself transiently is re-managed
    /// on the show event that follows.
    /// </para>
    /// </remarks>
    private void HandleWindowHidden(nint handle)
    {
        // Ours. The workspace it lives on was switched away from.
        if (_committer.IsConcealing(handle)) return;

        // Gone for good.
        if (!Win32Window.Exists(handle))
        {
            TryUnmanage(handle);
            return;
        }

        // Minimised, not dismissed. Windows sends this alongside the minimise event,
        // and a minimised window is still the user's - it keeps its place in the tree
        // and its slot in the layout.
        if (Win32Window.IsMinimised(handle)) return;

        TryUnmanage(handle);
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
                TryUnmanage(handle);
                break;

            case WinEventKind.Cloaked:
                // Shubbak conceals inactive workspaces by cloaking, and the shell
                // reports that straight back as an ordinary cloak event. Unmanaging on
                // it would drop every window on the workspace just left, so switching
                // back would find nothing to reveal and the windows would be stranded.
                //
                // The Hidden case below has carried the equivalent guard for a long
                // time. This one did not need it while cloaking silently failed for
                // every window Shubbak managed; now that it works, it does.
                if (!_committer.IsConcealing(handle)) TryUnmanage(handle);
                break;

            case WinEventKind.Hidden:
                HandleWindowHidden(handle);
                break;

            case WinEventKind.TitleChanged:
                if (_managed.TryGetValue(handle, out WindowNode? titled))
                {
                    Publish(_wm.UpdateTitle(titled, Win32Window.GetTitle(handle)));
                    break;
                }

                // Not managed yet - so this may be the moment it becomes eligible.
                // Store apps and other late-loading windows are created before they
                // have a title, and an untitled top-level window is rejected: they
                // are overwhelmingly splash screens and invisible helpers.
                //
                // Without this, such a window is judged once, at its least ready, and
                // never looked at again. Settings opened with Win+I stayed unmanaged
                // until it was closed and reopened - by which time the window already
                // existed and passed on the first try, which is what made it look
                // like an intermittent fault rather than a race.
                //
                // Minimised windows are excluded, which is komorebi's lesson rather
                // than ours: they hit a case where Firefox renamed a minimised window
                // as YouTube autoplayed, and treating that as an arrival pulled the
                // window back onto the screen.
                if (!Win32Window.IsMinimised(handle)) TryManage(handle);
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

            case WinEventKind.MoveSizeStart:
                // The starting geometry is what distinguishes a move from a resize,
                // and a real drag from a click on a title bar.
                if (_managed.ContainsKey(handle)) _dragOrigin[handle] = Win32Window.GetBounds(handle);
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
    /// <para>
    /// Three outcomes, decided by comparing the geometry against what it was when the
    /// drag started:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Floating</b> - the new geometry is simply adopted.</item>
    ///   <item><b>Resized</b> - the new size is converted back into the tree's size
    ///   ratios, so dragging a border behaves as it does in any tiling manager.</item>
    ///   <item><b>Moved</b> - resolved against the tree: dropping on the middle of
    ///   another window swaps them, dropping near an edge inserts beside it.</item>
    /// </list>
    /// <para>
    /// A drag that resolves to nothing simply relayouts, putting the window back -
    /// which is honest, because the alternative is guessing.
    /// </para>
    /// </remarks>
    private void HandleUserMove(nint handle)
    {
        if (!_managed.TryGetValue(handle, out WindowNode? window)) return;

        Rect before = _dragOrigin.TryGetValue(handle, out Rect recorded) ? recorded : window.Rect;
        _dragOrigin.Remove(handle);

        Rect after = Win32Window.GetBounds(handle);

        if (window.State == WindowState.Floating)
        {
            window.FloatingRect = after;
            _committer.Forget(handle);
            return;
        }

        if (!window.IsTiled)
        {
            _layoutDirty = true;
            return;
        }

        if (DragResolver.IsResize(before, after))
        {
            Publish(_wm.ResizeFromDrag(window, after));
            _layoutDirty = true;
            return;
        }

        if (!DragResolver.IsMove(before, after))
        {
            // A click on a title bar, not a drag. Nothing to do beyond letting the
            // next layout pass put the window back where it belongs.
            _layoutDirty = true;
            return;
        }

        // The cursor, not the window's corner: the user grabbed the title bar at an
        // arbitrary offset, so the window's top-left may be far from where they are
        // pointing.
        if (Win32Window.GetCursorPosition() is not { } cursor)
        {
            _layoutDirty = true;
            return;
        }

        WmResult result = _wm.DropWindow(window, cursor.X, cursor.Y);
        Publish(result);

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.Debug(LogCategory.Window,
                result.Succeeded
                    ? $"dropped \"{Truncate(window.Identity.Title, 32)}\" at {cursor.X},{cursor.Y}"
                    : $"drop rejected: {result.RejectionReason}");
        }

        _layoutDirty = true;
    }

    // ---- window lifecycle --------------------------------------------------

    private void TryManage(nint handle)
    {
        if (_managed.ContainsKey(handle) || _ignored.Contains(handle)) return;

        // Concealed windows are considered only during the initial adoption pass, and
        // even then only to be reconciled against the session below.
        ManageDecision decision = WindowFilter.Evaluate(handle, concealedAreEligible: _restoring);

        if (!decision.Manageable)
        {
            // At trace level this is the answer to "why is that window floating?",
            // recorded as it happens rather than reconstructed afterwards. The class
            // is included because it is what a rule has to match on, and transient
            // windows - shell flyouts especially - are gone long before anything can
            // be pointed at them to ask.
            if (Log.IsEnabled(LogLevel.Trace))
                Log.Trace(LogCategory.Window,
                    $"skip 0x{handle:X} \"{Truncate(Win32Window.GetTitle(handle), 40)}\" " +
                    $"[{Win32Window.GetClassName(handle)}]: {decision.Explain()}");

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

        // A window that starts floating has no remembered position, and the layout
        // engine treats a floating window's rectangle as the user's to keep - so
        // without this it would be "kept" at the origin with no size, and the window
        // would be flung into the corner the instant it appeared.
        //
        // Dialogs are the case that matters: Win+R and Save boxes size themselves to
        // their content, which is precisely why they are not tiled.
        if (window.State == WindowState.Floating)
        {
            Rect bounds = Win32Window.GetBounds(handle);
            if (!bounds.IsEmpty) window.FloatingRect = bounds;
        }

        // A saved session wins during the initial adoption pass, so a restart puts
        // windows back where they were rather than piling them onto whichever
        // workspace happens to be active.
        WorkspaceNode? remembered = _restoring ? RestoredWorkspaceFor(window) : null;

        // A window still concealed at this point was concealed by whoever ran last -
        // us, before a crash or a kill. Revive it only when the session names it.
        // Without that evidence it belongs to the application that hid it: a tray
        // host, a message-only helper, a media-key listener. A desktop carries dozens.
        //
        // Tested against the session match, deliberately, and not against the resolved
        // workspace. An earlier version checked the latter, which falls back to a real
        // workspace and so is never null - the guard never fired and startup revealed
        // eighty-four background windows on an ordinary desktop.
        if (_restoring && WindowCommitter.IsConcealed(handle))
        {
            if (remembered is null)
            {
                // Remembered as ignored so the events these windows emit do not bring
                // us back here for a second look.
                _ignored.Add(handle);

                if (Log.IsEnabled(LogLevel.Debug))
                    Log.Debug(LogCategory.Window,
                        $"leaving concealed 0x{handle:X} \"{Truncate(window.Identity.Title, 40)}\" " +
                        "alone: no session entry claims it");

                return;
            }

            // A budget, not because the check above is expected to fail, but because
            // it already did once. The session cannot justify reviving more windows
            // than it remembers, so exceeding that is proof of a logic error and the
            // damage is visible to the user immediately. Refusing costs a window that
            // stays concealed; not refusing carpets the desktop.
            if (_revived >= _revivalBudget)
            {
                Log.Error(LogCategory.Window,
                    $"refusing to revive 0x{handle:X}: already revived {_revived} window(s) " +
                    $"for a session of {_revivalBudget}. This is a bug - please report it.");

                _ignored.Add(handle);
                return;
            }

            WindowCommitter.Revive(handle);
            _revived++;

            Log.Info(LogCategory.Window,
                $"recovered concealed 0x{handle:X} \"{Truncate(window.Identity.Title, 40)}\" " +
                $"-> workspace {remembered.Name}");
        }

        WorkspaceNode? workspace = remembered ?? WorkspaceFor(handle);

        _managed[handle] = window;
        Publish(_wm.ManageWindow(window, workspace));

        Log.Info(LogCategory.Window,
            $"managed 0x{handle:X} \"{Truncate(attributes.Title, 40)}\" " +
            $"({attributes.ProcessName}) [{attributes.ClassName}] " +
            $"-> workspace {window.Workspace?.Name ?? "?"}");

        ApplyRules(window, attributes, RuleTrigger.OnManage);

        _layoutDirty = true;
    }

    private void TryUnmanage(nint handle)
    {
        _ignored.Remove(handle);

        if (!_managed.Remove(handle, out WindowNode? window)) return;

        if (ReferenceEquals(_borderedWindow, window)) _borderedWindow = null;

        _committer.Forget(handle);
        _animation.Remove(window.Handle);
        _dragOrigin.Remove(handle);
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
        _revived = 0;
        _revivalBudget = _session?.Windows.Count ?? 0;

        try
        {
            foreach (nint handle in Win32Window.EnumerateTopLevel()) TryManage(handle);
        }
        finally
        {
            // Always reported, so a recovery that runs away is visible in the log
            // without anyone having to think to look for it.
            if (_revived > 0 || _revivalBudget > 0)
            {
                Log.Info(LogCategory.Wm,
                    $"startup recovery: revived {_revived} of {_revivalBudget} remembered window(s)");
            }

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

            // Visibility is applied here, separately from geometry, because an
            // animated window never reaches Commit - the animation engine drives it
            // frame by frame instead. Leaving the reveal to Commit meant a window
            // whose position changed was animated into place while still concealed,
            // so a workspace that had been switched away from came back empty.
            _committer.Reveal(handle);

            // Where the window is now: mid-flight position if it is already moving,
            // otherwise its real position on screen.
            Rect current = _animation.TryGetCurrent(placement.Window.Handle, out Rect inFlight)
                ? inFlight
                : Win32Window.GetBounds(handle);

            AnimationKind kind = current.IsEmpty ? AnimationKind.WindowOpen : AnimationKind.WindowMove;

            if (_animation.Retarget(placement.Window.Handle, current, placement.Rect, kind))
            {
                // Animated: the tick loop drives the geometry from here.
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

        ApplyFocusBorder();
    }

    /// <summary>
    /// Periodically re-reads the monitor layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The work area is not static. Taj reserves its strip through the shell's appbar
    /// API, and if the bar starts after the window manager - which it does, since the
    /// window manager launches it - the work area shrinks <i>after</i> the initial
    /// read. Without this, windows are tiled underneath the bar until something else
    /// happens to trigger a relayout.
    /// </para>
    /// <para>
    /// The same path covers everything else that moves the work area: the taskbar
    /// being resized or set to auto-hide, a resolution change, DPI changes, and
    /// monitors being plugged or unplugged.
    /// </para>
    /// <para>
    /// Polled rather than driven by <c>WM_SETTINGCHANGE</c> because the daemon has no
    /// window to receive it, and enumerating a handful of monitors twice a second is
    /// far cheaper than creating and pumping one just for this.
    /// </para>
    /// </remarks>
    private void MaybeSyncMonitors(long now)
    {
        const double IntervalMs = 2_000;

        if (_lastMonitorSyncTicks != 0 &&
            (now - _lastMonitorSyncTicks) * 1000.0 / Stopwatch.Frequency < IntervalMs)
        {
            return;
        }

        _lastMonitorSyncTicks = now;

        if (MonitorLayoutChanged()) SyncMonitors();
    }

    /// <summary>
    /// Whether the monitor layout differs from what the tree records.
    /// </summary>
    /// <remarks>
    /// Checked before syncing because <see cref="SyncMonitors"/> publishes events and
    /// marks the layout dirty, and doing that twice a second regardless would keep
    /// the window manager permanently busy on an idle desktop.
    /// </remarks>
    private bool MonitorLayoutChanged()
    {
        IReadOnlyList<MonitorInfo> current = MonitorSource.Enumerate();

        if (current.Count != _wm.Root.Monitors.Count) return true;

        foreach (MonitorInfo info in current)
        {
            MonitorNode? existing = _wm.Root.FindMonitor(info.DeviceId);

            if (existing is null ||
                existing.Bounds != info.Bounds ||
                existing.WorkArea != info.WorkArea ||
                existing.Dpi != info.Dpi)
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// Marks the focused window with a coloured border.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the two windows whose state changed are touched, not every managed
    /// window. Re-applying a border that is already correct is a compositor round
    /// trip per window per focus change, and with a dozen windows open that is
    /// noticeable.
    /// </para>
    /// <para>
    /// When no unfocused colour is configured, unfocused windows have their border
    /// cleared rather than being given one - a border on everything is visual noise
    /// and defeats the purpose of marking the focused window at all.
    /// </para>
    /// </remarks>
    private void ApplyFocusBorder()
    {
        if (!_config.Effects.Enabled) return;

        WindowNode? focused = _wm.FocusedWindow;
        if (ReferenceEquals(focused, _borderedWindow)) return;

        if (_borderedWindow is { } previous && Win32Window.Exists((nint)previous.Handle))
            ApplyBorder(previous, _config.Effects.UnfocusedColour);

        if (focused is not null) ApplyBorder(focused, _config.Effects.FocusedColour);

        _borderedWindow = focused;
    }

    /// <summary>
    /// Reasserts the focused window's border.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DWMWA_BORDER_COLOR</c> is a property of the window, not of Shubbak, and
    /// applications are free to set it themselves. Ones that manage their own frame -
    /// Windows Terminal and other WinUI applications especially - reset it as part of
    /// ordinary repainting, which silently clears ours.
    /// </para>
    /// <para>
    /// Setting it once when focus changes is therefore not enough: the border would
    /// vanish partway through using a window and not return until focus moved away
    /// and back. Reasserting on a slow timer costs one DWM call a second and makes
    /// the border heal itself no matter what the application does.
    /// </para>
    /// </remarks>
    private void MaybeRefreshFocusBorder(long now)
    {
        const double IntervalMs = 1_000;

        if (!_config.Effects.Enabled) return;

        if (_lastBorderRefreshTicks != 0 &&
            (now - _lastBorderRefreshTicks) * 1000.0 / Stopwatch.Frequency < IntervalMs)
        {
            return;
        }

        _lastBorderRefreshTicks = now;

        if (_borderedWindow is not { } window) return;
        if (!Win32Window.Exists((nint)window.Handle)) return;

        ApplyBorder(window, _config.Effects.FocusedColour);
    }

    private static void ApplyBorder(WindowNode window, string? colour)
    {
        nint handle = (nint)window.Handle;

        if (colour is null || !TryParseColour(colour, out byte r, out byte g, out byte b))
        {
            WindowActions.ClearBorderColour(handle);
            return;
        }

        WindowActions.SetBorderColour(handle, r, g, b);
    }

    /// <summary>Parses <c>#RGB</c> or <c>#RRGGBB</c>.</summary>
    private static bool TryParseColour(string text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#') span = span[1..];

        switch (span.Length)
        {
            case 3:
                if (!Nibble(span[0], out int sr) || !Nibble(span[1], out int sg) || !Nibble(span[2], out int sb))
                    return false;

                // #abc means #aabbcc.
                r = (byte)(sr * 17);
                g = (byte)(sg * 17);
                b = (byte)(sb * 17);
                return true;

            case 6 or 8:
                return Byte(span[0], span[1], out r)
                    && Byte(span[2], span[3], out g)
                    && Byte(span[4], span[5], out b);

            default:
                return false;
        }

        static bool Nibble(char c, out int value)
        {
            value = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };

            return value >= 0;
        }

        static bool Byte(char high, char low, out byte value)
        {
            value = 0;
            if (!Nibble(high, out int h) || !Nibble(low, out int l)) return false;

            value = (byte)((h << 4) | l);
            return true;
        }
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

        // The work area may have shrunk - a bar appearing, the taskbar moving - so
        // everything has to be re-placed against the new bounds.
        _layoutDirty = true;
    }

    private void CreateConfiguredWorkspaces()
    {
        for (int index = 0; index < _config.Workspaces.Count; index++)
        {
            WorkspaceConfig declared = _config.Workspaces[index];

            ILayout? layout = declared.Layout is not null &&
                              LayoutRegistry.TryResolve(declared.Layout, out ILayout resolved)
                ? resolved
                : null;

            // Already here, so its settings are brought up to date rather than the
            // whole declaration being skipped. Reloading otherwise appeared to do
            // nothing for the settings people most often change - a workspace's
            // display name, which monitor it prefers, where it sits in the bar -
            // because the workspace it applied to already existed by name.
            if (_wm.Root.FindWorkspace(declared.Name) is { } existing)
            {
                existing.DisplayName = declared.DisplayName;
                existing.PreferredMonitorIndex = declared.BindToMonitor;
                existing.SortIndex = index;
                existing.IsTransient = false;

                // Only when the config names one: a workspace whose layout the user
                // has since changed by keystroke should keep it.
                if (layout is not null) existing.Layout = layout;

                continue;
            }

            var workspace = new WorkspaceNode(declared.Name, layout)
            {
                DisplayName = declared.DisplayName,
                PreferredMonitorIndex = declared.BindToMonitor,
                IsTransient = false,

                // Declaration order, so the bar shows workspaces in the order the
                // user thinks in rather than the order they happened to be created.
                SortIndex = index,
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
        _committer.HideMethod = _config.HideMethod;
        _committer.KeepInTaskbar = _config.KeepInTaskbar;
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
