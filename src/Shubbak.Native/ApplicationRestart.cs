using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.System.Recovery;

namespace Shubbak.Native;

/// <summary>
/// Asks Windows to start this process again after an installer has closed it.
/// </summary>
/// <remarks>
/// <para>
/// A silent installer - which is what <c>winget upgrade</c> runs - uses Restart
/// Manager to close whatever holds the files it is replacing, and Restart Manager
/// starts again, afterwards, exactly those applications that asked to be. This is the
/// asking. Without it an upgrade leaves the desktop with no window manager until the
/// next logon, which from the user's side is indistinguishable from the upgrade
/// having broken something.
/// </para>
/// <para>
/// Only that case. The flags rule out a restart after a crash, a hang or a reboot: a
/// crash restart would hide a crash loop behind a flicker, and a reboot restart would
/// race the Run key that already starts the daemon at logon - the loser opens a
/// console to say another window manager took the desktop first, which is the right
/// message at the wrong moment. The bar, the palette and the watcher do not register
/// at all; the window manager starts them from the config, so they come back with it.
/// </para>
/// </remarks>
public static class ApplicationRestart
{
    /// <summary>The most Windows will keep for the command line, in characters.</summary>
    private const int MaxCommandLine = 2048;

    /// <summary>
    /// Registers for a restart with the given arguments - not the executable, which
    /// Windows already knows.
    /// </summary>
    /// <returns>Whether Windows accepted the registration.</returns>
    public static bool Register(IEnumerable<string> arguments)
    {
        string commandLine = string.Join(' ', arguments.Select(Quote));

        if (commandLine.Length >= MaxCommandLine)
        {
            Log.Warn(LogCategory.Wm, "the command line is too long to register for restart after an upgrade; not registering");
            return false;
        }

        REGISTER_APPLICATION_RESTART_FLAGS flags =
            REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_CRASH |
            REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_HANG |
            REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_REBOOT;

        int result = PInvoke.RegisterApplicationRestart(commandLine, flags);

        if (result != 0)
        {
            Log.Warn(LogCategory.Wm, $"RegisterApplicationRestart failed: 0x{result:X8}; the window manager will not come back by itself after an upgrade");
            return false;
        }

        Log.Debug(LogCategory.Wm, commandLine.Length == 0
            ? "registered to restart after an installer closes this process"
            : $"registered to restart after an installer closes this process, with: {commandLine}");
        return true;
    }

    /// <summary>
    /// Quotes an argument the way <c>CommandLineToArgvW</c> will read it back.
    /// </summary>
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\"") < 0)
            return argument;

        var builder = new System.Text.StringBuilder(argument.Length + 2);
        builder.Append('"');

        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Backslashes before a quote are doubled, then the quote is escaped.
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(c);
            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled too.
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
