using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Taj.Core;
using Taj.Core.Layout;
using Taj.Core.Rendering;
using Taj.Rendering;
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
    /// Raised when a bar window is asked to close, meaning the process should stop.
    /// </summary>
    /// <remarks>
    /// Static because the window procedure has to be - it is an
    /// <c>UnmanagedCallersOnly</c> entry point, so it cannot close over an instance.
    /// There is one message loop behind however many bars, so any window closing is
    /// the process closing.
    /// </remarks>
    public static event Action? RequestShutdown;

    private readonly BarModel _model;
    private readonly int _monitorIndex;

    private HWND _handle;
    private GdiRenderer? _renderer;
    private FlexLayout? _layout;
    private VisualNode? _tree;
    private Rect _bounds;
    private bool _appbarRegistered;
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
    /// The same mechanism the taskbar uses. Shubbak reads the resulting work area
    /// through <c>GetMonitorInfo</c>, so the bar and the window manager stay
    /// consistent without either knowing about the other.
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
            PInvoke.SHAppBarMessage(AbmNew, ref data);
            _appbarRegistered = true;
        }

        PInvoke.SHAppBarMessage(AbmSetPos, ref data);
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
