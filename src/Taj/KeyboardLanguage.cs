using System.Globalization;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Taj;

/// <summary>
/// Reads the input language of whichever window is in front.
/// </summary>
/// <remarks>
/// <para>
/// Per window, not per system. Windows tracks the keyboard layout per input-processing
/// thread, so the answer depends on what is focused - which is exactly what the user
/// wants shown, since it is the thing that decides what typing produces.
/// </para>
/// <para>
/// Polled rather than pushed. There is no cross-process notification when the layout
/// changes: <c>WM_INPUTLANGCHANGE</c> reaches only the window that changed, and no
/// accessibility event covers it. Reading the layout is two cheap calls, so a poll a
/// few times a second costs nothing measurable and catches the language switcher, a
/// per-application layout, and focus moving between the two.
/// </para>
/// </remarks>
internal static class KeyboardLanguage
{
    /// <summary>
    /// The two-letter code for the foreground window's input language, upper case.
    /// </summary>
    /// <remarks>
    /// Two letters because the bar has no room for more and the distinction that
    /// matters is which script is about to come out of the keyboard. Falls back to the
    /// hexadecimal language id when the identifier is unknown, which beats showing
    /// nothing and says enough to look up.
    /// </remarks>
    public static string Current()
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull) return string.Empty;

        uint thread;

        unsafe
        {
            thread = PInvoke.GetWindowThreadProcessId(foreground, null);
        }

        if (thread == 0) return string.Empty;

        nint layout = PInvoke.GetKeyboardLayout(thread);
        if (layout == 0) return string.Empty;

        // The low word is the language identifier; the high word is the physical
        // layout, which is not what is being asked about - an English keyboard typing
        // Hebrew is still Hebrew.
        int languageId = (int)(layout & 0xFFFF);

        try
        {
            var culture = new CultureInfo(languageId);

            return culture.TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch (CultureNotFoundException)
        {
            return $"0x{languageId:X4}";
        }
    }
}
