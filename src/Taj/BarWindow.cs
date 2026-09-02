using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Taj.Core;
using Shubbak.Ui.Layout;
using Shubbak.Ui.Rendering;
using Shubbak.Ui.Gdi;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32.Graphics.Gdi;

namespace Taj;

/// <summary>
/// One bar window, on one monitor.
/// </summary>
/// <remarks>
/// <para>
/// The bar is a plain top-level window rather than an overlay. It reserves its space
/// through the shell's appbar API, so maximised windows stop at its edge and Shubbak
/// sees the reduced work area automatically - the same mechanism the taskbar uses.
/// Without it a maximised window would sit underneath the bar.
/// </para>
/// <para>
/// Redraws only when the model reports a change. A bar that repaints on a timer
/// burns battery for nothing; one that repaints on every event flickers.
/// </para>
/// </remarks>
public sealed class BarWindow : IDisposable
{
    private const string WindowClass = "TajBarWindow";
    private const uint AppbarCallbackMessage = PInvoke.WM_APP + 1;

    private static readonly Dictionary<nint, BarWindow> s_windows = [];
    private static bool s_classRegistered;

    /// <summary>
    /// Broadcast to every top-level window when Explorer restarts.
    /// </summary>
    /// <remarks>
    /// Registered once. The system allocates the same value for every process that
    /// asks, which is how one broadcast reaches all of them.
    /// </remarks>
    private static uint s_taskbarCreated;

    /// <summary>
    /// Raised when a bar window is asked to close, meaning the process should stop.
    /// </summary>
    /// <remarks>
    /// Static because the window procedure has to be - it is an
    /// <c>UnmanagedCallersOnly</c> entry point, so it cannot close over an instance.
    /// There is one message loop behind however many bars, so any window closing is
    /// the process closing.
    /// </remarks>
    public static event Action? RequestShutdown;

    /// <summary>
    /// Raised when the shell says a full-screen application has opened or closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static for the same reason as <see cref="RequestShutdown"/>, and because the
    /// answer is about the desktop rather than about one bar: a game on one monitor
    /// covers that bar, and the loop that drives all of them is one loop.
    /// </para>
    /// <para>
    /// True when one opens, false when one closes. Deliberately treated as a hint
    /// rather than as the truth - see <c>StandDown.StillCovered</c>.
    /// </para>
    /// </remarks>
    public static event Action<bool>? FullScreenAppChanged;

    private readonly BarModel _model;
    private readonly int _monitorIndex;

    private HWND _handle;
    private GdiRenderer? _renderer;
    private FlexLayout? _layout;
    private VisualNode? _tree;
    private Rect _bounds;
    private bool _appbarRegistered;
    private bool _refusalReported;
    private VisualNode? _hovered;
    private bool _mouseTracked;
    private bool _disposed;

    /// <summary>Raised when a widget is clicked, with the command to run.</summary>
    public event Action<string>? CommandRequested;

