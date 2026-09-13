using System.Runtime.InteropServices;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Shubbak.Ipc;
using Shubbak.Native;
using Taj.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Taj;

/// <summary>The Taj bar.</summary>
internal static class Program
{
    /// <summary>
    /// Everything that belongs to one bar, kept together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These were five parallel lists indexed by monitor position, and the position
    /// was captured into each connection's event handlers at creation. Two things
    /// went wrong with that. A bar whose window failed to create was skipped but its
    /// index was not, so every bar after it indexed the lists one slot off. And a
    /// monitor being unplugged compacts the window manager's list, so every bar after
    /// it filtered on the wrong display - permanently, with nothing to say so.
    /// </para>
    /// <para>
    /// A bar is now identified by the GDI device name of its display, which is what
    /// the window manager's own state uses, and its handlers capture <i>this object</i>
    /// rather than a position. The position is still needed for a rule written
    /// <c>monitor=1</c>; it is read off each snapshot and kept here.
    /// </para>
    /// </remarks>
    private sealed class Bar
    {
        public required string DeviceId { get; init; }
        public required BarWindow Window { get; init; }
        public required BarModel Model { get; init; }
        public required WmConnection Connection { get; init; }

        /// <summary>Per-bar state a reload has to rebuild.</summary>
        public required BarProfileSelector Selector { get; set; }

        /// <summary>The workspace this bar last reported, so a reload can re-pick its profile.</summary>
        public string Workspace { get; set; } = string.Empty;

        /// <summary>Where its display sits in the window manager's list, as last reported.</summary>
        public int MonitorIndex { get; set; } = -1;

        /// <summary>What the window manager's configuration calls its display, as last reported.</summary>
        public IReadOnlyList<string> MonitorNames { get; set; } = [];

        /// <summary>The contexts the window manager holds, as last reported.</summary>
        public IReadOnlyList<string> Contexts { get; set; } = [];
    }

    private static readonly List<Bar> s_bars = [];

    /// <summary>The configuration in force, for bars created after startup.</summary>
    private static TajConfig s_config = TajConfigLoader.CreateDefault();

    /// <summary>What was wrong with it, for the <c>config</c> indicator on a new bar.</summary>
    private static DiagnosticCounts s_problems;

    /// <summary>Arguments, kept so the config can be found again on a reload.</summary>
    private static string[] s_args = [];

    /// <summary>
    /// Set from the connection thread when the window manager reports a reload.
    /// </summary>
    /// <remarks>
    /// A flag rather than the work itself. The event arrives on the IPC task, and
    /// rebuilding touches windows and GDI objects owned by the thread running the
    /// message loop, so the loop picks it up on its next pass.
    /// </remarks>
    private static volatile bool s_reloadRequested;

    /// <summary>When the configuration was last re-read, so reports of one event coalesce.</summary>
    private static long s_lastReloadTicks;

    /// <summary>How close together two reload requests have to be to count as one.</summary>
    private static readonly TimeSpan ReloadCoalesceWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The displays as the window manager last described them, when that differs from
    /// what the bars were built for. Null when nothing is pending.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="s_reloadRequested"/> and for the same reason: set on
    /// a connection's pump thread, consumed by the loop, which creates and destroys bar
    /// windows in response. Several connections report the same change; the last one
    /// wins and the response is idempotent, so that is harmless.
    /// </remarks>
    private static volatile IReadOnlyList<MonitorInfoDto>? s_pendingMonitors;

    private static volatile bool s_running = true;

    /// <summary>
    /// Whether the window manager has released its hooks.
    /// </summary>
    /// <remarks>
    /// Written from a connection's pump thread and read by the message loop, so
    /// volatile for the same reason <see cref="s_reloadRequested"/> is.
    /// </remarks>
    private static volatile bool s_wmSuspended;

    /// <summary>
    /// Whether the shell has said a full-screen application is up.
    /// </summary>
    /// <remarks>
    /// A hint rather than the truth, in two ways. <c>ABN_FULLSCREENAPP</c> reports an
    /// opening and a closing, not what is in front right now, so this starts a
    /// stand-down and <c>StandDown.StillCovered</c> is what keeps it going. And it
    /// names no monitor, which is why <c>StandDown.ShouldStandDown</c> is told how
    /// many bars there are.
    /// </remarks>
    private static volatile bool s_fullScreenApp;

