using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>
/// A window of Shubbak's own that holds the keyboard when there is nothing else to
/// hold it.
/// </summary>
/// <remarks>
/// <para>
/// Displaying an empty workspace leaves the tree with no focused window, and the
/// system's foreground has to be somewhere. Wherever it is left, Windows hands it
/// back the moment whatever takes it next lets go - and what takes it next, on an
/// empty workspace, is a launcher. The launcher hides itself and Windows activates
/// the window that was active before it. If that is a window on the other monitor,
/// the point of action moves there, and the application the launcher started opens
/// on the wrong display.
/// </para>
/// <para>
/// The desktop was tried first and does not work: it is never chosen as the
/// fallback, so parking the foreground on it only guarantees that Windows will pick
/// some application window instead. Measured, cross-process, on Windows 11 - and the
/// measurement also said what does work. The fallback prefers the window that was
/// active before the one going away, wherever it is in the stacking order, and only
/// then walks the order from the top. A window that took the foreground when the
/// workspace emptied is that window, for the launcher and for a last window that
/// closes, hides or minimises after it.
/// </para>
/// <para>
/// So this is a zero-by-zero popup with no surface to draw and nothing to draw on
/// it, placed on the monitor whose workspace is empty. Owned rather than a tool
/// window, because an owned window is left off the taskbar and out of Alt+Tab just
/// as a tool window is, and the difference is what decides the minimise case: the
/// walk Windows performs when the active window minimises skips tool windows and
/// does not skip owned ones. The one cost of that choice is Alt+Esc, which walks the
/// same way and so stops on this window for one press.
/// </para>
/// <para>
/// It receives no events from the daemon's own hook - the hook skips this process -
/// so nothing in the tree ever mistakes it for a window to follow; the filter refuses
/// it by class besides. Created on first use, on the daemon's thread, which is the
/// thread that pumps its messages; a configuration that never asks for it never
/// creates it.
/// </para>
/// </remarks>
public sealed unsafe class FocusSink : IDisposable
{
    /// <summary>The window class, which the window filter excludes by name.</summary>
    public const string WindowClass = "ShubbakFocusSink";

    private static bool s_classRegistered;

    private HWND _owner;
    private HWND _window;
    private bool _disposed;

    /// <summary>Where the sink was last put, so a repeat on the same monitor moves nothing.</summary>
    private int _x;
    private int _y;

    /// <summary>The position of the sink's window, for tests.</summary>
    public (int X, int Y) Position => (_x, _y);

    /// <summary>Whether the sink window exists.</summary>
    public bool IsCreated => !_window.IsNull;

    /// <summary>Whether <paramref name="handle"/> is this sink's window.</summary>
    public bool Is(nint handle) => !_window.IsNull && handle == (nint)_window.Value;

    /// <summary>Whether the sink currently holds the system foreground.</summary>
    public bool HoldsForeground => !_window.IsNull && PInvoke.GetForegroundWindow() == _window;

    /// <summary>The sink's window, for tests; zero until it has been created.</summary>
    public nint Handle => (nint)_window.Value;

