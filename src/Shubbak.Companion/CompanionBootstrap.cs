using Shubbak.Config;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using Shubbak.Native;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;

namespace Shubbak.Companion;

/// <summary>
/// Who a companion is, for the things the bootstrap does in its name.
/// </summary>
/// <param name="Name">The executable's name and the log file's stem: <c>taj</c>, <c>dalil</c>, <c>ayn</c>.</param>
/// <param name="Noun">What one of it is called in a sentence: <c>a bar</c>, <c>a palette</c>, <c>a watcher</c>.</param>
/// <param name="Usage">What <c>--help</c> prints.</param>
/// <param name="HasWindow">
/// Whether it puts a window on the desktop. A companion that does opts into per-monitor
/// DPI awareness before creating one - without it Windows reports virtualised
/// coordinates on scaled displays and the window lands in the wrong place - and adopts
/// the machine's accent colour before its configuration is read, so a colour written
/// <c>accent</c> resolves to this machine's.
/// </param>
public sealed record CompanionIdentity(string Name, string Noun, string Usage, bool HasWindow)
{
    /// <summary>The stop command a person types to end it: <c>shubbak taj-exit</c>.</summary>
    public string ExitCommand => $"shubbak {Name}-exit";
}

/// <summary>
/// What the bootstrap hands the program once it has been started properly.
/// </summary>
/// <param name="Args">The command line.</param>
/// <param name="ConfigPath">The <c>--config</c> path, if one was given; the resolver decides the rest.</param>
public sealed record CompanionContext(string[] Args, string? ConfigPath);

