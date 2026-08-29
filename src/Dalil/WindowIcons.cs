using System.Collections.Concurrent;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dalil;

/// <summary>
/// The application icon for a window, remembered.
/// </summary>
/// <remarks>
/// <para>
/// An icon is recognised before a word is read, and the window list is scanned rather
/// than read - so this is the single largest difference to how quickly the right row
/// is found, and it costs nothing to look at.
/// </para>
/// <para>
/// It does not cost nothing to fetch. <c>WM_GETICON</c> is a synchronous message to
/// another process, which means a window belonging to an application that has stopped
/// pumping messages will not answer until the timeout expires. Twelve of those on a
/// paint is a third of a second of a window that exists to feel instant, so nothing
/// here is ever called from the paint path: the handles are resolved on the background
/// thread that already fetches the window list, and drawing is a dictionary read that
/// cannot block and cannot fail.
/// </para>
/// <para>
/// The handles are not owned. A class icon belongs to the application that registered
/// the class and a window icon belongs to the window; destroying either would be
/// destroying somebody else's resource, and both outlive anything this process does
/// with them. So there is nothing to release and no lifetime to manage - only a
/// dictionary to keep from growing for ever.
/// </para>
/// </remarks>
internal static class WindowIcons
{
    /// <summary>How long to wait for one application to answer.</summary>
    /// <remarks>
    /// Short, and it is a ceiling rather than a cost: a responsive application answers
    /// in microseconds because the message is handled by <c>DefWindowProc</c> without
    /// ever reaching its code. The timeout exists only to bound what a hung one can do
    /// to the priming pass, and a missing icon is a much smaller problem than a
    /// palette that takes a second to fill in.
    /// </remarks>
    private const uint AnswerTimeoutMs = 40;

    /// <summary>
    /// How many windows to remember icons for.
    /// </summary>
    /// <remarks>
    /// Dalil is resident for the length of a login session, and windows open and close
    /// all day. Without a bound this is a slow leak of one dictionary entry per window
    /// ever seen - not large, and not something that should be discovered after a
    /// fortnight of uptime.
    /// </remarks>
    private const int Capacity = 512;

    private static readonly ConcurrentDictionary<long, nint> s_icons = new();

    private const int IconSmall = 0;
    private const int IconBig = 1;

    /// <summary>The one Windows synthesises from the big icon when there is no small one.</summary>
    private const int IconSmall2 = 2;

    /// <summary>
    /// Works out the icons for a set of windows, on whatever thread calls this.
    /// </summary>
    /// <remarks>
    /// Must not be the message loop. Everything about the timing here assumes a caller
    /// with nothing waiting on it, which is why it is invoked from the same background
    /// task that reads the window list rather than from anywhere near a repaint.
    /// </remarks>
    internal static void Prime(IEnumerable<long> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);

        // Cleared wholesale rather than evicted one at a time. There is no useful
        // recency information here - every entry is equally cheap to rebuild and the
        // next priming pass refills whatever is still on screen - so a policy would be
        // more code than it could possibly save.
        if (s_icons.Count > Capacity) s_icons.Clear();

        foreach (long handle in handles)
        {
            if (s_icons.ContainsKey(handle)) continue;

            s_icons[handle] = Resolve(handle);
        }
    }

    /// <summary>
    /// The icon for a window, or zero.
    /// </summary>
    /// <remarks>
    /// A pure read. Zero means either "no icon" or "not looked up yet", and the
    /// difference does not matter to a caller whose only options are to draw one or
    /// not - so both are answered without asking Windows anything, which is what makes
    /// this safe to call while painting.
    /// </remarks>
    internal static nint Get(long handle) =>
        s_icons.TryGetValue(handle, out nint icon) ? icon : 0;

    /// <summary>Forgets everything, for a configuration reload that turned icons off.</summary>
    internal static void Clear() => s_icons.Clear();

    /// <summary>
    /// Asks a window for its icon, in descending order of how good the answer is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window first and the class second, because that is the order they take
    /// effect in: an application that calls <c>WM_SETICON</c> is deliberately
    /// overriding whatever its class says, usually per document or per profile, and the
    /// class icon is the fallback the system itself uses.
    /// </para>
    /// <para>
    /// <c>ICON_SMALL2</c> before <c>ICON_SMALL</c>: it is the one Windows will
    /// synthesise by downscaling the big icon when the application never supplied a
    /// small one, which is most of them.
    /// </para>
    /// </remarks>
    private static nint Resolve(long handle)
    {
        var window = new HWND((nint)handle);

        if (!PInvoke.IsWindow(window)) return 0;

        foreach (int which in (ReadOnlySpan<int>)[IconSmall2, IconSmall, IconBig])
        {
            if (Ask(window, which) is var icon && icon != 0) return icon;
        }

        foreach (GET_CLASS_LONG_INDEX which in
                 (ReadOnlySpan<GET_CLASS_LONG_INDEX>)
                 [GET_CLASS_LONG_INDEX.GCLP_HICONSM, GET_CLASS_LONG_INDEX.GCLP_HICON])
        {
            // A plain read of the window class structure. It cannot block, cannot
            // reach another process's code, and is the reason a hung application still
            // usually manages to show an icon.
            nint icon = (nint)PInvoke.GetClassLongPtr(window, which);

            if (icon != 0) return icon;
        }

        return 0;
    }

    /// <summary>One <c>WM_GETICON</c>, bounded and never fatal.</summary>
    private static unsafe nint Ask(HWND window, int which)
    {
        nuint result = 0;

        LRESULT sent = PInvoke.SendMessageTimeout(
            window,
            PInvoke.WM_GETICON,
            new WPARAM((nuint)which),
            new LPARAM(0),

            // ABORTIFHUNG returns immediately for a window already known to be
            // unresponsive rather than waiting out the full timeout for it, which is
            // what keeps a priming pass over a desktop with one dead application from
            // taking as long as the desktop is wide.
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK,
            AnswerTimeoutMs,
            &result);

        return sent == 0 ? 0 : (nint)result;
    }
}
