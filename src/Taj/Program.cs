using System.Runtime.InteropServices;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
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

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return 0;
        }

        ConfigureLogging(args);
        s_args = args;

        // Before any window is created: without it Windows reports virtualised
        // coordinates on scaled displays and the bar lands in the wrong place.
        PInvoke.SetProcessDpiAwarenessContext((DPI_AWARENESS_CONTEXT)(nint)(-4));

        try
        {
            TajConfig config = LoadConfig(args);

            if (!CreateBars(config))
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

    private static TajConfig LoadConfig(string[] args)
    {
        string? path = ResolveConfigPath(args);

        if (path is null || !File.Exists(path))
        {
            Log.Info(LogCategory.Config, "no config found; using the default bar");
            return TajConfigLoader.CreateDefault();
        }

        string source = File.ReadAllText(path);
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load(source);

        foreach (Diagnostic diagnostic in diagnostics)
            Console.Error.Write(diagnostic.Render(source, path));

        Log.Info(LogCategory.Config,
            $"loaded {config.Profiles.Count} profile(s) and {config.Rules.Count} rule(s) from {path}");

        return config;
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

        try
        {
            config = LoadConfig(s_args);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogCategory.Config, $"could not re-read the config: {ex.Message}");
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
        }

        Log.Info(LogCategory.Config, $"reloaded; {s_bars.Count} bar(s) rebuilt");
    }

    /// <summary>
    /// Pumps messages and updates the bars.
    /// </summary>
    /// <remarks>
    /// A 16 ms tick, but a bar only repaints when its model reports a change. The
    /// tick exists to poll for those changes, not to drive redraws.
    /// </remarks>
    private static void RunMessageLoop()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            s_running = false;
        };

        // Closing any bar window closes the bar. Reaches here from `shubbak taj-exit`,
        // from Task Manager's "End task", and from anything else that politely asks a
        // window to go.
        BarWindow.RequestShutdown += () => s_running = false;

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

            foreach (BarWindow bar in s_bars) bar.Update();

            Thread.Sleep(16);
        }
    }

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
        string? configuredFile = null;

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

        int index = Array.IndexOf(args, "--log-file");

        if (index >= 0)
        {
            configuredFile = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1]
                : DefaultTajLogPath;
        }

        if (configuredFile is null) return;

        try
        {
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
          --help               Show this message.

        NOTES
          One bar is created per monitor. Each reserves its strip through the shell's
          appbar API, so maximised windows stop at its edge.

          Taj retries until the window manager appears, so it can be launched from
          Shubbak's own startup-command without a race.
        """);
}
