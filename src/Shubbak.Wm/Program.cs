using System.Runtime;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;
using Shubbak.Native;

namespace Shubbak.Wm;

/// <summary>The Shubbak window manager daemon.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return 0;
        }

        ConfigureLogging(args);

        // A garbage collection suspends every thread in the process, including the
        // one servicing the low-level keyboard hook - and until that thread answers,
        // the keystroke has not reached the focused application. Sustained low
        // latency trades a little memory for shorter pauses, which is the right way
        // round for something that sits between the user and every keypress.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // Same argument, applied to the scheduler rather than the collector. A
        // long-lived process with low average CPU is exactly what Windows moves onto
        // efficiency cores and throttles under EcoQoS - a good default for a daemon,
        // and the wrong one for a daemon holding a keyboard hook with a 300 ms
        // deadline before the system silently unhooks it.
        //
        // Reported by `diagnose` rather than applied silently, because it trades
        // power for punctuality and that is a trade someone on a laptop may want to
        // know about.
        PowerThrottling.OptOut();

        string? configPath = ResolveConfigPath(args);

        if (args.Contains("--check-config", StringComparer.Ordinal))
            return CheckConfig(configPath);

        using var daemon = new WmDaemon();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Info(LogCategory.Wm, "shutdown requested");
            daemon.Stop();
        };

        // A crash in a window manager strands every window it was managing, so the
        // last thing it does is leave behind a report that explains what happened.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                Log.Error(LogCategory.Wm, "unhandled exception", exception);

            TryWriteCrashReport(configPath);
        };

        try
        {
            daemon.Run(configPath);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Wm, "fatal", ex);
            TryWriteCrashReport(configPath);
            return 1;
        }
        finally
        {
            Log.CloseFile();
        }
    }

    /// <summary>
    /// Applies logging options from the command line.
    /// </summary>
    /// <remarks>
    /// Deliberately done before anything else, so that a failure during config
    /// loading or monitor enumeration is itself captured.
    /// </remarks>
    private static void ConfigureLogging(string[] args)
    {
        if (Value(args, "--log-level") is { } levelText)
        {
            if (Log.TryParseLevel(levelText, out LogLevel level))
            {
                Log.Level = level;
            }
            else
            {
                Console.Error.WriteLine(
                    $"shubbak-wm: unknown log level '{levelText}'. " +
                    "Use trace, debug, info, warn, error or none.");
            }
        }

        if (args.Contains("--quiet", StringComparer.Ordinal)) Log.ToConsole = false;

        // --log-file with no value means "the standard location", because that is
        // what people want and remembering the path is friction.
        int index = Array.IndexOf(args, "--log-file");
        if (index >= 0)
        {
            string path = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 1]
                : Log.DefaultLogPath;

            try
            {
                Log.OpenFile(path);
                Console.Error.WriteLine($"shubbak-wm: logging to {Log.FilePath}");
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"shubbak-wm: could not open log file: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"shubbak-wm: could not open log file: {ex.Message}");
            }
        }
    }

    private static void TryWriteCrashReport(string? configPath)
    {
        try
        {
            var report = new DiagnosticReport("crash")
                .AddEnvironment();

            if (configPath is not null && File.Exists(configPath))
                report.AddCodeSection("Config", File.ReadAllText(configPath), "kdl");

            string path = report
                .AddRecentLog()
                .AddFooter()
                .WriteTo(Path.Combine(
                    Path.GetDirectoryName(Log.DefaultLogPath)!,
                    $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.md"));

            Console.Error.WriteLine($"shubbak-wm: crash report written to {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful left to do; the process is going down regardless.
        }
    }

    /// <summary>
    /// Validates config and exits, without touching a single window.
    /// </summary>
    private static int CheckConfig(string? path)
    {
        if (path is null)
        {
            Console.Error.WriteLine("no config file found");
            return 1;
        }

        ConfigLoadResult result = ConfigLoader.LoadFile(path);
        string source = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        foreach (Diagnostic diagnostic in result.Diagnostics)
            Console.Error.Write(diagnostic.Render(source, path));

        int errors = result.Errors.Count();
        int warnings = result.Warnings.Count();

        Console.WriteLine(
            errors == 0 && warnings == 0
                ? $"{path}: ok - {result.Config.Keybindings.Count} keybindings, " +
                  $"{result.Config.Workspaces.Count} workspaces, {result.Config.Rules.Count} rules"
                : $"{path}: {errors} error(s), {warnings} warning(s)");

        return errors == 0 ? 0 : 1;
    }

    /// <summary>
    /// Finds the config file.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="ConfigPathResolver"/> so the daemon, the CLI and the
    /// bar cannot disagree about which file is in effect - and reports where nothing
    /// was found, since "no config file" is useless when the file exists but the
    /// search looked elsewhere.
    /// </remarks>
    private static string? ResolveConfigPath(string[] args)
    {
        ConfigLocation location = ConfigPathResolver.ResolveAndLog(Value(args, "--config"));

        if (!location.Found) Console.Error.Write(location.DescribeSearch());

        return location.Path;
    }

    private static string? Value(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
                return args[i + 1];

        return null;
    }

    private static void PrintUsage() => Console.WriteLine("""
        Shubbak - a tiling window manager for Windows

        USAGE
          shubbak-wm [options]

        OPTIONS
          --config <path>      Config file to load. Search order:
                                 1. --config
                                 2. $SHUBBAK_CONFIG        (file or directory)
                                 3. $XDG_CONFIG_HOME/shubbak/shubbak.kdl
                                 4. $XDG_CONFIG_DIRS entries
                                 5. %USERPROFILE%\.config\shubbak\shubbak.kdl
                                 6. %APPDATA%\shubbak\shubbak.kdl
                               Run `shubbak config-path` to see which one won.
          --check-config       Validate the config and exit without managing windows.

          --log-level <level>  trace | debug | info | warn | error | none
                               Default: info.
                               trace records every window event and command - verbose,
                               but it is what makes a problem reproducible from a log.
          --log-file [path]    Also write to a file. With no path, uses
                               %LOCALAPPDATA%\Shubbak\shubbak.log
          --quiet              Do not write to the console.

          --help               Show this message.

        DIAGNOSING A PROBLEM
          Reproduce it with tracing on, then bundle everything into one file:

            shubbak-wm --log-level trace --log-file
            shubbak diagnose --output report.md

          The report includes the environment, the config, the live window tree and
          the recent log. Recent entries are kept in memory even when file logging is
          off, so a report is still useful after an unexpected problem.

        NOTES
          Run elevated to manage windows belonging to elevated processes;
          without it those windows are detected but cannot be moved.
        """);
}
