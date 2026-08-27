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
        // Anything that answers and exits comes first, and each one takes a console
        // before it writes, because this is a GUI-subsystem binary and stdout goes
        // nowhere until something asks for one. See ConsoleHost.
        if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
        {
            ConsoleHost.Ensure();
            PrintUsage();
            return 0;
        }

        // Answered before the config is touched, so that a build with a broken config
        // can still be identified in a bug report.
        if (Array.Exists(args, a => a is "--version" or "-v" or "version"))
        {
            ConsoleHost.Ensure();
            Console.WriteLine(ShubbakVersion.Banner);
            return 0;
        }

        // --foreground is what a human uses to watch the daemon work. It is the only
        // reason this binary opens a console on the ordinary path.
        bool foreground = args.Contains("--foreground", StringComparer.Ordinal)
            || args.Contains("--console", StringComparer.Ordinal);

        if (foreground) ConsoleHost.Ensure();

        ConfigureLogging(args, foreground);

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

        // A terminal-facing operation by definition: it exists to print diagnostics
        // with carets under them, so it takes a console whether or not one was asked
        // for. `shubbak check-config` is the fuller check - it validates the bar
        // section too - and the usage text points there.
        if (args.Contains("--check-config", StringComparer.Ordinal))
        {
            ConsoleHost.Ensure();
            return CheckConfig(configPath);
        }

        // Only one window manager per account, enforced before anything touches the
        // desktop. Two daemons used to start in silence and then fight: two low-level
        // keyboard hooks, so every binding fired twice; two layout passes contradicting
        // each other through DeferWindowPos; a CLI connecting to whichever accept loop
        // won the race; and on exit, one restoring windows the other still believed
        // were concealed. Nothing reported any of it, because nothing was looking.
        using var instance = SingleInstance.TryAcquire(
            args.Contains("--replace", StringComparer.Ordinal));

        if (!instance.Held) return 1;

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

            if (TryWriteCrashReport(configPath) is { } report)
                Note($"shubbak-wm: crash report written to {report}");
        };

        try
        {
            daemon.Run(configPath);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Wm, "fatal", ex);

            // Written before the message, so the message can name it. Fail blocks
            // when it owns the console, and a report the user is told about only
            // after they dismiss the window would be told about too late.
            string? report = TryWriteCrashReport(configPath);

            // The whole risk of a GUI-subsystem daemon is that this is silent. A
            // window manager that fails to start and says nothing is indistinguishable
            // from one that was never launched, so this takes a console rather than
            // assuming it has one.
            Fail(report is null
                ? $"shubbak-wm: {ex.Message}"
                : $"shubbak-wm: {ex.Message}{Environment.NewLine}" +
                  $"shubbak-wm: crash report written to {report}");

            return 1;
        }
        finally
        {
            Log.CloseFile();
        }
    }

    /// <summary>
    /// Reports a fatal problem where the user can actually see it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This binary has no console unless it asks for one, so an error written to
    /// standard error before that point goes nowhere. Every path that ends the process
    /// unhappily comes through here instead of writing directly.
    /// </para>
    /// <para>
    /// When the console was created by us rather than inherited from a terminal it
    /// dies with the process, taking the message with it before it can be read. In
    /// that case, and only that case, the process waits.
    /// </para>
    /// </remarks>
    private static void Fail(string message)
    {
        ConsoleHost.EnsureForError();

        try
        {
            Console.Error.WriteLine(message);

            if (ConsoleHost.OwnsConsole)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Press any key to close this window.");
                Console.ReadKey(intercept: true);
            }
        }
        catch (Exception)
        {
            // There is no console and no way to make one. The log file and the crash
            // report are what is left, and both are written by the caller.
        }
    }

    /// <summary>
    /// Applies logging options from the command line.
    /// </summary>
    /// <remarks>
    /// Deliberately done before anything else, so that a failure during config
    /// loading or monitor enumeration is itself captured.
    /// </remarks>
    /// <param name="args">The command line.</param>
    /// <param name="foreground">
    /// Whether a console was opened for a human to read. Console logging follows this
    /// rather than defaulting on: without a console the writes are discarded, and at
    /// logon there is nobody to read them anyway.
    /// </param>
    private static void ConfigureLogging(string[] args, bool foreground)
    {
        Log.ToConsole = foreground && !args.Contains("--quiet", StringComparer.Ordinal);

        if (Value(args, "--log-level") is { } levelText)
        {
            if (Log.TryParseLevel(levelText, out LogLevel level))
            {
                Log.Level = level;
            }
            else
            {
                Log.Warn(LogCategory.Wm, $"unknown log level '{levelText}', keeping {Log.Level}");

                Note($"shubbak-wm: unknown log level '{levelText}'. " +
                     "Use trace, debug, info, warn, error or none.");
            }
        }

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
                Note($"shubbak-wm: logging to {Log.FilePath}");
            }
            catch (IOException ex)
            {
                Note($"shubbak-wm: could not open log file: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Note($"shubbak-wm: could not open log file: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Writes to standard error if - and only if - somebody is there to read it.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="Fail"/>. These messages are worth reading in a
    /// terminal and not worth conjuring a window for at logon, so unlike
    /// <see cref="Fail"/> this never creates a console. Everything said here is also
    /// on its way to the log, which is what a report will be read from.
    /// </remarks>
    private static void Note(string message)
    {
        if (!ConsoleHost.HasOutput) return;

        try
        {
            Console.Error.WriteLine(message);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Writes a crash report, and says where it went.</summary>
    /// <returns>The report's path, or <see langword="null"/> if none could be written.</returns>
    private static string? TryWriteCrashReport(string? configPath)
    {
        try
        {
            var report = new DiagnosticReport("crash")
                .AddEnvironment();

            if (configPath is not null && File.Exists(configPath))
                report.AddCodeSection("Config", File.ReadAllText(configPath), "kdl");

            return report
                .AddRecentLog()
                .AddFooter()
                .WriteTo(Path.Combine(
                    Path.GetDirectoryName(Log.DefaultLogPath)!,
                    $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.md"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful left to do; the process is going down regardless.
            return null;
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

        // Not fatal - the daemon runs on defaults - so this is a Note rather than a
        // Fail. ResolveAndLog has already put it in the log either way, which is where
        // a report will read it from when nobody was watching a terminal.
        if (!location.Found) Note(location.DescribeSearch());

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
                               `shubbak check-config` checks more - it validates the
                               bar's section of the same file as well - and is the
                               one to prefer.

          --foreground         Run attached to a console, for watching it work.
                               Without this the daemon has no console at all: it is a
                               GUI-subsystem binary so that starting it at logon does
                               not leave a black window on the desktop. Errors that
                               stop it starting will still open one and say so.
                               --console is accepted as a synonym.

          --log-level <level>  trace | debug | info | warn | error | none
                               Default: info.
                               trace records every window event and command - verbose,
                               but it is what makes a problem reproducible from a log.
          --log-file [path]    Also write to a file. With no path, uses
                               %LOCALAPPDATA%\Shubbak\shubbak.log
          --quiet              Do not write to the console, even with --foreground.

          --version            Print the version and exit.
          --help               Show this message.

          --replace            Ask a running window manager to stand down, then take
                               over. Without it, starting a second one is refused -
                               two window managers on one desktop fight over every
                               window and run every keybinding twice.

        GETTING OUT OF THE WAY
          shubbak wm-toggle-suspend

          Releases the keyboard hook and the window event hooks, and leaves every
          window exactly where it is. This is the one to use before a game: a bound
          chord is a chord the game never receives, and suspending gives it back.

          Resume with the same key - the system watches for that one chord while
          suspended, which costs nothing per keystroke because it is not a hook -
          or with `shubbak wm-resume`.

          `wm-toggle-pause` is a different thing and worth not confusing: it stops
          windows being rearranged but keeps the keyboard, so bindings still work.

        DIAGNOSING A PROBLEM
          Reproduce it with tracing on, then bundle everything into one file:

            shubbak-wm --foreground --log-level trace --log-file
            shubbak diagnose --output report.md

          The report includes the environment, the config, the live window tree and
          the recent log. Recent entries are kept in memory even when file logging is
          off, so a report is still useful after an unexpected problem.

          Because the shell does not wait for a GUI-subsystem process, --foreground
          returns you to the prompt immediately and then writes underneath it. The
          output is all there; it just shares the screen with your next command.
          Redirecting is what to do if that matters:

            shubbak-wm --foreground --log-level trace --log-file only.log

        STARTING IT WITH WINDOWS
          shubbak autostart enable

          Registers this binary to run at logon, from wherever it currently lives.
          `shubbak autostart status` says whether it is registered and from where.

        NOTES
          Run elevated to manage windows belonging to elevated processes;
          without it those windows are detected but cannot be moved.
        """);
}