    /// <summary>
    /// Takes the foreground, placing the sink on the given monitor.
    /// </summary>
    /// <param name="workArea">
    /// The work area of the monitor whose workspace is empty. The sink is put at its
    /// top-left corner, so anything that asks which monitor the foreground window is
    /// on - a launcher deciding where to open - is told this one.
    /// </param>
    /// <returns>Whether the sink holds the foreground afterwards.</returns>
    /// <remarks>
    /// Cheap to repeat: when it already holds the foreground nothing is asked of the
    /// system beyond the one call that says so - unless the monitor has changed, in
    /// which case it is moved there without any change of activation. That happens
    /// when the last window on the other monitor closes and Windows hands the
    /// foreground back to a sink still sitting where the previous empty workspace was.
    /// </remarks>
    public bool Take(Rect workArea)
    {
        if (_disposed) return false;
        if (!EnsureCreated()) return false;

        bool moved = workArea.X != _x || workArea.Y != _y;
        _x = workArea.X;
        _y = workArea.Y;

        if (PInvoke.GetForegroundWindow() == _window)
        {
            if (moved)
            {
                PInvoke.SetWindowPos(
                    _window, HWND.Null, _x, _y, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
            }

            return true;
        }

        // Shown without activating first, and positioned, so that the activation
        // that follows finds a visible window to give the foreground to. Zero size is
        // deliberate: there is nothing to see and nothing to hit.
        PInvoke.SetWindowPos(
            _window, HWND.Null, _x, _y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

        return WindowActions.Focus((nint)_window.Value);
    }

    /// <summary>
    /// Hides the sink, for while Shubbak is not arranging the desktop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A visible sink is eligible to be handed the foreground, and while paused or
    /// suspended that is the wrong thing to be: a window closing would leave the
    /// keyboard on a window of Shubbak's own with Shubbak doing nothing about it.
    /// Hidden, it is skipped, and Windows behaves as it would with no window manager.
    /// </para>
    /// <para>
    /// If it holds the foreground at the time, the foreground is given to the desktop
    /// first, which is where it would have been left before the sink existed. Hiding
    /// an active window makes Windows choose the fallback, and the fallback is
    /// exactly the choice this class exists to pre-empt.
    /// </para>
    /// </remarks>
    public void Retire()
    {
        if (_window.IsNull || !PInvoke.IsWindowVisible(_window)) return;

        if (PInvoke.GetForegroundWindow() == _window) WindowActions.FocusDesktop();

        PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_HIDE);
    }

    private bool EnsureCreated()
    {
        if (!_window.IsNull) return true;

        try
        {
            EnsureClassRegistered();

            // The owner exists to be an owner. It is never shown, and it is a tool
            // window so that nothing lists it either.
            _owner = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                WindowClass,
                "Shubbak focus sink owner",
                WINDOW_STYLE.WS_POPUP,
                0, 0, 0, 0,
                HWND.Null,
                (SafeHandle?)null,
                (SafeHandle?)null,
                null);

            if (_owner.IsNull)
            {
                Log.Warn(LogCategory.Window, $"could not create the focus sink's owner: {Marshal.GetLastWin32Error()}");
                return false;
            }

            // No redirection bitmap: the compositor allocates no surface for a window
            // that will never paint. Transparent: should it ever have a pixel, a click
            // on it goes to whatever is underneath.
            _window = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
                WindowClass,
                "Shubbak focus sink",
                WINDOW_STYLE.WS_POPUP,
                0, 0, 0, 0,
                _owner,
                (SafeHandle?)null,
                (SafeHandle?)null,
                null);

            if (_window.IsNull)
            {
                Log.Warn(LogCategory.Window, $"could not create the focus sink: {Marshal.GetLastWin32Error()}");
                PInvoke.DestroyWindow(_owner);
                _owner = HWND.Null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory.Window, $"could not create the focus sink: {ex.Message}");
            return false;
        }
    }

    private static void EnsureClassRegistered()
    {
        if (s_classRegistered) return;

        fixed (char* className = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = HINSTANCE.Null,
                lpszClassName = className,
            };

            // 1410 is ERROR_CLASS_ALREADY_EXISTS, which is success for our purposes.
            if (PInvoke.RegisterClassEx(in wc) == 0 && Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            }
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        // Alt+F4 while the sink has the keyboard. DefWindowProc would destroy the
        // window, and the sink would then be gone for the rest of the session with
        // nothing to notice. A close request is simply declined; the sink leaves when
        // the daemon does.
        if (message == PInvoke.WM_CLOSE) return new LRESULT(0);

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_window.IsNull)
        {
            // Destroying the active window makes Windows pick a fallback, and the
            // fallback is what this class exists to avoid. The desktop is where the
            // foreground was left before the sink existed, so that is where it goes.
            if (PInvoke.GetForegroundWindow() == _window) WindowActions.FocusDesktop();

            PInvoke.DestroyWindow(_window);
            _window = HWND.Null;
        }

        if (!_owner.IsNull)
        {
            PInvoke.DestroyWindow(_owner);
            _owner = HWND.Null;
        }
    }
}
