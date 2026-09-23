using System.Globalization;
using Shubbak.Core.Diagnostics;
using Taj.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Taj;

/// <summary>
/// Reads, and changes, the input language of whichever window is in front.
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
/// <para>
/// Changing it goes the same way round: the request is posted to the window in
/// front, whose thread owns the layout, which is what the language switcher does
/// and what <c>DefWindowProc</c> answers. The bar never activates itself, so the
/// window in front when it is clicked is still the one the user was typing into.
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

        uint thread = ThreadOf(foreground);
        if (thread == 0) return string.Empty;

        HKL layout = PInvoke.GetKeyboardLayout(thread);
        if (layout.IsNull) return string.Empty;

        return Code(layout);
    }

    /// <summary>
    /// Switches the foreground window to another installed layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target is worked out here, from the installed list, rather than left to the
    /// <c>HKL_NEXT</c> sentinel: an explicit identifier in the request is the form every
    /// window procedure understands, and it makes "the one after this" mean the same
    /// thing whichever application is in front.
    /// </para>
    /// <para>
    /// Posted rather than sent. The window in front may be busy, and a bar that waits on
    /// it is a bar that hangs with it. The <c>keyboard</c> source reads the result on
    /// its next poll, which is how the indicator follows the switch.
    /// </para>
    /// </remarks>
    /// <returns>Whether a request was posted.</returns>
    public static bool Switch(KeyboardCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        HWND foreground = PInvoke.GetForegroundWindow();

        if (foreground.IsNull)
        {
            Log.Warn(LogCategory.Wm, $"{command}: no window is in front to switch");
            return false;
        }

        HKL[] installed = Installed();

        if (installed.Length == 0)
        {
            Log.Warn(LogCategory.Wm, $"{command}: no keyboard layouts are installed");
            return false;
        }

        uint thread = ThreadOf(foreground);
        HKL current = thread == 0 ? HKL.Null : PInvoke.GetKeyboardLayout(thread);

        HKL? target = command.Change switch
        {
            KeyboardChange.Next => Step(installed, current, +1),
            KeyboardChange.Previous => Step(installed, current, -1),
            _ => Array.Find(installed, l => string.Equals(Code(l), command.Language, StringComparison.OrdinalIgnoreCase)) is { IsNull: false } found
                ? found
                : null,
        };

        if (target is not { } layout)
        {
            Log.Warn(LogCategory.Wm,
                $"{command}: no installed layout is '{command.Language}'; " +
                $"installed: {string.Join(", ", installed.Select(Code))}");
            return false;
        }

        // lParam carries the layout; wParam is informational and DefWindowProc passes
        // it through as activation flags, so nothing is asked for there.
        bool posted = PInvoke.PostMessage(
            foreground, PInvoke.WM_INPUTLANGCHANGEREQUEST, default, new LPARAM(Id(layout)));

        if (!posted)
            Log.Warn(LogCategory.Wm, $"{command}: the window in front would not take the request");
        else if (Log.IsEnabled(LogLevel.Debug))
            Log.Debug(LogCategory.Wm, $"{command}: asked the window in front to switch {Code(current)} -> {Code(layout)}");

        return posted;
    }

    /// <summary>The layout a step away from the current one in the installed list, wrapping.</summary>
    /// <remarks>
    /// The current layout is looked for by exact identifier and then by language alone,
    /// because the identifier a thread reports can name a substitute layout that is not
    /// literally in the list. Not found at all starts from the beginning, so the click
    /// still does something.
    /// </remarks>
    internal static HKL Step(HKL[] installed, HKL current, int direction)
    {
        int index = Array.FindIndex(installed, l => Id(l) == Id(current));

        if (index < 0)
            index = Array.FindIndex(installed, l => LanguageId(l) == LanguageId(current));

        int count = installed.Length;
        int next = index < 0 ? (direction > 0 ? 0 : count - 1) : (((index + direction) % count) + count) % count;

        return installed[next];
    }

    /// <summary>Every layout installed for this session, in the system's order.</summary>
    private static unsafe HKL[] Installed()
    {
        int count = PInvoke.GetKeyboardLayoutList(0, null);
        if (count <= 0) return [];

        var layouts = new HKL[count];

        fixed (HKL* p = layouts)
        {
            int written = PInvoke.GetKeyboardLayoutList(count, p);
            if (written <= 0) return [];
            if (written < count) Array.Resize(ref layouts, written);
        }

        return layouts;
    }

    private static unsafe uint ThreadOf(HWND window) => PInvoke.GetWindowThreadProcessId(window, null);

    /// <summary>The identifier as a number, which is how it travels in a message.</summary>
    private static unsafe nint Id(HKL layout) => (nint)layout.Value;

    /// <summary>
    /// The low word: the language. The high word is the physical layout, which is not
    /// what is being asked about - an English keyboard typing Hebrew is still Hebrew.
    /// </summary>
    private static int LanguageId(HKL layout) => (int)(Id(layout) & 0xFFFF);

    internal static string Code(HKL layout)
    {
        int languageId = LanguageId(layout);

        try
        {
            return new CultureInfo(languageId).TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch (CultureNotFoundException)
        {
            return $"0x{languageId:X4}";
        }
    }
}
