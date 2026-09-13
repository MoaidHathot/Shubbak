using Microsoft.Win32;

namespace Shubbak.Native;

/// <summary>
/// The per-user <c>Run</c> key entry that starts the window manager at logon.
/// </summary>
/// <remarks>
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
/// Shared between the CLI's <c>autostart</c> command and the window manager's own
/// <c>--autostart</c> switch, so the two cannot write different things. The switch
/// exists for the installer's "start now" checkbox, which launches the window manager
/// directly - there is no terminal for a CLI to print into - and for the person who
/// wants one command to do both.
/// </para>
/// </remarks>
public static class RunKey
{
    /// <summary>Where Windows looks for per-user startup commands.</summary>
    /// <remarks>
    /// <c>HKCU</c> rather than <c>HKLM</c>: a window manager is a property of a user's
    /// session, not of the machine, and <c>HKLM</c> would need elevation to write.
    /// </remarks>
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value name, which is also what Task Manager's Startup tab shows.</summary>
    private const string ValueName = "Shubbak";

    /// <summary>
    /// Registers a command to run at logon, replacing any previous one.
    /// </summary>
    /// <returns>The command line written.</returns>
    /// <exception cref="UnauthorizedAccessException">The key could not be written.</exception>
    /// <exception cref="IOException">The key could not be written.</exception>
    public static string Register(string executable, IEnumerable<string> arguments)
    {
        string command = BuildCommand(executable, arguments);

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(Key, writable: true)
            ?? throw new IOException($"could not open HKCU\\{Key}");

        key.SetValue(ValueName, command, RegistryValueKind.String);
        return command;
    }

    /// <summary>Removes the entry. Harmless when there is none.</summary>
    /// <returns>Whether there was one to remove.</returns>
    public static bool Unregister()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Key, writable: true);

        if (key?.GetValue(ValueName) is null) return false;

        key.DeleteValue(ValueName, throwOnMissingValue: false);
        return true;
    }

    /// <summary>The command line currently registered, or null when there is none.</summary>
    public static string? Registered()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Key);
        string? command = key?.GetValue(ValueName) as string;
        return string.IsNullOrWhiteSpace(command) ? null : command;
    }

    /// <summary>Builds the command line the Run key will hold.</summary>
    /// <remarks>
    /// The executable is always quoted. An unquoted path containing a space is read by
    /// Windows as a command plus arguments, so an install under
    /// <c>C:\Program Files\Shubbak</c> would try to run <c>C:\Program</c> - the classic
    /// unquoted-service-path bug, silent until the day someone installs to the
    /// default location.
    /// </remarks>
    public static string BuildCommand(string executable, IEnumerable<string> arguments)
    {
        string command = '"' + executable + '"';

        foreach (string argument in arguments)
            command += argument.Contains(' ', StringComparison.Ordinal)
                ? " \"" + argument + '"'
                : " " + argument;

        return command;
    }

    /// <summary>Extracts the executable from a stored command line.</summary>
    public static string ExecutableFrom(string command)
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

    /// <summary>
    /// Whether two paths name the same executable, following symbolic links.
    /// </summary>
    /// <remarks>
    /// winget's portable install puts the executables in one directory and a symlink
    /// to each in another that is on PATH, so the registered path and the running
    /// copy can differ as strings while being one file. Comparing the strings said
    /// "this is not the copy that will start at logon" to somebody whose install was
    /// entirely in order, which is the kind of warning that teaches people to ignore
    /// warnings.
    /// </remarks>
    public static bool SameFile(string left, string right)
    {
        if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(FinalTarget(left), FinalTarget(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string FinalTarget(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }
}
