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
    private static readonly List<BarWindow> s_bars = [];
    private static readonly List<BarModel> s_models = [];
    private static readonly List<WmConnection> s_connections = [];

    /// <summary>Per-bar state a reload has to rebuild.</summary>
    private static readonly List<BarProfileSelector> s_selectors = [];

    /// <summary>
    /// The workspace each bar last reported, so a reload can re-pick its profile.
    /// </summary>
    private static readonly List<string> s_workspaces = [];

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
    /// A hint rather than the truth. <c>ABN_FULLSCREENAPP</c> reports an opening and a
    /// closing, not what is in front right now, so this starts a stand-down and
    /// <c>StandDown.StillCovered</c> is what keeps it going.
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

        try
        {
            (TajConfig config, _) = LoadConfig(args, out DiagnosticCounts problems);

            if (!CreateBars(config))
            {
                Log.Error(LogCategory.Wm, "no bars could be created");
                return 1;
            }

            // Said at startup as well as on reload. A config that has been wrong since
            // logon is the one most likely to have been given up on.
            foreach (BarModel model in s_models)
                model.SetValue("config", Problems(problems));

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
    /// Creates one bar per monitor.
    /// </summary>
    /// <remarks>
    /// Each bar gets its own model and its own connection, so a profile rule that
    /// depends on the active workspace can resolve differently per monitor - which is
    /// the whole point of per-workspace bar profiles on a multi-monitor setup.
    /// </remarks>
    private static bool CreateBars(TajConfig config)
    {
        List<Rect> monitors = EnumerateMonitors();

        if (monitors.Count == 0)
        {
            Log.Error(LogCategory.Monitor, "no monitors found");
            return false;
        }

        for (int index = 0; index < monitors.Count; index++)
        {
            var model = new BarModel(config.Default);
            var selector = new BarProfileSelector(config.Profiles, config.Rules, config.Default);

            foreach (Core.Sources.ISource source in TajConfigLoader.CreateSources(config.Sources, KeyboardLanguage.Current))
                model.AddSource(source);

            var bar = new BarWindow(model, index);
            var connection = new WmConnection(model, index);

            int monitorIndex = index;

            connection.ActiveWorkspaceChanged += workspace =>
            {
                s_workspaces[monitorIndex] = workspace;
                SelectProfile(monitorIndex, workspace);
            };

            connection.ConfigReloaded += () => s_reloadRequested = true;

            // The window manager going away takes the bar with it. Signalled rather
            // than acted on, for the same reason a reload is: this runs on the
            // connection's pump thread, and the windows belong to the message loop.
            connection.WindowManagerStopped += () => s_running = false;

            // A level rather than an edge, and set rather than or-ed, because every
            // connection talks to the same daemon and so reports the same answer.
            connection.SuspendedChanged += suspended => s_wmSuspended = suspended;

            connection.WindowManagerTimeout = config.WindowManagerTimeout;

            bar.CommandRequested += command => _ = connection.SendCommandAsync(command);

            if (!bar.Create(monitors[index]))
            {
                bar.Dispose();
                model.Dispose();
                continue;
            }

            connection.Start();

            s_models.Add(model);
            s_bars.Add(bar);
            s_selectors.Add(selector);
            s_workspaces.Add(string.Empty);
            s_connections.Add(connection);
        }

        return s_bars.Count > 0;
    }

    /// <summary>Picks and applies the profile for one bar.</summary>
    private static void SelectProfile(int index, string workspace)
    {
        if (index >= s_models.Count || index >= s_selectors.Count) return;

        BarModel model = s_models[index];
        BarProfile chosen = s_selectors[index].Select(workspace, index);

        if (ReferenceEquals(chosen, model.Profile)) return;

        model.Profile = chosen;

        // Logged because a profile switch changes the whole bar at once, and
        // when it looks wrong there is otherwise no way to tell whether the
        // wrong profile was chosen, the right one was built badly, or the
        // window failed to resize.
        Log.Info(LogCategory.Config,
            $"monitor {index} -> profile \"{chosen.Name}\" on workspace \"{workspace}\" " +
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

            foreach (BarModel unchanged in s_models)
                unchanged.SetValue("config", Problems(problems));

            return;
        }

        for (int index = 0; index < s_models.Count; index++)
        {
            BarModel model = s_models[index];

            s_selectors[index] =
                new BarProfileSelector(config.Profiles, config.Rules, config.Default);

            // Sources hold timers, so the old set has to be disposed rather than
            // dropped, or a reloaded bar accumulates a clock per reload.
            model.ReplaceSources(TajConfigLoader.CreateSources(config.Sources, KeyboardLanguage.Current));

            // Forced through, rather than going via SelectProfile: the profile object
            // is new after a reload even when it is the same profile by name, and the
            // reference check would otherwise skip it.
            model.Profile = s_selectors[index].Select(s_workspaces[index], index);

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

        foreach (BarModel model in s_models) model.Dirtied += Wake;

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
                ReloadConfig();
            }

            ApplyStandDown();

            // Ahead of the stand-down test, and deliberately. A bar standing down still
            // holds its strip - covering the screen is the full-screen application's job,
            // not something the bar does by giving its space back - so a reservation the
            // shell has refused still has to be retried while one is up.
            foreach (BarWindow bar in s_bars) bar.EnsureReserved();

            if (!s_stoodDown) foreach (BarWindow bar in s_bars) bar.Update();

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
        if (s_fullScreenApp && !StandDown.StillCovered(CurrentActivity()))
        {
            // The edge has outlived what it described. ABN_FULLSCREENAPP reports an
            // opening and a closing, not what is in front, so this is expected rather
            // than exceptional.
            s_fullScreenApp = false;
        }

        bool wanted = StandDown.ShouldStandDown(s_wmSuspended, s_fullScreenApp, confirmed: true);

        if (wanted == s_stoodDown) return;

        s_stoodDown = wanted;

        foreach (BarModel model in s_models)
        {
            if (wanted) model.StandDown();
            else model.StandUp();
        }

        if (wanted)
        {
            // Drawn once more before going quiet, so the bar is left showing the state
            // that stopped it rather than whatever it happened to be showing a frame
            // earlier.
            foreach (BarWindow bar in s_bars) bar.Update();
        }

        Log.Info(LogCategory.Wm, wanted
            ? "standing down: nothing on screen is showing the bar"
            : "standing up: the bar is visible again");
    }

    /// <summary>
    /// Asks the shell what the user is doing, in the terms <c>Taj.Core</c> uses.
    /// </summary>
    /// <remarks>
    /// The mapping lives here because <c>Taj.Core</c> is deliberately free of Win32,
    /// which is what lets the rule that consumes this be tested without a desktop.
    /// </remarks>
    private static UserActivityKind CurrentActivity() => DisplayPreferences.CurrentActivity() switch
    {
        UserActivity.FullScreenGame => UserActivityKind.FullScreenGame,
        UserActivity.FullScreenApp => UserActivityKind.FullScreenApp,
        UserActivity.Presenting => UserActivityKind.Presenting,
        UserActivity.Ordinary => UserActivityKind.Ordinary,
        _ => UserActivityKind.Unknown,
    };

    private static unsafe List<Rect> EnumerateMonitors()
    {
        List<Rect> monitors = [];
        GCHandle handle = GCHandle.Alloc(monitors);

        try
        {
            PInvoke.EnumDisplayMonitors(
                HDC.Null, (RECT?)null, &Collect, new LPARAM(GCHandle.ToIntPtr(handle)));
        }
        finally
        {
            handle.Free();
        }

        return monitors;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe BOOL Collect(HMONITOR monitor, HDC _, RECT* __, LPARAM lParam)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(lParam.Value);
            if (handle.Target is not List<Rect> list) return true;

            var info = new MONITORINFOEXW
            {
                monitorInfo = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFOEXW) },
            };

            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                RECT bounds = info.monitorInfo.rcMonitor;

                // Full bounds rather than the work area: the bar reserves its own
                // strip through the appbar API, and using the work area would make it
                // shrink away from itself every time it re-registered.
                list.Add(Rect.FromEdges(bounds.left, bounds.top, bounds.right, bounds.bottom));
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static void Shutdown()
    {
        foreach (WmConnection connection in s_connections)
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

        foreach (BarWindow bar in s_bars) bar.Dispose();
        foreach (BarModel model in s_models) model.Dispose();

        s_connections.Clear();
        s_bars.Clear();
        s_models.Clear();
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