    public BarWindow(BarModel model, int monitorIndex)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _monitorIndex = monitorIndex;
    }

    public unsafe nint Handle => (nint)_handle.Value;

    /// <summary>Creates the window on the given monitor work area.</summary>
    public unsafe bool Create(Rect monitorBounds)
    {
        EnsureClassRegistered();

        BarProfile profile = _model.Profile;

        _bounds = profile.Edge == BarEdge.Top
            ? new Rect(monitorBounds.X, monitorBounds.Y, monitorBounds.Width, profile.Height)
            : new Rect(
                monitorBounds.X,
                monitorBounds.Bottom - profile.Height,
                monitorBounds.Width,
                profile.Height);

        _handle = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
            WindowClass,
            "Taj",
            WINDOW_STYLE.WS_POPUP,
            _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
            HWND.Null, (SafeHandle?)null, (SafeHandle?)null, null);

        if (_handle.IsNull)
        {
            Log.Error(LogCategory.Wm, $"could not create bar window: {Marshal.GetLastWin32Error()}");
            return false;
        }

        s_windows[(nint)_handle.Value] = this;

        AllowShellRestartBroadcast();

        _renderer = new GdiRenderer((nint)_handle.Value);
        _layout = new FlexLayout(_renderer);

        RegisterAppbar();
        ApplyBackdrop();

        PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        return true;
    }

    /// <summary>Rebuilds and repaints if the model has changed.</summary>
    public void Update()
    {
        if (_handle.IsNull || _renderer is null || _layout is null) return;

        if (!_model.IsDirty && _tree is not null) return;

        // Height can change when the profile does, e.g. a presentation profile with
        // a slimmer bar.
        if (_model.Profile.Height != _bounds.Height) Resize(_model.Profile.Height);

        _tree = _model.Build();
        _layout.Arrange(_tree, _bounds with { X = 0, Y = 0 });

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.Debug(LogCategory.Wm,
                $"bar {_monitorIndex} laid out at {_bounds.Width}x{_bounds.Height}: " +
                string.Join(", ", _tree.SelfAndDescendants()
                    .Where(n => n.Visible && !n.Rect.IsEmpty && n.Kind == VisualKind.Text)
                    .Select(n => $"{n.Id}@{n.Rect.Left}..{n.Rect.Right}")));
        }

        PInvoke.InvalidateRect(_handle, (RECT?)null, false);
        PInvoke.UpdateWindow(_handle);
    }

    private void Resize(int height)
    {
        _bounds = _model.Profile.Edge == BarEdge.Top
            ? _bounds with { Height = height }
            : new Rect(_bounds.X, _bounds.Bottom - height, _bounds.Width, height);

        PInvoke.SetWindowPos(
            _handle, HWND.Null, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);

        RegisterAppbar();
    }

    private void Paint()
    {
        if (_renderer is null || _tree is null) return;

        VisualPainter.Paint(
            _renderer, _tree, _bounds with { X = 0, Y = 0 }, _model.Profile.Background, _hovered);
    }

    /// <summary>Tracks which node the pointer is over, repainting when it changes.</summary>
    /// <remarks>
    /// The tree is not rebuilt for this. Hovering changes how a node is drawn, not
    /// what it says, and rebuilding on pointer movement would mean rebuilding many
    /// times a second for no change in content.
    /// </remarks>
    private void OnMouseMove(int x, int y)
    {
        if (!_mouseTracked) StartTrackingMouse();

        VisualNode? hovered = Interactive(_tree?.HitTest(x, y));

        if (ReferenceEquals(hovered, _hovered)) return;

        _hovered = hovered;

        PInvoke.InvalidateRect(_handle, (RECT?)null, false);
    }

    private void OnMouseLeave()
    {
        _mouseTracked = false;

        if (_hovered is null) return;

        _hovered = null;

        PInvoke.InvalidateRect(_handle, (RECT?)null, false);
    }

    /// <summary>The nearest ancestor that reacts to the pointer, if any.</summary>
    private VisualNode? Interactive(VisualNode? node)
    {
        if (_tree is null) return null;

        for (VisualNode? current = node; current is not null; current = FindParent(_tree, current))
            if (current.HoverStyle is not null) return current;

        return null;
    }

    private void StartTrackingMouse()
    {
        // Without this there is no WM_MOUSELEAVE, and the highlight would stay behind
        // after the pointer had gone.
        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
            hwndTrack = _handle,
        };

        _mouseTracked = PInvoke.TrackMouseEvent(ref track);
    }

    private void OnClick(int x, int y)
    {
        if (_tree?.HitTest(x, y) is not { } node) return;

        // Walk up: the click usually lands on a text node inside the element that
        // carries the command.
        for (VisualNode? current = node; current is not null; current = FindParent(_tree, current))
        {
            if (current.OnClick is { Length: > 0 } command)
            {
                CommandRequested?.Invoke(command);
                return;
            }
        }
    }

    private static VisualNode? FindParent(VisualNode root, VisualNode child)
    {
        foreach (VisualNode candidate in root.SelfAndDescendants())
            if (candidate.Children.Contains(child)) return candidate;

        return null;
    }

    // ---- shell integration -------------------------------------------------

    /// <summary>
    /// Reserves the bar's strip of screen so other windows do not cover it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same mechanism the taskbar uses. Shubbak reads the resulting work area
    /// through <c>GetMonitorInfo</c>, so the bar and the window manager stay
    /// consistent without either knowing about the other.
    /// </para>
    /// <para>
    /// <c>ABM_NEW</c> can fail - the shell may not be up yet, which is the ordinary
    /// case when Taj is started from Shubbak's startup commands during logon. The
    /// result is therefore believed rather than assumed: claiming a reservation that
    /// was refused means never asking again, and a bar nobody has reserved room for is
    /// a bar every window is tiled on top of.
    /// </para>
    /// </remarks>
    private unsafe void RegisterAppbar()
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)sizeof(APPBARDATA),
            hWnd = _handle,
            uCallbackMessage = AppbarCallbackMessage,
            uEdge = _model.Profile.Edge == BarEdge.Top ? 1u : 3u,   // ABE_TOP : ABE_BOTTOM
            rc = new RECT
            {
                left = _bounds.Left,
                top = _bounds.Top,
                right = _bounds.Right,
                bottom = _bounds.Bottom,
            },
        };

        const uint AbmNew = 0x00000000;
        const uint AbmSetPos = 0x00000003;

        if (!_appbarRegistered)
        {
            if (PInvoke.SHAppBarMessage(AbmNew, ref data) == 0)
            {
                // Once. The retry runs off the message loop, which wakes on every
                // repaint and every source that publishes, so a shell that stays
                // unwilling would otherwise write this line hundreds of times.
                if (!_refusalReported)
                {
                    _refusalReported = true;

                    Log.Warn(LogCategory.Wm,
                        $"the shell refused bar {_monitorIndex}'s reservation; will keep trying");
                }

                return;
            }

            if (_refusalReported)
            {
                _refusalReported = false;

                Log.Info(LogCategory.Wm, $"bar {_monitorIndex}'s strip is reserved again");
            }

            _appbarRegistered = true;
        }

        PInvoke.SHAppBarMessage(AbmSetPos, ref data);
    }

    /// <summary>
    /// Re-attempts a reservation the shell has refused. Cheap, and usually nothing.
    /// </summary>
    /// <remarks>
    /// Called from the message loop rather than driven by an event because there is no
    /// event to drive it: the shell announces that it has started, not that it is
    /// finally ready to accept an appbar, and the two are not the same instant after a
    /// crash. A bool test per pass of a loop that already wakes once a second is a
    /// cheaper answer than a timer, and one that also covers the logon race.
    /// </remarks>
    public void EnsureReserved()
    {
        if (_appbarRegistered || _handle.IsNull) return;

        RegisterAppbar();
    }

    private unsafe void UnregisterAppbar()
    {
        if (!_appbarRegistered) return;

        var data = new APPBARDATA
        {
            cbSize = (uint)sizeof(APPBARDATA),
            hWnd = _handle,
        };

        const uint AbmRemove = 0x00000001;
        PInvoke.SHAppBarMessage(AbmRemove, ref data);

        _appbarRegistered = false;
    }

    /// <summary>What the shell can tell a registered appbar.</summary>
    /// <remarks>
    /// These arrive as <c>wParam</c> of <see cref="AppbarCallbackMessage"/>, which is
    /// the message handed to the shell in <see cref="RegisterAppbar"/>. Registering a
    /// callback message and then never listening to it is registering to be told
    /// nothing, which is what this used to do.
    /// </remarks>
    private static class AppbarNotification
    {
        /// <summary>
        /// Something happened that may have moved the bar's strip: the taskbar was
        /// resized, moved or hidden, or another appbar appeared on the same edge.
        /// </summary>
        public const nuint PositionChanged = 0x00000001;

        /// <summary>A full-screen application opened or closed.</summary>
        public const nuint FullScreenApp = 0x00000002;
    }

    /// <summary>
    /// Re-asserts the reservation after the shell says the layout of docked windows
    /// has changed.
    /// </summary>
    /// <remarks>
    /// Without this, the taskbar being moved to the top, resized, or switched to
    /// auto-hide leaves the bar's reservation describing a strip that is no longer
    /// where the bar is - and Shubbak tiles into the work area that reservation
    /// produced, so the error lands on every window rather than on the bar.
    /// </remarks>
    private void OnAppbarPositionChanged()
    {
        if (!_appbarRegistered) return;

        RegisterAppbar();
    }

    /// <summary>
    /// Reserves the strip again after Explorer has restarted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shell owns the list of registered appbars, so a restart forgets every one of
    /// them and hands the reserved space back. Nothing arrives on the appbar callback
    /// to say so - as far as the new Explorer is concerned this bar never registered,
    /// and it does not send notifications to windows it has never heard of. The
    /// broadcast is the only announcement there is.
    /// </para>
    /// <para>
    /// The symptom of not listening is not a broken bar, which is why it survived: the
    /// bar keeps drawing, keeps updating and keeps taking clicks. It is the work area
    /// that reverts, and Shubbak - correctly reading a work area that now covers the
    /// whole monitor - tiles every window over the top of a bar that is still perfectly
    /// alive underneath. Explorer hanging and being restarted is the common way in.
    /// </para>
    /// </remarks>
    private void OnShellRestarted()
    {
        Log.Info(LogCategory.Wm, $"the shell restarted; reserving bar {_monitorIndex}'s strip again");

        // Removed before it is added, and the removal is expected to do nothing. Against
        // a genuinely restarted Explorer it addresses a shell that never heard of this
        // window, which is free. It earns its place in the other case: the broadcast can
        // arrive without the registration having actually been dropped - a shell
        // replacement, or a tool that sends it deliberately - and ABM_NEW is refused for
        // a window already on the list, which would leave the retry below failing
        // against a reservation that was never lost, forever.
        //
        // Re-asserting with ABM_SETPOS instead would be wrong the other way round: after
        // a real restart it addresses nobody.
        UnregisterAppbar();

        RegisterAppbar();
    }

    /// <summary>
    /// Lets the Explorer-restart broadcast through UIPI.
    /// </summary>
    /// <remarks>
    /// Windows silently drops messages sent from a lower integrity level to a higher
    /// one. Shubbak tells people to run the daemon elevated in order to manage elevated
    /// windows, and the daemon starts Taj, so an elevated bar being told nothing by an
    /// ordinary Explorer is a configuration the documentation actively recommends.
    /// Failure is ignored: unelevated, there is nothing to allow.
    /// </remarks>
    private unsafe void AllowShellRestartBroadcast()
    {
        if (s_taskbarCreated == 0)
            s_taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");

        if (s_taskbarCreated == 0)
        {
            Log.Warn(LogCategory.Wm,
                "could not register the shell-restart broadcast; the bar will not " +
                "reserve its strip again if Explorer restarts");
            return;
        }

        PInvoke.ChangeWindowMessageFilterEx(
            _handle, s_taskbarCreated, WINDOW_MESSAGE_FILTER_ACTION.MSGFLT_ALLOW, null);
    }

    /// <summary>
    /// Steps out of the way of a full-screen application, and back afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The documented contract for an appbar: drop to the bottom of the z-order while
    /// a full-screen application is up, and return afterwards. The taskbar does the
    /// same thing, which is why it disappears under a full-screen video and comes back
    /// when the video ends.
    /// </para>
    /// <para>
    /// The reservation is deliberately left alone. Un-reserving would shrink the work
    /// area away from underneath every tiled window and lay the whole workspace out
    /// again, twice, for the sake of one window that is already covering the bar. The
    /// z-order is the entire mechanism, and the entire fix.
    /// </para>
    /// </remarks>
    private void OnFullScreenApp(bool opening)
    {
        // HWND_BOTTOM = 1, HWND_TOP = 0. Sentinels rather than real handles.
        var band = new HWND(opening ? 1 : 0);

        PInvoke.SetWindowPos(
            _handle, band, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        // Told once, to the loop, rather than each bar deciding for itself. A bar that
        // is completely covered has nothing to poll for and nothing to redraw, and the
        // loop is where that is acted on.
        FullScreenAppChanged?.Invoke(opening);
    }

    /// <summary>
    /// Asks the compositor for a system backdrop.
    /// </summary>
    /// <remarks>
    /// Windows 11 only, and failure is ignored: on Windows 10 the bar simply uses its
    /// configured background colour, which is a perfectly good bar.
    /// </remarks>
    private unsafe void ApplyBackdrop()
    {
        const DWMWINDOWATTRIBUTE SystemBackdropType = (DWMWINDOWATTRIBUTE)38;
        const int Mica = 2;

        int value = Mica;
        PInvoke.DwmSetWindowAttribute(_handle, SystemBackdropType, &value, sizeof(int));
    }

    // ---- window plumbing ---------------------------------------------------

    private static unsafe void EnsureClassRegistered()
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

                // No background brush: every pixel is painted from the off-screen
                // buffer, and letting Windows erase first causes a visible flash.
                hbrBackground = Windows.Win32.Graphics.Gdi.HBRUSH.Null,

                // A class with no cursor leaves whatever the pointer was last given,
                // which over a bar that never sets one is usually the busy cursor
                // inherited from the application it just left. The bar is never busy.
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
            };

            if (PInvoke.RegisterClassEx(in wc) == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new InvalidOperationException(
                    $"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (s_windows.TryGetValue((nint)hwnd.Value, out BarWindow? window))
            {
                // Ahead of the switch because its value is allocated at run time by
                // RegisterWindowMessage, and a case label has to be a constant.
                if (s_taskbarCreated != 0 && message == s_taskbarCreated)
                {
                    window.OnShellRestarted();
                    return new LRESULT(0);
                }

                switch (message)
                {
                    case PInvoke.WM_PAINT:
                    {
                        PInvoke.BeginPaint(hwnd, out PAINTSTRUCT ps);
                        window.Paint();
                        PInvoke.EndPaint(hwnd, in ps);
                        return new LRESULT(0);
                    }

                    case PInvoke.WM_LBUTTONDOWN:
                    {
                        int x = (short)(lParam.Value & 0xFFFF);
                        int y = (short)((lParam.Value >> 16) & 0xFFFF);
                        window.OnClick(x, y);
                        return new LRESULT(0);
                    }

                    case PInvoke.WM_MOUSEMOVE:
                    {
                        int x = (short)(lParam.Value & 0xFFFF);
                        int y = (short)((lParam.Value >> 16) & 0xFFFF);
                        window.OnMouseMove(x, y);
                        return new LRESULT(0);
                    }

                    case PInvoke.WM_MOUSELEAVE:
                        window.OnMouseLeave();
                        return new LRESULT(0);

                    case AppbarCallbackMessage:
                        switch ((nuint)wParam.Value)
                        {
                            case AppbarNotification.PositionChanged:
                                window.OnAppbarPositionChanged();
                                break;

                            case AppbarNotification.FullScreenApp:
                                window.OnFullScreenApp(lParam.Value != 0);
                                break;

                            default:
                                break;
                        }

                        return new LRESULT(0);

                    case PInvoke.WM_CLOSE:
                        // Closing any bar closes the bar. There is one message loop
                        // behind however many monitors, so a window going is the
                        // process going - and without this, closing the window left
                        // Taj running with nothing to show, which is how `taj-exit`
                        // and Task Manager's "End task" both used to do nothing.
                        RequestShutdown?.Invoke();
                        return new LRESULT(0);

                    case PInvoke.WM_DESTROY:
                        s_windows.Remove((nint)hwnd.Value);
                        return new LRESULT(0);

                    default:
                        break;
                }
            }
        }
        catch
        {
            // An exception escaping an UnmanagedCallersOnly callback tears down the
            // process, and a crashed bar is worse than a missed repaint.
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAppbar();

        _renderer?.Dispose();

        if (!_handle.IsNull)
        {
            s_windows.Remove((nint)_handle.Value);
            PInvoke.DestroyWindow(_handle);
            _handle = HWND.Null;
        }
    }
}
