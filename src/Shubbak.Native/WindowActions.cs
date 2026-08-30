using Shubbak.Core.Geometry;
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
    /// Takes the foreground away from every window, giving it to the desktop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For when the window manager has nothing to focus - an empty workspace being
    /// displayed. Doing nothing leaves the system's foreground on whatever had it:
    /// a window just concealed, or one still perfectly visible on another monitor.
    /// Windows then hands it back the moment anything else releases the foreground,
    /// and a launcher opening and closing is enough. That arrives as "go to that
    /// window", moves the point of action to its workspace, and silently undoes the
    /// switch to the empty one.
    /// </para>
    /// <para>
    /// The desktop is the one window that is always present, always visible, and
    /// belongs to no workspace, so parking the foreground there makes the system
    /// agree with the tree instead of contradicting it.
    /// </para>
    /// <para>
    /// GlazeWM does the same thing - its log says "Setting focus to the desktop
    /// window" - when the last window on a workspace closes. It does not do it when
    /// switching to an empty workspace, which is exactly why their issue #997 is
    /// still open with this symptom.
    /// </para>
    /// </remarks>
    public static unsafe bool FocusDesktop()
    {
        HWND shell = PInvoke.GetShellWindow();

        return !shell.IsNull && Focus((nint)shell.Value);
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

    /// <summary>
    /// Drops a window's maximised flag, and tells it where it lives now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A maximised window is drawn by the compositor on the assumption that it fills
    /// the monitor: the shadow is suppressed and part of the frame is deliberately put
    /// off the top of the screen. Move one to half the screen without clearing the flag
    /// and that frame is no longer off the screen - it is a black strip along the top -
    /// and the border colour renders against a frame that is the wrong shape. Windows
    /// that reopen maximised, which is most of the Store applications, arrived that way
    /// and were tiled that way.
    /// </para>
    /// <para>
    /// <c>SetWindowPlacement</c> rather than <c>ShowWindow(SW_RESTORE)</c>, because it
    /// carries the restored rectangle with it. Restoring on its own puts the window
    /// back wherever it was before it was maximised, which is a visible jump to a
    /// stale position immediately before the layout corrects it.
    /// </para>
    /// <para>
    /// Synchronous, and that matters. The committer places windows with a sending
    /// <c>SetWindowPos</c>, and a send overtakes anything merely posted - so the
    /// asynchronous form of this would arrive <i>after</i> the placement and undo it.
    /// A window that is not answering is given the asynchronous form anyway: it cannot
    /// be worse than leaving it maximised, and blocking the whole layout pass on one
    /// stuck application is what the committer already refuses to do elsewhere.
    /// </para>
    /// <para>
    /// The rectangle is converted on the way in, because <c>WINDOWPLACEMENT</c> does
    /// not hold screen coordinates. See <see cref="ToWorkspace"/>.
    /// </para>
    /// </remarks>
    /// <param name="handle">The window.</param>
    /// <param name="restored">
    /// Where it should sit once restored, as a window rectangle in screen coordinates
    /// - shadow included, because that is what <c>SetWindowPos</c> is given.
    /// </param>
    /// <returns>Whether the flag was cleared synchronously.</returns>
    public static unsafe bool Unmaximise(nint handle, Rect restored)
    {
        var hwnd = new HWND(handle);

        if (handle == 0 || !PInvoke.IsWindow(hwnd)) return false;

        if (Win32Window.IsHung(handle))
        {
            PInvoke.ShowWindowAsync(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
            return false;
        }

        Rect workspace = ToWorkspace(restored, WorkspaceOrigin());

        var placement = new WINDOWPLACEMENT
        {
            length = (uint)sizeof(WINDOWPLACEMENT),
            showCmd = SHOW_WINDOW_CMD.SW_SHOWNORMAL,
            rcNormalPosition = new RECT
            {
                left = workspace.X,
                top = workspace.Y,
                right = workspace.X + workspace.Width,
                bottom = workspace.Y + workspace.Height,
            },
        };

        return PInvoke.SetWindowPlacement(hwnd, in placement);
    }

    /// <summary>
    /// Converts a screen rectangle to the workspace coordinates
    /// <c>WINDOWPLACEMENT</c> is expressed in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>rcNormalPosition</c> is not in screen coordinates, and passing screen
    /// coordinates to it is a documented mistake with a name: the window "creeps" by
    /// the height of whatever is docked at the top of the display, every time it is
    /// restored. Measured here with Taj on the top edge, a window asked to restore to
    /// y=400 arrived at y=434 - the bar's 34 pixels, exactly.
    /// </para>
    /// <para>
    /// The damage was limited, which is why this survived: the committer moves the
    /// window with <c>SetWindowPos</c> immediately afterwards, and that both corrects
    /// the position and rewrites <c>rcNormalPosition</c> properly, so the window ends
    /// up in the right place and later restores work. What was left was a single frame
    /// of the window drawn a bar's height too low, which is precisely the visible jump
    /// this call carries a rectangle in order to avoid.
    /// </para>
    /// <para>
    /// Kept separate from the call so the arithmetic can be tested without a window
    /// and without a desktop that happens to have something docked at the top.
    /// </para>
    /// </remarks>
    /// <param name="screen">The rectangle in screen coordinates.</param>
    /// <param name="origin">
    /// The upper-left corner of the workspace area, in screen coordinates.
    /// </param>
    public static Rect ToWorkspace(Rect screen, (int X, int Y) origin) =>
        screen.Translate(-origin.X, -origin.Y);

    /// <summary>
    /// Where workspace coordinate (0,0) is, in screen coordinates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SPI_GETWORKAREA</c> rather than the work area of the monitor the window is
    /// on. The documentation describes a single workspace origin - "the upper-left
    /// corner of the workspace area, the area of the screen not being used by
    /// application toolbars" - not one per display, and this is the call that reports
    /// it. Measured against a real placement it agreed exactly.
    /// </para>
    /// <para>
    /// Should a future Windows turn out to apply this per monitor, a machine whose
    /// displays reserve different amounts at the top would be wrong here by the
    /// difference. It would still be less wrong than not converting at all, which is
    /// wrong by the whole reservation on every machine that has one.
    /// </para>
    /// <para>
    /// Read each time rather than cached. A bar starting, stopping or changing height
    /// moves it, and this runs only for a window that is both maximised and being
    /// placed - rare enough that one system call costs nothing.
    /// </para>
    /// </remarks>
    private static unsafe (int X, int Y) WorkspaceOrigin()
    {
        RECT area = default;

        bool ok = PInvoke.SystemParametersInfo(
            SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETWORKAREA, 0, &area, 0);

        // A failure means the question could not be asked. Treating the origin as the
        // top-left of the screen is what the old code did unconditionally, so falling
        // back to it cannot make anything worse than it already was.
        return ok ? (area.left, area.top) : (0, 0);
    }

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
