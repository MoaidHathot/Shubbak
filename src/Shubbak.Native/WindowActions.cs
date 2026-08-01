using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>
/// Window operations that change focus or ask a window to close.
/// </summary>
/// <remarks>
/// Separated from <see cref="WindowCommitter"/> because these are one-shot actions
/// rather than part of a layout frame, and because focus in particular needs
/// workarounds that have nothing to do with geometry.
/// </remarks>
public static class WindowActions
{
    /// <summary>
    /// Gives a window keyboard focus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SetForegroundWindow</c> is heavily restricted: Windows refuses it unless
    /// the calling thread already owns the foreground, to stop applications stealing
    /// focus. A window manager legitimately needs to do exactly that, so it uses the
    /// documented workaround of temporarily attaching its input queue to the current
    /// foreground thread's, which makes the call succeed.
    /// </para>
    /// <para>
    /// The attachment is always undone, including on failure - leaving input queues
    /// attached couples the two threads' input state and causes symptoms that look
    /// like random keyboard freezes.
    /// </para>
    /// </remarks>
    public static unsafe bool Focus(nint handle)
    {
        var hwnd = new HWND(handle);
        if (handle == 0 || !PInvoke.IsWindow(hwnd)) return false;

        if (Win32Window.IsMinimised(handle))
            PInvoke.ShowWindowAsync(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);

        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground == hwnd) return true;

        uint ours = PInvoke.GetCurrentThreadId();
        uint theirs = foreground.IsNull ? 0 : Win32Window.GetThreadId((nint)foreground.Value);

        bool attached = false;

        try
        {
            if (theirs != 0 && theirs != ours)
                attached = PInvoke.AttachThreadInput(ours, theirs, true);

            PInvoke.BringWindowToTop(hwnd);
            return PInvoke.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) PInvoke.AttachThreadInput(ours, theirs, false);
        }
    }

    /// <summary>
    /// Asks a window to close.
    /// </summary>
    /// <remarks>
    /// Posts <c>WM_CLOSE</c> rather than terminating: the window may have unsaved
    /// work and is entitled to prompt, and it may legitimately refuse. The tree is
    /// not touched here - the window leaves only when the OS reports it destroyed.
    /// </remarks>
    public static void Close(nint handle)
    {
        var hwnd = new HWND(handle);
        if (handle == 0 || !PInvoke.IsWindow(hwnd)) return;

        PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, default, default);
    }

    public static void Minimise(nint handle) =>
        PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_MINIMIZE);

    public static void Restore(nint handle) =>
        PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_RESTORE);

    public static void Maximise(nint handle) =>
        PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_MAXIMIZE);

    /// <summary>Adds or removes the always-on-top band.</summary>
    public static void SetAlwaysOnTop(nint handle, bool onTop)
    {
        // HWND_TOPMOST = -1, HWND_NOTOPMOST = -2. Sentinels rather than real handles.
        var band = new HWND(onTop ? -1 : -2);

        PInvoke.SetWindowPos(
            new HWND(handle), band, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>True while the given virtual-key code is held down.</summary>
    public static bool IsKeyDown(int virtualKey) =>
        (PInvoke.GetKeyState(virtualKey) & 0x8000) != 0;
}
