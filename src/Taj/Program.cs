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

    private static volatile bool s_running = true;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return 0;
        }

        ConfigureLogging(args);

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

            foreach (Core.Sources.ISource source in TajConfigLoader.CreateSources(config.Sources))
                model.AddSource(source);

            var bar = new BarWindow(model, index);
            var connection = new WmConnection(model, index);

            int monitorIndex = index;

            connection.ActiveWorkspaceChanged += workspace =>
            {
                BarProfile chosen = selector.Select(workspace, monitorIndex);

                if (ReferenceEquals(chosen, model.Profile)) return;

                model.Profile = chosen;

                // Logged because a profile switch changes the whole bar at once, and
                // when it looks wrong there is otherwise no way to tell whether the
                // wrong profile was chosen, the right one was built badly, or the
                // window failed to resize.
                Log.Info(LogCategory.Config,
                    $"monitor {monitorIndex} -> profile \"{chosen.Name}\" on workspace \"{workspace}\" " +
                    $"(height {chosen.Height}, zones: " +
                    $"{string.Join(", ", chosen.Zones.Select(z => $"{z.Id}/{z.Widgets.Count}w/grow{z.Grow}"))})");
            };

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
            s_connections.Add(connection);
        }

        return s_bars.Count > 0;
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

                // An empty string means "the standard place", matching how the window
                // manager reads the same setting.
                if (shared.LogFile is { } file)
                    configuredFile = file.Length > 0 ? file : DefaultTajLogPath;
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