    /// <summary>Whether the bar is currently stood down.</summary>
    private static bool s_stoodDown;

    private static int Main(string[] args)
    {
        // Taj is a GUI-subsystem binary, so it starts with no console and every write
        // to one is discarded. Both of these printed nothing at all before ConsoleHost
        // was here to ask for one.
        if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
        {
            ConsoleHost.Ensure();
            PrintUsage();
            return 0;
        }

        if (Array.Exists(args, a => a is "--version" or "-v" or "version"))
        {
            ConsoleHost.Ensure();
            Console.WriteLine(ShubbakVersion.Banner);
            return 0;
        }

        ConfigureLogging(args);
        s_args = args;

        // One bar per account, for the same reason there is one window manager.
        //
        // Two of these is not merely untidy. Each bar reserves its strip of screen
        // through the shell's appbar API, so a second set takes the work area a second
        // time and every tiled window is laid out into a desktop shorter than it
        // should be - which reads as a gaps setting gone wrong rather than as two bars.
        // They then draw on top of each other, and `shubbak taj-exit` closes all of
        // them at once because it goes by window class.
        //
        // The pairing happens without anybody doing anything strange: a bar survives
        // the window manager restarting - that is deliberate, it reconnects inside
        // window-manager-timeout - and the restarted window manager then runs its
        // startup commands, one of which starts a bar.
        using SingleInstanceLock instance = SingleInstanceLock.Claim(
            IpcProtocol.InstanceMutexNameFor("taj"));

        // An uncertain answer starts anyway, which is the opposite of what the window
        // manager does with the same uncertainty. Two bars are visibly wrong and easily
        // undone; no bar at all, because a mutex could not be opened, is a worse
        // outcome than the thing being guarded against.
        if (!instance.Held && instance.Certain)
        {
            ConsoleHost.Ensure();
            Console.Error.WriteLine("taj: a bar is already running.");
            Console.Error.WriteLine("hint: `shubbak taj-exit` stops it.");

            Log.Info(LogCategory.Wm, "another bar is already running; leaving it to it");
            return 1;
        }

        // Before any window is created: without it Windows reports virtualised
        // coordinates on scaled displays and the bar lands in the wrong place.
        PInvoke.SetProcessDpiAwarenessContext((DPI_AWARENESS_CONTEXT)(nint)(-4));

        // Before the config is read, so a colour written `accent` is this machine's.
        // Re-read on every reload, which is how the bar follows a change of accent -
        // see BarWindow.SystemColoursChanged.
        SystemColours.Adopt();

        try
        {
            (TajConfig config, _) = LoadConfig(args, out DiagnosticCounts problems);

            // Kept, because bars are created after startup too - a monitor plugged in
            // later gets one - and each new bar is built from whatever is in force.
            s_config = config;
            s_problems = problems;

            if (!CreateBars())
            {
                Log.Error(LogCategory.Wm, "no bars could be created");
                return 1;
            }

            Log.Info(LogCategory.Wm, $"Taj started with {s_bars.Count} bar(s)");

            RunMessageLoop();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Wm, "fatal", ex);
            return 1;
        }
        finally
        {
            Shutdown();
            Log.CloseFile();
        }
    }

    private static TajConfig LoadConfig(string[] args) => LoadConfig(args, out _).Config;

    /// <summary>
    /// Reads the bar's section, and says whether it is safe to apply over a running bar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Diagnostics go to the log as well as to standard error. Taj is started detached
    /// from the window manager's <c>startup-command</c>, so it has no console and every
    /// rendered caret it produced went nowhere at all - which made the promise that the
    /// config file talks back true only of <c>shubbak check-config</c>.
    /// </para>
    /// <para>
    /// The usability flag exists because <see cref="TajConfigLoader"/> answers a file
    /// that will not parse with <see cref="TajConfigLoader.CreateDefault"/>, which is
    /// the right answer at startup and the wrong one on a reload: a stray brace
    /// mid-edit replaced a carefully built bar with the stock one, silently, with
    /// nothing to connect the change to the keystroke that caused it.
    /// </para>
    /// </remarks>
    private static (TajConfig Config, bool Usable) LoadConfig(string[] args, out DiagnosticCounts problems)
    {
        problems = default;

        string? path = ResolveConfigPath(args);

        if (path is null || !File.Exists(path))
        {
            Log.Info(LogCategory.Config, "no config found; using the default bar");
            return (TajConfigLoader.CreateDefault(), true);
        }

        string source = File.ReadAllText(path);
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load(source);

        foreach (Diagnostic diagnostic in diagnostics)
            Console.Error.Write(diagnostic.Render(source, path));

        problems = ConfigDiagnostics.Report(diagnostics, path, "the bar's settings");

        bool usable = problems.Errors == 0;

        // Only when it is going to be used. Saying "loaded 1 profile(s)" and then
        // "keeping the bar as it is" two lines later is a log arguing with itself, and
        // the profile it counted is the stock one the loader falls back to rather than
        // anything that was read out of the file.
        if (usable)
        {
            Log.Info(LogCategory.Config,
                $"loaded {config.Profiles.Count} profile(s) and {config.Rules.Count} rule(s) from {path}");
        }

        return (config, usable);
    }

    /// <summary>
    /// Creates one bar per attached display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each bar gets its own model and its own connection, so a profile rule that
    /// depends on the active workspace can resolve differently per monitor - which is
    /// the whole point of per-workspace bar profiles on a multi-monitor setup.
    /// </para>
    /// <para>
    /// From the local enumeration rather than from the window manager, because the bar
    /// is usually started by the window manager's own startup command and can win the
    /// race - and a bar with no window manager yet is still a clock. Once connected,
    /// the window manager's monitor list is the one that counts; see
    /// <see cref="ReconcileBars"/>.
    /// </para>
    /// </remarks>
    private static bool CreateBars()
    {
        IReadOnlyList<MonitorInfo> monitors = MonitorSource.Enumerate();

        if (monitors.Count == 0)
        {
            Log.Error(LogCategory.Monitor, "no monitors found");
            return false;
        }

        // Full bounds rather than the work area: the bar reserves its own strip
        // through the appbar API, and using the work area would make it shrink away
        // from itself every time it re-registered.
        foreach (MonitorInfo monitor in monitors)
            CreateBar(monitor.DeviceId, monitor.Bounds);

        return s_bars.Count > 0;
    }

    /// <summary>
    /// Builds a bar for one display from the configuration in force, and starts it.
    /// </summary>
    /// <remarks>
    /// Runs on the message-loop thread, at startup and again whenever a display
    /// arrives. Everything the connection later reports is a signal for the loop, never
    /// an action - the windows and the sources belong to this thread - with one
    /// standing exception: the profile switch on a workspace change writes the model's
    /// profile from the pump thread, which is how it has always worked and what the
    /// model's dirty flag exists for.
    /// </remarks>
    /// <returns>The bar, or null if its window could not be created.</returns>
    private static Bar? CreateBar(string deviceId, Rect bounds)
    {
        TajConfig config = s_config;

        var model = new BarModel(config.Default);
        var selector = new BarProfileSelector(config.Profiles, config.Rules, config.Default);

        foreach (Core.Sources.ISource source in TajConfigLoader.CreateSources(config.Sources, KeyboardLanguage.Current))
            model.AddSource(source);

        var window = new BarWindow(model, deviceId);
        var connection = new WmConnection(model, deviceId);

        var bar = new Bar
        {
            DeviceId = deviceId,
            Window = window,
            Model = model,
            Connection = connection,
            Selector = selector,
        };

        // The handlers capture the bar, not a position in a list. A position was how a
        // bar came to filter on the wrong display after its neighbour was unplugged.
        connection.ActiveWorkspaceChanged += (workspace, monitorIndex, monitorNames) =>
        {
            bar.Workspace = workspace;
            bar.MonitorIndex = monitorIndex;
            bar.MonitorNames = monitorNames;
            SelectProfile(bar);
        };

        // Same shape as the workspace: remembered on the bar so a reload can re-pick
        // its profile, and the profile re-picked at once because a rule may name it.
        connection.ContextsChanged += contexts =>
        {
            bar.Contexts = contexts;
            SelectProfile(bar);
        };

        connection.MonitorsChanged += monitors =>
        {
            s_pendingMonitors = monitors;
            Wake();
        };

        connection.ConfigReloaded += () =>
        {
            s_reloadRequested = true;
            Wake();
        };

        // The window manager going away takes the bar with it. Signalled rather
        // than acted on, for the same reason a reload is: this runs on the
        // connection's pump thread, and the windows belong to the message loop.
        connection.WindowManagerStopped += () =>
        {
            s_running = false;
            Wake();
        };

        // A level rather than an edge, and set rather than or-ed, because every
        // connection talks to the same daemon and so reports the same answer.
        connection.SuspendedChanged += suspended =>
        {
            s_wmSuspended = suspended;
            Wake();
        };

        connection.WindowManagerTimeout = config.WindowManagerTimeout;

        window.CommandRequested += command =>
        {
            // The one verb the bar answers itself. Everything else is the window
            // manager's, and goes to it unread.
            if (KeyboardCommand.Recognises(command))
            {
                if (KeyboardCommand.TryParse(command, out KeyboardCommand? keyboard, out string? problem))
                    KeyboardLanguage.Switch(keyboard!);
                else
                    Log.Warn(LogCategory.Wm, $"refused click command '{command}': {problem}");

                return;
            }

            _ = connection.SendCommandAsync(command);
        };

        if (!window.Create(bounds))
        {
            window.Dispose();
            model.Dispose();
            return null;
        }

        // Said at startup as well as on reload. A config that has been wrong since
        // logon is the one most likely to have been given up on.
        model.SetValue("config", Problems(s_problems));

        model.Dirtied += Wake;

        // A bar created while the rest are stood down joins them, or it alone would
        // go on ticking behind the full-screen application.
        if (s_stoodDown) model.StandDown();

        connection.Start();

        s_bars.Add(bar);
        return bar;
    }

    /// <summary>
    /// Closes a bar and lets go of everything it owned.
    /// </summary>
    private static void DestroyBar(Bar bar)
    {
        s_bars.Remove(bar);

        bar.Model.Dirtied -= Wake;

        // Waited for, as at shutdown, so the pump cannot report on a bar that no longer
        // exists. It ends promptly: the token it watches is cancelled, and the one call
        // that does not take the token answers within its own timeout.
        bar.Connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

        bar.Window.Dispose();
        bar.Model.Dispose();
    }

    /// <summary>
    /// Brings the bars into line with the displays the window manager reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bars used to be created once, from the displays present at startup, and
    /// that was the whole of it. Plug in a monitor and it had no bar; unplug one and its
    /// bar stayed, reserving a strip of a display that no longer existed and filtering
    /// on a position that now belonged to a different one. A laptop docked and undocked
    /// once a day met both.
    /// </para>
    /// <para>
    /// Driven by the window manager's monitor list rather than by <c>WM_DISPLAYCHANGE</c>,
    /// for the same reason the bar reads window titles off the pipe rather than off the
    /// desktop: one party watches the displays, and the bar should agree with it about
    /// what is attached rather than race it. The window manager polls every two
    /// seconds, so a dock is reflected here within that.
    /// </para>
    /// <para>
    /// Idempotent: a bar per display the window manager knows, at that display's
    /// rectangle, and no others. Several connections report the same change and the
    /// second report finds nothing to do.
    /// </para>
    /// </remarks>
    private static void ReconcileBars(IReadOnlyList<MonitorInfoDto> monitors)
    {
        foreach (Bar bar in s_bars.ToArray())
        {
            if (monitors.Any(m => SameDevice(m.DeviceId, bar.DeviceId))) continue;

            Log.Info(LogCategory.Monitor, $"display {bar.Window.Label} has gone; closing its bar");
            DestroyBar(bar);
        }

        foreach (MonitorInfoDto monitor in monitors)
        {
            var bounds = new Rect(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            Bar? existing = s_bars.Find(b => SameDevice(b.DeviceId, monitor.DeviceId));

            if (existing is null)
            {
                Log.Info(LogCategory.Monitor,
                    $"display {monitor.DeviceId} has arrived" +
                    $"{(monitor.FriendlyName is { } name ? $" (\"{name}\")" : "")}; opening a bar on it");

                CreateBar(monitor.DeviceId, bounds);
                continue;
            }

            existing.Window.Relocate(bounds);
        }
    }

    private static bool SameDevice(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks and applies the profile for one bar.</summary>
    private static void SelectProfile(Bar bar)
    {
        BarModel model = bar.Model;
        BarProfile chosen = bar.Selector.Select(bar.Workspace, bar.MonitorIndex, bar.MonitorNames, bar.Contexts);

        if (ReferenceEquals(chosen, model.Profile)) return;

        model.Profile = chosen;

        // Logged because a profile switch changes the whole bar at once, and
        // when it looks wrong there is otherwise no way to tell whether the
        // wrong profile was chosen, the right one was built badly, or the
        // window failed to resize.
        Log.Info(LogCategory.Config,
            $"{bar.Window.Label} -> profile \"{chosen.Name}\" on workspace \"{bar.Workspace}\"" +
            (bar.Contexts.Count > 0 ? $" in context {string.Join(", ", bar.Contexts)}" : string.Empty) + " " +
            $"(height {chosen.Height}, zones: " +
            $"{string.Join(", ", chosen.Zones.Select(z => $"{z.Id}/{z.Widgets.Count}w/grow{z.Grow}"))})");
    }

    /// <summary>
    /// Re-reads the configuration and rebuilds every bar from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on the message-loop thread. Sources own timers and the bars own windows
    /// and GDI objects, and neither may be replaced from the connection's thread.
    /// </para>
    /// <para>
    /// A configuration that does not parse leaves everything exactly as it is, which
    /// is what the window manager does with the same file. Half-applying a broken
    /// config would be worse than ignoring it: the bar is how the user finds out what
    /// state they are in.
    /// </para>
    /// </remarks>
    private static void ReloadConfig()
    {
        TajConfig config;
        bool usable;
        DiagnosticCounts problems;

        try
        {
            (config, usable) = LoadConfig(s_args, out problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogCategory.Config, $"could not re-read the config: {ex.Message}");
            return;
        }

        // Kept rather than applied, matching what the window manager does with the rest
        // of the same file. A file that will not parse yields the stock bar, and
        // swapping a carefully built bar for the stock one because of a stray brace is
        // a visible change with nothing to explain it - worse than leaving it alone.
        if (!usable)
        {
            Log.Error(LogCategory.Config,
                "the configuration has errors; keeping the bar as it is. " +
                "Run `shubbak check-config` to see them.");

            s_problems = problems;

            foreach (Bar unchanged in s_bars)
                unchanged.Model.SetValue("config", Problems(problems));

            return;
        }

        // Kept for bars created from now on, so a display plugged in after a reload
        // gets the reloaded configuration rather than the one Taj started with.
        s_config = config;
        s_problems = problems;

        foreach (Bar bar in s_bars)
        {
            BarModel model = bar.Model;

            bar.Selector = new BarProfileSelector(config.Profiles, config.Rules, config.Default);

            // Sources hold timers, so the old set has to be disposed rather than
            // dropped, or a reloaded bar accumulates a clock per reload.
            model.ReplaceSources(TajConfigLoader.CreateSources(config.Sources, KeyboardLanguage.Current));

            // Forced through, rather than going via SelectProfile: the profile object
            // is new after a reload even when it is the same profile by name, and the
            // reference check would otherwise skip it.
            model.Profile = bar.Selector.Select(bar.Workspace, bar.MonitorIndex, bar.MonitorNames, bar.Contexts);

            model.SetValue("config", Problems(problems));
        }

        Log.Info(LogCategory.Config, $"reloaded; {s_bars.Count} bar(s) rebuilt");
    }

    /// <summary>
    /// What the <c>config</c> template variable says.
    /// </summary>
    /// <remarks>
    /// Empty when the settings are clean, which is the ordinary case - a widget whose
    /// template renders empty hides itself, so it costs no room and no attention. The
    /// same shape as <c>paused</c> and <c>suspended</c>, which are the other two
    /// "something is unusual" indicators and are empty almost all of the time.
    /// </remarks>
    private static string Problems(DiagnosticCounts counts) =>
        counts.Any ? $"config: {counts.Describe()}" : string.Empty;

    /// <summary>
    /// Signalled when any bar's model goes dirty, so the loop stops waiting.
    /// </summary>
    /// <remarks>
    /// Auto-reset: a signal raised while the loop is already awake and working is
    /// remembered rather than lost, so a source publishing during a redraw cannot
    /// leave its value unpainted until something else happens.
    /// </remarks>
    private static readonly AutoResetEvent s_wake = new(false);

    /// <summary>
    /// Pumps messages and updates the bars.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loop waits; it does not sleep. It used to run every 16 ms whatever was
    /// happening - sixty-two passes a second, almost all of which found the model
    /// unchanged and did nothing. That was the largest single consumer in the three
    /// processes: measured over 25 seconds of an idle desktop, the bar spent more CPU
    /// than the window manager it reports on.
    /// </para>
    /// <para>
    /// So the model says when it changes and this waits for that, exactly as the
    /// palette next door already did and the daemon's own pump has always done. A
    /// ceiling is still applied, because the cost of a missed signal is a bar that
    /// looks frozen and the cost of the ceiling is one wake a second.
    /// </para>
    /// <para>
    /// Standing down widens the ceiling and stops the sources; see
    /// <see cref="ApplyStandDown"/>. Messages are pumped either way, which is what
    /// keeps the indicator clickable.
    /// </para>
    /// </remarks>
    private static void RunMessageLoop()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            s_running = false;
            s_wake.Set();
        };

        // Closing any bar window closes the bar. Reaches here from `shubbak taj-exit`,
        // from Task Manager's "End task", and from anything else that politely asks a
        // window to go.
        BarWindow.RequestShutdown += () =>
        {
            s_running = false;
            s_wake.Set();
        };

        BarWindow.FullScreenAppChanged += up =>
        {
            s_fullScreenApp = up;
            s_wake.Set();
        };

        // The accent changed. Colours written `accent` were resolved when the file was
        // read, so it is read again - the same path a saved file takes, coalesced the
        // same way, since Windows says this once per display.
        BarWindow.SystemColoursChanged += () =>
        {
            s_reloadRequested = true;
            s_wake.Set();
        };

        // Every model wakes the loop when it changes; CreateBar wires that as each bar
        // is made, at startup and later alike, so there is nothing to do here.

        while (s_running)
        {
            while (PInvoke.PeekMessage(out MSG msg, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                if (msg.message == PInvoke.WM_QUIT)
                {
                    s_running = false;
                    break;
                }

                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }

            if (s_reloadRequested)
            {
                s_reloadRequested = false;

                // One reload per event, not one per bar. Every bar's connection hears
                // the same config.reloaded and each wakes the loop, so without this the
                // file was re-read and every source rebuilt once per display - which for
                // a command source means its process killed and started again, twice.
                // A quarter of a second is far longer than the reports are apart and far
                // shorter than a person can save a file twice.
                long now = System.Diagnostics.Stopwatch.GetTimestamp();

                if (System.Diagnostics.Stopwatch.GetElapsedTime(s_lastReloadTicks, now) > ReloadCoalesceWindow)
                {
                    s_lastReloadTicks = now;
                    ReloadConfig();
                }
            }

            // Taken and cleared in one step, so a report arriving while this pass is
            // reconciling is kept for the next one rather than lost.
            if (Interlocked.Exchange(ref s_pendingMonitors, null) is { } monitors)
                ReconcileBars(monitors);

            ApplyStandDown();

            // Ahead of the stand-down test, and deliberately. A bar standing down still
            // holds its strip - covering the screen is the full-screen application's job,
            // not something the bar does by giving its space back - so a reservation the
            // shell has refused still has to be retried while one is up.
            foreach (Bar bar in s_bars) bar.Window.EnsureReserved();

            if (!s_stoodDown) foreach (Bar bar in s_bars) bar.Window.Update();

            if (!s_running) break;

            Wait(s_stoodDown ? StoodDownCeilingMs : ActiveCeilingMs);
        }
    }

    /// <summary>Wakes the loop. Handed to every model and to anything else that changes state.</summary>
    private static void Wake() => s_wake.Set();

    /// <summary>
    /// Waits for a message, a signal, or the ceiling, whichever comes first.
    /// </summary>
    /// <remarks>
    /// <c>QS_ALLINPUT</c> so that paints, clicks and the appbar's own notifications
    /// end the wait as promptly as a source publishing does, and
    /// <c>MWMO_INPUTAVAILABLE</c> so a message that arrived between the peek loop
    /// above and this call is not slept through.
    /// </remarks>
    private static void Wait(uint milliseconds)
    {
        PInvoke.MsgWaitForMultipleObjectsEx(
            [(HANDLE)s_wake.SafeWaitHandle.DangerousGetHandle()],
            milliseconds,
            QUEUE_STATUS_FLAGS.QS_ALLINPUT,
            MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
    }

    /// <summary>
    /// The longest the loop will wait when the bar is visible, absent any signal.
    /// </summary>
    /// <remarks>
    /// A safety net rather than a schedule. Every path that changes what the bar shows
    /// signals, so in practice this expires only on a desktop where genuinely nothing
    /// is happening. It exists because the failure it guards against - a signal added
    /// later that nobody wires up - would show as a bar that has quietly stopped, and
    /// a second of staleness is a much better symptom than that.
    /// </remarks>
    private const uint ActiveCeilingMs = 1000;

    /// <summary>
    /// The longest it waits while stood down.
    /// </summary>
    /// <remarks>
    /// Shorter than the active ceiling, which looks backwards until you remember what
    /// runs here: this is also the rate at which a stand-down caused by a full-screen
    /// application is re-confirmed, and that check is what ends one. A quarter of a
    /// second is therefore the longest a mistaken stand-down can last.
    /// </remarks>
    private const uint StoodDownCeilingMs = 250;

    /// <summary>
    /// Starts or ends a stand-down, and stops or starts the sources with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transition is done here rather than where the signals arrive, because both
    /// of them arrive on other threads - a connection's pump and the window procedure -
    /// and the sources belong to the loop.
    /// </para>
    /// <para>
    /// The shell is asked only while it has claimed a full-screen application, so an
    /// ordinary desktop makes no system call at all. And a claim the shell will not
    /// confirm is <i>dropped</i> rather than merely disbelieved: leaving it set would
    /// mean asking again on every pass, which at the active tick rate is sixty-two
    /// system calls a second to keep answering the same question. The shell says so
    /// again if a full-screen application really does come back.
    /// </para>
    /// </remarks>
    private static void ApplyStandDown()
    {
        if (s_fullScreenApp && !StandDown.StillCovered(DisplayPreferences.CurrentActivity()))
        {
            // The edge has outlived what it described. ABN_FULLSCREENAPP reports an
            // opening and a closing, not what is in front, so this is expected rather
            // than exceptional.
            s_fullScreenApp = false;
        }

        bool wanted = StandDown.ShouldStandDown(
            s_wmSuspended, s_fullScreenApp, confirmed: true, s_bars.Count);

        if (wanted == s_stoodDown) return;

        s_stoodDown = wanted;

        foreach (Bar bar in s_bars)
        {
            if (wanted) bar.Model.StandDown();
            else bar.Model.StandUp();
        }

        if (wanted)
        {
            // Drawn once more before going quiet, so the bar is left showing the state
            // that stopped it rather than whatever it happened to be showing a frame
            // earlier.
            foreach (Bar bar in s_bars) bar.Window.Update();
        }

        Log.Info(LogCategory.Wm, wanted
            ? $"standing down: {(s_wmSuspended ? "the window manager is suspended" : "a full-screen application is covering the bar")}"
            : "standing up: the bar is visible again");
    }

    private static void Shutdown()
    {
        // Connections first, so no pump can report on a bar that is being torn down.
        foreach (Bar bar in s_bars)
            bar.Connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

        foreach (Bar bar in s_bars) bar.Window.Dispose();
        foreach (Bar bar in s_bars) bar.Model.Dispose();

        s_bars.Clear();
    }

    /// <summary>
    /// Sets up logging from the shared config, then from the command line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Taj reads the <c>logging</c> section of the same file the window manager does,
    /// so turning logging on is one edit rather than two - and, more to the point,
    /// so it is on at all. Taj is normally launched by a startup command with no
    /// arguments, which meant it had no logging whatsoever: a question about why the
    /// bar looked wrong could not be answered, because the bar had never written
    /// anything down.
    /// </para>
    /// <para>
    /// It writes to <c>taj.log</c> rather than the window manager's file. Two
    /// processes cannot share one, and the window manager rotates its own on start.
    /// </para>
    /// </remarks>
    private static void ConfigureLogging(string[] args)
    {
        // Beside the window manager's, unless the config or the command line says
        // otherwise. Not optional, and its absence was found the hard way: this used to
        // open a file only when `logging { file }` named one, so a config that could
        // not be parsed - which yields defaults, and a default with no log path - left
        // Taj with nowhere at all to say why. That is the one case where being able to
        // say anything matters, and it was the one case that had no log. Dalil has
        // always defaulted this way; the asymmetry was not a decision.
        string configuredFile = DefaultTajLogPath;

        if (ConfigPathResolver.Resolve(Value(args, "--config")).Path is { } configPath &&
            File.Exists(configPath))
        {
            try
            {
                ShubbakConfig shared = ConfigLoader.LoadFile(configPath).Config;

                Log.Level = shared.LogLevel;

                // Taj writes beside the window manager's log, never into it. The
                // config resolves an empty path to the window manager's own file, and
                // two processes cannot share one - the second to open it truncates
                // the first's, which is worse than not logging at all.
                if (shared.LogFile is { Length: > 0 } file)
                    configuredFile = Path.Combine(
                        Path.GetDirectoryName(file) ?? string.Empty, "taj.log");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A bar that cannot read the config still has a default to draw.
            }
        }

        // The command line wins, so a one-off investigation does not need a config edit.
        if (Value(args, "--log-level") is { } level && Log.TryParseLevel(level, out LogLevel parsed))
            Log.Level = parsed;

        // Off unless output genuinely leads somewhere - a console, or a redirect. Taj
        // is normally started from the window manager's startup-command, where these
        // entries were formatted and then discarded on every single one.
        Log.ToConsole = ConsoleHost.HasOutput
            && !args.Contains("--quiet", StringComparer.Ordinal);

        int index = Array.IndexOf(args, "--log-file");

        if (index >= 0)
        {
            configuredFile = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1]
                : DefaultTajLogPath;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configuredFile)!);
            Log.OpenFile(configuredFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"taj: could not open log file: {ex.Message}");
        }
    }

    private static string DefaultTajLogPath =>
        Path.Combine(Path.GetDirectoryName(Log.DefaultLogPath)!, "taj.log");


    /// <summary>
    /// Finds the config file.
    /// </summary>
    /// <remarks>
    /// Shares <see cref="ConfigPathResolver"/> with the window manager and the CLI,
    /// so the bar can never end up reading a different file from the thing it is
    /// displaying.
    /// </remarks>
    private static string? ResolveConfigPath(string[] args) =>
        ConfigPathResolver.Resolve(Value(args, "--config")).Path;

    private static string? Value(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.Ordinal)) return args[i + 1];

        return null;
    }

    private static void PrintUsage() => Console.WriteLine("""
        Taj - the status bar for Shubbak

        USAGE
          taj [options]

        OPTIONS
          --config <path>      Config file. Taj reads the `bar` section of the same
                               file Shubbak uses, so there is one config to learn,
                               and resolves it the same way - including
                               $XDG_CONFIG_HOME. Run `shubbak config-path` to see
                               which file is in effect.
          --log-level <level>  trace | debug | info | warn | error | none
          --log-file [path]    Also write to a file.
          --quiet              Do not write to the console.
          --version            Print the version and exit.
          --help               Show this message.

        NOTES
          One bar is created per monitor. Each reserves its strip through the shell's
          appbar API, so maximised windows stop at its edge.

          Taj retries until the window manager appears, so it can be launched from
          Shubbak's own startup-command without a race.
        """);
}
