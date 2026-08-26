using Microsoft.Win32;

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
/// <b>Why the <c>Run</c> key and not the alternatives.</b> The Startup folder needs a
/// shortcut, which is a binary file this would have to author through COM. Task
/// Scheduler can start a process before the shell is ready and needs either elevation
/// or an XML definition. The <c>Run</c> key is one string under <c>HKCU</c>, needs no
/// privileges, is inspectable with any registry editor, and is removed by writing
/// nothing - which also means an uninstall that misses it leaves one dangling value
/// rather than a scheduled task nobody can find.
/// </para>
/// <para>
/// <b>Why this lives in the CLI.</b> <c>shubbak-wm</c> is a GUI-subsystem binary with
/// no console; a command whose entire output is text belongs in the console binary.
/// The daemon is named rather than assumed - see <see cref="FindDaemon"/> - so the
/// registration records where the daemon actually is, not where the CLI is.
/// </para>
/// </remarks>
internal static class Autostart
{
    /// <summary>Where Windows looks for per-user startup commands.</summary>
    /// <remarks>
    /// <c>HKCU</c> rather than <c>HKLM</c>: a window manager is a property of a user's
    /// session, not of the machine, and <c>HKLM</c> would need elevation to write.
    /// </remarks>
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value name, which is also what Task Manager's Startup tab shows.</summary>
    private const string ValueName = "Shubbak";

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
        string command = BuildCommand(daemon, extra);

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException($"could not open HKCU\\{RunKey}");

            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"shubbak: could not write the startup entry: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Shubbak will start at logon: {command}");
        Console.WriteLine();
        Console.WriteLine("This records the path as it is now. Moving the binaries means");
        Console.WriteLine("running `shubbak autostart enable` again from the new location.");
        return 0;
    }

    private static int Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);

            if (key?.GetValue(ValueName) is null)
            {
                Console.WriteLine("Shubbak was not set to start at logon. Nothing to do.");
                return 0;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
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
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            command = key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"shubbak: could not read the startup entry: {ex.Message}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(command))
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

        if (FindDaemon() is { } current
            && !string.Equals(
                Path.GetFullPath(current), Path.GetFullPath(registered), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine("warning: this is not the copy that will start at logon.");
            Console.WriteLine($"         registered: {registered}");
            Console.WriteLine($"         this one:   {current}");
            Console.WriteLine("         Re-run `shubbak autostart enable` to point it here.");
        }

        return 0;
    }

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
    private static string? FindDaemon()
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

    /// <summary>Builds the command line the Run key will hold.</summary>
    /// <remarks>
    /// The executable is always quoted. An unquoted path containing a space is read by
    /// Windows as a command plus arguments, so an install under
    /// <c>C:\Program Files\Shubbak</c> would try to run <c>C:\Program</c> - the classic
    /// unquoted-service-path bug, silent until the day someone installs to the
    /// default location.
    /// </remarks>
    internal static string BuildCommand(string daemon, string[] extra)
    {
        string command = '"' + daemon + '"';

        foreach (string argument in extra)
            command += argument.Contains(' ', StringComparison.Ordinal)
                ? " \"" + argument + '"'
                : " " + argument;

        return command;
    }

    /// <summary>Extracts the executable from a stored command line.</summary>
    internal static string ExecutableFrom(string command)
    {
        command = command.Trim();

        if (command.StartsWith('"'))
        {
            int closing = command.IndexOf('"', 1);
            if (closing > 0) return command[1..closing];
        }

        int space = command.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? command : command[..space];
    }
}
