using System.Collections.Concurrent;
using Shubbak.Ui.Layout;

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
/// It used to be fetched by the palette itself: a <c>WM_GETICON</c> to each window,
/// which is a synchronous message into another process, with a timeout to bound what
/// a hung one could do to the priming pass and no answer at all for a window that had
/// never set an icon. The window manager now answers <c>window-icon</c> over the pipe
/// for any client, asking the window as the taskbar does, then its class, then the
/// executable - so a window that set none still shows the icon its taskbar button
/// does - and remembering the answer for everyone who asks. The palette asks it, once
/// per window it has not seen, on the background thread that already reads the list,
/// and the pixels it gets back are drawn without any process being asked anything.
/// </para>
/// <para>
/// Never called from the paint path. Drawing is a dictionary read that cannot block
/// and cannot fail; the fetch happens where the list is fetched, and the window
/// manager's own cache makes the second open of a session a few sub-millisecond
/// answers rather than a message per window.
/// </para>
/// </remarks>
internal static class WindowIcons
{
    /// <summary>
    /// How many windows to remember icons for.
    /// </summary>
    /// <remarks>
    /// Dalil is resident for the length of a login session, and windows open and close
    /// all day. Without a bound this is a slow leak of a few kilobytes per window ever
    /// seen - not large, and not something that should be discovered after a fortnight
    /// of uptime. Cleared wholesale rather than evicted one at a time: every entry is
    /// equally cheap to rebuild and the next priming pass refills whatever is still on
    /// screen, so a policy would be more code than it could possibly save.
    /// </remarks>
    private const int Capacity = 512;

    /// <summary>
    /// The size asked for. A hint to the window manager about which variant to prefer;
    /// the renderer resamples whatever comes to the row's icon square, which at an
    /// ordinary scale is smaller than this and at a high one is not.
    /// </summary>
    private const int PreferredSize = 32;

    private static readonly ConcurrentDictionary<long, ImageBitmap?> s_icons = new();

    /// <summary>
    /// Fetches the icons of the windows that have none remembered yet.
    /// </summary>
    /// <remarks>
    /// Must not be the message loop. Everything about the timing here assumes a caller
    /// with nothing waiting on it, which is why it is invoked from the same background
    /// task that reads the window list rather than from anywhere near a repaint. A
    /// window the window manager has no icon for is remembered as such, or it would be
    /// asked about on every open for as long as it lived.
    /// </remarks>
    internal static async Task PrimeAsync(WmConnection connection, IReadOnlyList<long> handles)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(handles);

        if (s_icons.Count > Capacity) s_icons.Clear();

        List<long> missing = [];

        foreach (long handle in handles)
            if (!s_icons.ContainsKey(handle) && !missing.Contains(handle)) missing.Add(handle);

        if (missing.Count == 0) return;

        IReadOnlyDictionary<long, ImageBitmap?> read =
            await connection.ReadIconsAsync(missing, PreferredSize).ConfigureAwait(false);

        foreach ((long handle, ImageBitmap? image) in read)
            s_icons[handle] = image;
    }

    /// <summary>
    /// The icon for a window, or null.
    /// </summary>
    /// <remarks>
    /// A pure read. Null means either "no icon" or "not looked up yet", and the
    /// difference does not matter to a caller whose only options are to draw one or
    /// not - so both are answered without asking anyone anything, which is what makes
    /// this safe to call while painting.
    /// </remarks>
    internal static ImageBitmap? Get(long handle) =>
        s_icons.TryGetValue(handle, out ImageBitmap? icon) ? icon : null;

    /// <summary>Forgets everything, for a configuration reload that turned icons off.</summary>
    internal static void Clear() => s_icons.Clear();
}