/// <summary>
/// The start of every companion, once.
/// </summary>
/// <remarks>
/// <para>
/// In this order, and the order is most of the point: <c>--help</c> and
/// <c>--version</c> first, since they want a console and nothing else; then the
/// single-instance lock; then the log file; then the process-wide settings a window
/// needs; then the program. The lock before the log, because opening the log file
/// truncates it and the copy already running is writing to it - a duplicate that said
/// "already running" and left used to take the first copy's log with it, and the
/// window manager starts a duplicate of each companion on every restart.
/// </para>
/// <para>
/// A duplicate says so only to a terminal it was actually typed into. The usual one
/// was started by the window manager, which has no console, and conjuring one to
/// print a line nobody will read flashes a black window on every restart.
/// </para>
/// <para>
/// An exception nothing caught is written to the log before the process ends, which
/// is the one thing a companion started at logon with no console owes the person
/// wondering why it is not there.
/// </para>
/// </remarks>
public static class CompanionBootstrap
{
    /// <summary>Runs a companion, returning the process exit code.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="identity">Who this is.</param>
    /// <param name="body">The program, run once everything above it is in place.</param>
    /// <param name="preflight">
    /// Something to do instead of running, decided from the command line alone -
    /// <c>ayn --report</c>, which prints and leaves. Runs after help and version and
    /// before the lock, so it works beside a running copy. Returns the exit code to
    /// leave with, or null to carry on.
    /// </param>
    public static int Run(
        string[] args,
        CompanionIdentity identity,
        Func<CompanionContext, int> body,
        Func<CompanionContext, int?>? preflight = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(body);

        // Both want a console and nothing else. A GUI-subsystem binary starts with no
        // console and every write to one is discarded, so both printed nothing at all
        // before ConsoleHost was there to ask for one.
        if (Arguments.AsksForHelp(args))
        {
            ConsoleHost.Ensure();
            Console.WriteLine(identity.Usage);
            return 0;
        }

        if (Arguments.AsksForVersion(args))
        {
            ConsoleHost.Ensure();
            Console.WriteLine(ShubbakVersion.Banner);
            return 0;
        }

        var context = new CompanionContext(args, Arguments.Value(args, "--config") ?? Arguments.Value(args, "-c"));

        if (preflight?.Invoke(context) is { } exit) return exit;

        using SingleInstanceLock instance = SingleInstanceLock.Claim(IpcProtocol.InstanceMutexNameFor(identity.Name));

        // An uncertain answer starts anyway. Two of a companion are confusing and
        // easily undone; none, because a mutex could not be opened, is the worse
        // outcome and the one the guard exists to avoid.
        if (!instance.Held && instance.Certain)
        {
            if (ConsoleHost.TryAttach())
            {
                Console.Error.WriteLine($"{identity.Name}: {identity.Noun} is already running.");
                Console.Error.WriteLine($"hint: `{identity.ExitCommand}` stops it.");
            }

            return 1;
        }

        ConfigureLogging(context, identity, toFile: true);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error(LogCategory.Wm, $"{identity.Name} is ending on an unhandled exception", e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "unknown"));
            Log.CloseFile();
        };

        if (identity.HasWindow)
        {
            // Before any window is created. The cast is how the context handles are
            // spelled - they are sentinel values, not an enum CsWin32 can name.
            PInvoke.SetProcessDpiAwarenessContext((DPI_AWARENESS_CONTEXT)(nint)(-4));

            // Before the config is read, so a colour written `accent` is this machine's.
            SystemColours.Adopt();
        }

        try
        {
            return body(context);
        }
        finally
        {
            Log.CloseFile();
        }
    }

    /// <summary>
    /// Sets up logging from the shared configuration file, then from the command line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The companion reads the <c>logging</c> section of the same file the window
    /// manager does, so turning logging on is one edit rather than two - and, more to
    /// the point, so it is on at all. A companion is normally launched by a startup
    /// command with no arguments, which once meant it had no logging whatsoever: a
    /// question about why the bar looked wrong could not be answered, because the bar
    /// had never written anything down.
    /// </para>
    /// <para>
    /// It writes to its own file, <c>&lt;name&gt;.log</c> beside the window manager's,
    /// never into the window manager's. Two processes cannot share one; the second to
    /// open it truncates the first's.
    /// </para>
    /// <para>
    /// The command line wins over the file - <c>--log-level</c>, <c>--quiet</c>,
    /// <c>--log-file [path]</c> - so a one-off investigation needs no config edit.
    /// </para>
    /// </remarks>
    /// <param name="context">The command line, as <see cref="Run"/> read it.</param>
    /// <param name="identity">Who this is, for the log file's name.</param>
    /// <param name="toFile">
    /// Whether to open the log file at all. A one-shot mode running beside a resident
    /// copy - <c>ayn --report</c> - must not, since opening the file truncates it.
    /// </param>
    public static void ConfigureLogging(CompanionContext context, CompanionIdentity identity, bool toFile)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);

        string file = DefaultLogPath(identity);

        if (ConfigPathResolver.Resolve(context.ConfigPath).Path is { } configPath && File.Exists(configPath))
        {
            try
            {
                ShubbakConfig shared = ConfigLoader.LoadFile(configPath).Config;

                Log.Level = shared.LogLevel;

                if (shared.LogFile is { Length: > 0 } configured)
                    file = Path.Combine(Path.GetDirectoryName(configured) ?? string.Empty, $"{identity.Name}.log");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A companion that cannot read the config still has defaults to run on.
            }
        }

        if (Arguments.Value(context.Args, "--log-level") is { } level)
        {
            if (Log.TryParseLevel(level, out LogLevel parsed))
                Log.Level = parsed;
            else
                Log.Warn(LogCategory.Config, $"--log-level {level} is not a level; use trace, debug, info, warn, error or none");
        }

        // Off unless output genuinely leads somewhere - a console, or a redirect. A
        // companion is normally started from the window manager's startup-command,
        // where these entries were formatted and then discarded on every single one.
        Log.ToConsole = ConsoleHost.HasOutput && !Arguments.Has(context.Args, "--quiet");

        if (Arguments.Has(context.Args, "--log-file"))
            file = Arguments.Value(context.Args, "--log-file") ?? DefaultLogPath(identity);

        if (!toFile) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Log.OpenFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the log is not worth losing the program over. Said where it can be
            // heard, if anywhere.
            if (ConsoleHost.HasOutput)
                Console.Error.WriteLine($"{identity.Name}: could not open log file {file}: {ex.Message}");
        }
    }

    /// <summary>Beside the window manager's log: <c>%LOCALAPPDATA%\Shubbak\&lt;name&gt;.log</c>.</summary>
    public static string DefaultLogPath(CompanionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return Path.Combine(Path.GetDirectoryName(Log.DefaultLogPath)!, $"{identity.Name}.log");
    }
}
