using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
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
/// does not skip owned ones.
/// </para>
/// <para>
/// Alt+Esc walks the same way, and so lands on this window when the cycle reaches
/// it. Rather than cost the user a press that does nothing, the sink passes the
/// gesture on: it works out that it was cycled onto rather than fallen back to, and
/// hands the keyboard to the window Alt+Esc would have reached next, putting itself
/// at the bottom of the order on the way. How it tells the two apart is in
/// <see cref="WasCycledOnto"/>; what it needs telling is in
/// <see cref="PreviousForeground"/>.
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

    /// <summary>
    /// Posted by the sink to itself when it was activated without asking to be, so
    /// the judgement about why can be made after the activation has finished.
    /// </summary>
    /// <remarks>
    /// Never inside <c>WM_ACTIVATE</c>: that message is the system part-way through
    /// handing activation over, and moving the foreground again from inside it is
    /// asking two windows to be active at once. One message later the hand-over is
    /// complete, and the window that let go has finished whatever it was doing -
    /// hiding, closing, minimising - which is what the judgement reads.
    /// </remarks>
    private const uint PassOnMessage = PInvoke.WM_APP + 2;

    private static readonly Dictionary<nint, FocusSink> s_windows = [];
    private static bool s_classRegistered;

    private HWND _owner;
    private HWND _window;
    private bool _disposed;

    /// <summary>Where the sink was last put, so a repeat on the same monitor moves nothing.</summary>
    private int _x;
    private int _y;

    /// <summary>Set while <see cref="Take"/> is activating the window, so that activation is not judged.</summary>
    private bool _taking;

    /// <summary>Whether Alt was down at the moment the sink was last activated.</summary>
    private bool _altHeldAtActivation;

    /// <summary>Whether Shift was down too, which makes the cycle run the other way.</summary>
    private bool _shiftHeldAtActivation;

    /// <summary>
    /// Asked which window had the foreground before the sink, whenever the sink is
    /// activated without having asked to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supplied by the window manager, which sees every change of foreground the
    /// system reports except to windows of its own - so the answer is exactly the
    /// window the sink took over from, whatever thread or process it belongs to. Zero,
    /// or no delegate, means nobody knows, and an activation is then never read as a
    /// cycle.
    /// </para>
    /// <para>
    /// Asked rather than worked out here, because it cannot be worked out here. The
    /// window being deactivated, which <c>WM_ACTIVATE</c> is documented to carry, is
    /// null across threads, and the window that let go is always on another thread.
    /// <c>WM_ACTIVATEAPP</c> names a thread, and the thread it names is the one that
    /// had the keyboard - which for a WinUI application such as the Windows 11 Notepad
    /// is not the thread that owns the window: its focus sits in a child on a thread
    /// of its own, and that thread owns nothing on screen. Both were measured before
    /// this was settled on, the second on a live desktop.
    /// </para>
    /// </remarks>
    public Func<nint>? PreviousForeground { get; set; }

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
    /// How many times the sink has passed a cycling gesture on, for tests and the
    /// diagnostic report.
    /// </summary>
    public int PassedOn { get; private set; }

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

        // An activation the sink asked for is not one to judge. The flag covers the
        // whole call: WM_ACTIVATE arrives inside it, synchronously.
        _taking = true;

        try
        {
            return WindowActions.Focus((nint)_window.Value);
        }
        finally
        {
            _taking = false;
        }
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

    /// <summary>
    /// Whether an activation the sink did not ask for was Alt+Esc reaching it, rather
    /// than Windows falling back to it.
    /// </summary>
    /// <param name="altHeldAtActivation">Whether Alt was down when the sink was activated.</param>
    /// <param name="previousForegroundStillShowing">
    /// Whether the window that had the foreground before the sink is still a window
    /// Alt+Esc would stop on - visible, not minimised, enabled, not cloaked; see
    /// <see cref="IsCycleStop"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// A fallback happens because the window that had the foreground went away: a
    /// launcher hid itself, the last window on the workspace closed or minimised. That
    /// window is then hidden, gone or iconic, whatever the keyboard is doing. Alt+Esc
    /// happens with that window still on screen - it has only gone to the bottom of
    /// the stacking order - and with Alt held, since the switch is made while Esc is
    /// going down.
    /// </para>
    /// <para>
    /// Both are required, because each alone is fooled by something common. Alt+F4
    /// closes a window with Alt down and is a fallback; a window that stays on screen
    /// while the sink is activated without Alt is nothing Windows is known to do, and
    /// a cycle is the more consequential misreading, so it is not read into unknown
    /// cases. Judged on the window and not on its thread or its process: closing one
    /// browser window with Alt+F4 while another is open on the other monitor is a
    /// fallback too, and the second window is on the same thread as the first.
    /// </para>
    /// </remarks>
    public static bool WasCycledOnto(bool altHeldAtActivation, bool previousForegroundStillShowing) =>
        altHeldAtActivation && previousForegroundStillShowing;

    /// <summary>
    /// Whether a window is one Alt+Esc would stop on.
    /// </summary>
    /// <remarks>
    /// The same shape Windows uses for its own walk, as far as it could be measured:
    /// on screen and able to take the keyboard, and not a tool window unless it asks
    /// to be treated as an application window. Cloaked windows are skipped, which is
    /// what Windows does for windows on other virtual desktops - measured - and which
    /// keeps the walk out of the workspaces Shubbak has concealed, where activating a
    /// window would switch the desktop to it. A window owned by one that is itself on
    /// screen is skipped as well: its owner is further down the order and activating
    /// the owner brings the owned window forward, which is how Windows treats a
    /// dialog. A handle that is no longer a window is, naturally, not a stop.
    /// </remarks>
    public static bool IsCycleStop(nint handle)
    {
        if (handle == 0) return false;

        var hwnd = new HWND(handle);

        if (!PInvoke.IsWindow(hwnd)) return false;

        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd) || !PInvoke.IsWindowEnabled(hwnd))
            return false;

        WINDOW_EX_STYLE exStyle = Win32Window.GetExStyle(handle);

        if ((exStyle & WINDOW_EX_STYLE.WS_EX_NOACTIVATE) != 0) return false;

        if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & WINDOW_EX_STYLE.WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        if (Win32Window.GetCloakState(handle) != Win32Window.CloakState.None) return false;

        HWND rootOwner = PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
        if (rootOwner != hwnd && PInvoke.IsWindowVisible(rootOwner)) return false;

        return true;
    }

    /// <summary>
    /// The window Alt+Esc would have reached had the sink not been in the way.
    /// </summary>
    /// <param name="backwards">Alt+Shift+Esc: the window at the bottom of the order rather than the next one down.</param>
    /// <returns>The handle, or zero when nothing else is on screen.</returns>
    /// <remarks>
    /// Forwards, the first stop below the sink in the stacking order, which is where
    /// Alt+Esc was headed. Backwards, the first stop up from the bottom, which is
    /// what Alt+Shift+Esc brings forward. The sink's own windows are never chosen.
    /// </remarks>
    private nint NextCycleStop(bool backwards)
    {
        HWND cursor = backwards
            ? PInvoke.GetWindow(_window, GET_WINDOW_CMD.GW_HWNDLAST)
            : PInvoke.GetWindow(_window, GET_WINDOW_CMD.GW_HWNDNEXT);

        GET_WINDOW_CMD step = backwards ? GET_WINDOW_CMD.GW_HWNDPREV : GET_WINDOW_CMD.GW_HWNDNEXT;

        while (!cursor.IsNull)
        {
            if (cursor != _window && cursor != _owner && IsCycleStop((nint)cursor.Value))
                return (nint)cursor.Value;

            cursor = PInvoke.GetWindow(cursor, step);
        }

        return 0;
    }

    /// <summary>
    /// Hands a cycling gesture on to the window it was headed for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sink goes to the bottom of the stacking order first, which is where
    /// Alt+Esc puts the window it leaves and which keeps the sink out of the next few
    /// presses; then the next stop is given the keyboard. <c>SC_NEXTWINDOW</c> through
    /// <c>DefWindowProc</c> would have been the natural way to say this and does
    /// nothing when asked from here - measured, twice - so the walk is Shubbak's own.
    /// </para>
    /// <para>
    /// Only while the sink still holds the foreground. If something else has taken it
    /// in the meantime - the user kept pressing - there is nothing to pass on.
    /// </para>
    /// </remarks>
    private void PassOn()
    {
        if (_window.IsNull || PInvoke.GetForegroundWindow() != _window) return;

        nint next = NextCycleStop(_shiftHeldAtActivation);

        // HWND_BOTTOM = 1. A sentinel rather than a real handle.
        PInvoke.SetWindowPos(
            _window, new HWND(1), 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        if (next == 0) return;

        PassedOn++;

        bool handed = WindowActions.Focus(next);

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.Debug(LogCategory.Window,
                $"alt+{(_shiftHeldAtActivation ? "shift+" : "")}esc reached the focus sink; " +
                $"{(handed ? "passed on to" : "could not pass on to")} 0x{next:X} \"{Win32Window.GetTitle(next).Truncate(40)}\"");
        }
    }

    /// <summary>What the sink does with an activation it did not ask for.</summary>
    private void OnActivated()
    {
        if (_taking) return;

        // Read now, not when the posted message arrives: Alt is what the user was
        // holding at the instant of the switch, and a moment later it may be up.
        _altHeldAtActivation = PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_MENU) < 0;
        _shiftHeldAtActivation = PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_SHIFT) < 0;

        PInvoke.PostMessage(_window, PassOnMessage, default, default);
    }

    /// <summary>The posted half: judge, and pass on if it was a cycle.</summary>
    private void OnPassOnDue()
    {
        nint previous = PreviousForeground?.Invoke() ?? 0;
        bool stillShowing = !Is(previous) && IsCycleStop(previous);

        if (!WasCycledOnto(_altHeldAtActivation, stillShowing))
        {
            if (Log.IsEnabled(LogLevel.Trace))
            {
                Log.Trace(LogCategory.Window,
                    $"focus sink keeps the foreground: a fallback, not a cycle " +
                    $"(alt={_altHeldAtActivation}; before it 0x{previous:X} " +
                    $"\"{Win32Window.GetTitle(previous).Truncate(32)}\", still on screen={stillShowing})");
            }

            return;
        }

        PassOn();
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

            s_windows[(nint)_window.Value] = this;

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
        try
        {
            switch (message)
            {
                // Alt+F4 while the sink has the keyboard. DefWindowProc would destroy
                // the window, and the sink would then be gone for the rest of the
                // session with nothing to notice. A close request is simply declined;
                // the sink leaves when the daemon does.
                case PInvoke.WM_CLOSE:
                    return new LRESULT(0);

                // The low word is WA_INACTIVE (0), WA_ACTIVE or WA_CLICKACTIVE. The
                // window being deactivated, which lParam is documented to carry, is
                // null across threads - so it is not read; see PreviousForeground.
                case PInvoke.WM_ACTIVATE:
                    if ((wParam.Value & 0xFFFF) != 0 && s_windows.TryGetValue((nint)hwnd.Value, out FocusSink? activated))
                        activated.OnActivated();

                    break;

                case PassOnMessage:
                    if (s_windows.TryGetValue((nint)hwnd.Value, out FocusSink? due)) due.OnPassOnDue();

                    return new LRESULT(0);

                default:
                    break;
            }
        }
        catch (Exception)
        {
            // An exception escaping an UnmanagedCallersOnly callback tears down the
            // process. Nothing here is worth that.
        }

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

            s_windows.Remove((nint)_window.Value);
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
