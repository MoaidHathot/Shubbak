using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>
/// Tells a person something when there is no terminal to tell them in.
/// </summary>
/// <remarks>
/// <para>
/// One caller, so far: <c>shubbak setup</c> when the installer runs it. The installer
/// runs it hidden, with its output captured into the installer's log, and that log is
/// written only when somebody asked for one - so a failure there, left to standard
/// error, is a failure nobody is told about. The installer's own way of saying it, a
/// failed custom action, reports "a program run as part of the setup did not finish as
/// expected" and marks the whole installation failed, for a window manager that is
/// installed and merely not yet running. A plain message box naming the problem and
/// the remedy is the honest size of the news.
/// </para>
/// <para>
/// Modal to nothing and set to the foreground, since the process showing it has no
/// window of its own and is about to exit; without the flag the box can open behind
/// the installer and go unseen, which is the failure being fixed.
/// </para>
/// </remarks>
public static class UserNotice
{
    /// <summary>Shows a message with an error icon and an OK button, and waits for the click.</summary>
    public static void ShowError(string title, string text)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);

        PInvoke.MessageBox(
            HWND.Null,
            text,
            title,
            MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONERROR | MESSAGEBOX_STYLE.MB_SETFOREGROUND);
    }
}
