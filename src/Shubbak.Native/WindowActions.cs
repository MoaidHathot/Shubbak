using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.Graphics.Dwm;
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

    /// <summary><c>DWMWA_BORDER_COLOR</c>, Windows 11 build 22000 and later.</summary>
    private const uint DwmwaBorderColour = 34;

    /// <summary><c>DWMWA_COLOR_DEFAULT</c> - restore the system's border colour.</summary>
    private const uint DwmBorderColourDefault = 0xFFFFFFFF;

    /// <summary>
    /// Draws a coloured border around a window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How the focused window is marked. The compositor draws it on the window's own
    /// frame, so there is no overlay window to keep positioned, nothing to fight the
    /// z-order, and no flicker when windows move - which is why GlazeWM uses the same
    /// mechanism and why every approach involving a separate always-on-top window
    /// ends up worse.
    /// </para>
    /// <para>
    /// Windows 11 only. On Windows 10 the call fails harmlessly and windows simply
    /// have no border, which is a reasonable outcome rather than an error worth
    /// reporting on every focus change.
    /// </para>
    /// </remarks>
    /// <param name="handle">The window.</param>
    /// <param name="red">Red channel.</param>
    /// <param name="green">Green channel.</param>
    /// <param name="blue">Blue channel.</param>
    public static unsafe bool SetBorderColour(nint handle, byte red, byte green, byte blue)
    {
        // COLORREF byte order: 0x00BBGGRR.
        uint colour = (uint)(red | (green << 8) | (blue << 16));

        HRESULT hr = PInvoke.DwmSetWindowAttribute(
            new HWND(handle), (DWMWINDOWATTRIBUTE)DwmwaBorderColour, &colour, sizeof(uint));

        return hr.Succeeded;
    }

    /// <summary>Restores a window's default border colour.</summary>
    public static unsafe bool ClearBorderColour(nint handle)
    {
        uint colour = DwmBorderColourDefault;

        HRESULT hr = PInvoke.DwmSetWindowAttribute(
            new HWND(handle), (DWMWINDOWATTRIBUTE)DwmwaBorderColour, &colour, sizeof(uint));

        return hr.Succeeded;
    }
}
