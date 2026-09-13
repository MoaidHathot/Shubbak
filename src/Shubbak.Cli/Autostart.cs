using Shubbak.Native;

namespace Shubbak.Cli;

/// <summary>
/// Registers the window manager to start when the user logs in.
/// </summary>
/// <remarks>
/// <para>
/// Shubbak had no way to start itself. <c>startup-command</c> launches other
/// programs once the daemon is already up, which covers the bar and the palette but
/// cannot cover the thing running it - so every user had to discover the
/// <c>Run</c> key, the Startup folder or Task Scheduler for themselves, and the
/// answers people arrived at differed in ways that mattered.
/// </para>
/// <para>
/// The registry work is <see cref="RunKey"/>, shared with the window manager's own
/// <c>--autostart</c> switch. What is here is the command line around it: finding
/// the daemon, the messages, and <c>status</c>, which is the part that needs a
/// console. The daemon is named rather than assumed - see <see cref="FindDaemon"/> -
/// so the registration records where the daemon actually is, not where the CLI is.
/// </para>
/// </remarks>
internal static class Autostart
{
    private const string DaemonExe = "shubbak-wm.exe";

    public static int Run(string[] args)
    {
        string action = args.Length > 1 ? args[1] : "status";

        return action switch
        {
            "enable" or "on" => Enable(args),
            "disable" or "off" => Disable(),
            "status" => Status(),
            _ => Unknown(action),
        };
    }

    private static int Unknown(string action)
    {
        Console.Error.WriteLine($"shubbak: unknown autostart action '{action}'.");
        Console.Error.WriteLine("hint: enable, disable or status");
        return 1;
    }

    private static int Enable(string[] args)
    {
        if (FindDaemon() is not { } daemon)
        {
            Console.Error.WriteLine($"shubbak: could not find {DaemonExe}.");
            Console.Error.WriteLine(
                "hint: it is normally beside shubbak.exe. Put the install directory " +
                "on PATH, or run this from there.");
            return 1;
        }

        // Anything after the action is passed through to the daemon, so that a
        // non-standard config can be made to survive a reboot without hand-editing
        // the registry:  shubbak autostart enable --config D:\dotfiles\shubbak.kdl
        string[] extra = args.Length > 2 ? args[2..] : [];

        if (Register(daemon, extra) is not { } command) return 1;

        Console.WriteLine($"Shubbak will start at logon: {command}");
        Console.WriteLine();
        Console.WriteLine("This records the path as it is now. Moving the binaries means");
        Console.WriteLine("running `shubbak autostart enable` again from the new location.");
        return 0;
    }

    /// <summary>
    /// Writes the Run key entry and returns the command it holds, or null with the
    /// reason already printed.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="Enable"/> so that <c>shubbak setup</c> can register
    /// the daemon as one step of several without inheriting the paragraph of advice
    /// the standalone command prints.
    /// </remarks>
    internal static string? Register(string daemon, string[] extra)
    {
        try
        {
            return RunKey.Register(daemon, extra);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"shubbak: could not write the startup entry: {ex.Message}");
            return null;
        }
    }

    /// <summary>The command the Run key currently holds, or null when there is none.</summary>
    internal static string? Registered()
    {
        try
        {
            return RunKey.Registered();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static int Disable()
    {
        try
        {
            if (!RunKey.Unregister())
            {
                Console.WriteLine("Shubbak was not set to start at logon. Nothing to do.");
                return 0;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"shubbak: could not remove the startup entry: {ex.Message}");
            return 1;
        }

        Console.WriteLine("Shubbak will no longer start at logon.");
        return 0;
    }

    /// <summary>
    /// Reports whether autostart is registered, and whether it still points anywhere.
    /// </summary>
    /// <remarks>
    /// The two failure modes worth naming are both silent otherwise: a registration
    /// left behind by binaries that have since been deleted or moved, and one that
    /// points at a different copy of Shubbak than the one being run now. Either makes
    /// "I updated it but the old version keeps starting" the symptom, and neither is
    /// visible without comparing the two paths.
    /// </remarks>
    private static int Status()
    {
        string? command;

        try
        {
            command = RunKey.Registered();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"shubbak: could not read the startup entry: {ex.Message}");
            return 1;
        }

        if (command is null)
        {
            Console.WriteLine("Not set to start at logon.");
            Console.WriteLine("hint: shubbak autostart enable");
            return 0;
        }

        Console.WriteLine($"Starts at logon: {command}");

        string registered = ExecutableFrom(command);

        if (!File.Exists(registered))
        {
            Console.WriteLine();
            Console.WriteLine($"warning: {registered} does not exist.");
            Console.WriteLine("         Shubbak will not start. Re-run `shubbak autostart enable`.");
            return 1;
        }

        if (FindDaemon() is { } current && !SameFile(current, registered))
        {
            Console.WriteLine();
            Console.WriteLine("warning: this is not the copy that will start at logon.");
            Console.WriteLine($"         registered: {registered}");
            Console.WriteLine($"         this one:   {current}");
            Console.WriteLine("         Re-run `shubbak autostart enable` to point it here.");
        }

        return 0;
    }

    /// <inheritdoc cref="RunKey.SameFile"/>
    internal static bool SameFile(string left, string right) => RunKey.SameFile(left, right);

    /// <inheritdoc cref="RunKey.BuildCommand"/>
    internal static string BuildCommand(string daemon, string[] extra) => RunKey.BuildCommand(daemon, extra);

    /// <inheritdoc cref="RunKey.ExecutableFrom"/>
    internal static string ExecutableFrom(string command) => RunKey.ExecutableFrom(command);

    /// <summary>
    /// Finds the daemon beside this executable, then on <c>PATH</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Beside first, because that is where every supported install puts it and it is
    /// the answer that stays right when two copies exist. <c>PATH</c> second, because
    /// a package manager that exposes each binary through its own symlink directory
    /// leaves them without a shared parent.
    /// </para>
    /// <para>
    /// <see cref="Environment.ProcessPath"/> rather than <c>Assembly.Location</c>,
    /// which is empty under NativeAOT - which is how Shubbak ships.
    /// </para>
    /// </remarks>
    internal static string? FindDaemon()
    {
        if (Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } directory)
        {
            string sibling = Path.Combine(directory, DaemonExe);
            if (File.Exists(sibling)) return sibling;
        }

        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;

            try
            {
                candidate = Path.Combine(entry, DaemonExe);
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid characters. Someone else's problem.
                continue;
            }

            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
