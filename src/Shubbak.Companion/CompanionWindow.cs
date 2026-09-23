using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Companion;

/// <summary>How a window's class is registered: what Windows is told about every window of it.</summary>
/// <param name="Name">The class name, which is also how <c>shubbak taj-exit</c> finds the windows to close.</param>
/// <param name="DropShadow">Whether the compositor draws a shadow under it, as it does for a menu.</param>
public sealed record WindowClassOptions(string Name, bool DropShadow = false);

/// <summary>Which mouse button a press was.</summary>
public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// A top-level window drawn by its owner, with the plumbing every companion window
/// needs and none of what any one of them shows.
/// </summary>
/// <remarks>
/// <para>
/// One window procedure for every window of every derived class, in one place, with
/// the three properties it has to have: it never lets an exception escape - an
/// exception leaving an <c>UnmanagedCallersOnly</c> callback ends the process - it
/// says what it swallowed, and it pairs <c>BeginPaint</c> with <c>EndPaint</c> whatever
/// painting does, since a throw between the two leaves the update region unvalidated
/// and Windows posts the paint again at once, for ever. Both the bar and the palette
/// had all three wrong at some point, separately.
/// </para>
/// <para>
/// The derived class is asked first, through <see cref="OnMessage"/>, so a window
/// with messages of its own - the appbar's callback, the palette's keyboard - answers
/// them before this class sees anything; what it does not claim falls to the handlers
/// here, and what nobody claims to <c>DefWindowProc</c>.
/// </para>
/// <para>
/// Handles are <see cref="nint"/> at this boundary, because CsWin32 generates its
/// types internal to each assembly and the <c>HWND</c> here is not the derived
/// class's <c>HWND</c>.
/// </para>
/// </remarks>
public abstract class CompanionWindow : IDisposable
{
    private static readonly Dictionary<nint, CompanionWindow> s_windows = [];
    private static readonly HashSet<string> s_registered = new(StringComparer.Ordinal);

    private readonly WindowClassOptions _class;
    private HWND _handle;
    private bool _trackingMouse;
    private bool _disposed;

    protected CompanionWindow(WindowClassOptions windowClass)
    {
        _class = windowClass ?? throw new ArgumentNullException(nameof(windowClass));
    }

    /// <summary>The window, or zero before <see cref="CreateWindow"/> and after <see cref="Dispose"/>.</summary>
    public unsafe nint Handle => (nint)_handle.Value;

    /// <summary>Whether the window exists.</summary>
    public bool Exists => !_handle.IsNull;

    /// <summary>
    /// Raised when a window of any derived class is asked to close - by
    /// <c>shubbak &lt;name&gt;-exit</c>, Task Manager, or Windows ending the session.
    /// </summary>
    /// <remarks>
    /// Static, because a companion has one message loop behind however many windows,
    /// and a window going is the process going. Closing one bar closes the bar.
    /// </remarks>
    public static event Action? RequestShutdown;

    /// <summary>
    /// Raised when Windows announces a change of accent colour, once per window that
    /// hears the broadcast.
    /// </summary>
    public static event Action? SystemColoursChanged;

    /// <summary>Creates the window. False, and logged, if Windows refused.</summary>
    /// <param name="exStyle">The <c>WS_EX_*</c> flags.</param>
    /// <param name="style">The <c>WS_*</c> flags.</param>
    /// <param name="title">The window text, which is what the taskbar and Task Manager show.</param>
    /// <param name="bounds">Where, in screen pixels.</param>
    protected unsafe bool CreateWindow(uint exStyle, uint style, string title, Rect bounds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_handle.IsNull) throw new InvalidOperationException("The window has already been created.");

        EnsureClassRegistered(_class);

        _handle = PInvoke.CreateWindowEx(
            (WINDOW_EX_STYLE)exStyle,
            _class.Name,
            title,
            (WINDOW_STYLE)style,
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            HWND.Null, (SafeHandle?)null, (SafeHandle?)null, null);

        if (_handle.IsNull)
        {
            Log.Error(LogCategory.Ui, $"could not create the {_class.Name} window: error {Marshal.GetLastWin32Error()}");
            return false;
        }

        s_windows[(nint)_handle.Value] = this;
        return true;
    }

    /// <summary>Shows the window without activating it: what a bar wants.</summary>
    public void ShowWithoutActivating()
    {
        if (!_handle.IsNull) PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    /// <summary>Shows the window, activating it: what a palette wants.</summary>
    public void Show()
    {
        if (!_handle.IsNull) PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_SHOW);
    }

    /// <summary>Hides the window.</summary>
    public void Hide()
    {
        if (!_handle.IsNull) PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_HIDE);
    }

    /// <summary>Paints the whole window now, on this thread.</summary>
    public void Repaint()
    {
        if (_handle.IsNull) return;

        PInvoke.InvalidateRect(_handle, (RECT?)null, false);
        PInvoke.UpdateWindow(_handle);
    }

    /// <summary>The DPI the window is currently on, or 96 before it exists.</summary>
    public uint Dpi => _handle.IsNull ? 96 : PInvoke.GetDpiForWindow(_handle);

    // ---- what a derived class answers ----------------------------------------------

    /// <summary>Draws the window. Called between <c>BeginPaint</c> and <c>EndPaint</c>.</summary>
    protected abstract void OnPaint();

    /// <summary>
    /// First look at every message. Return true and set <paramref name="result"/> to
    /// answer it; return false to let this class and then <c>DefWindowProc</c> have it.
    /// </summary>
    protected virtual bool OnMessage(uint message, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        return false;
    }

    /// <summary>The pointer moved over the window, in client pixels.</summary>
    protected virtual void OnMouseMove(int x, int y)
    {
    }

    /// <summary>The pointer left the window.</summary>
    protected virtual void OnMouseLeave()
    {
    }

    /// <summary>A button went down over the window, in client pixels.</summary>
    protected virtual void OnMouseDown(MouseButton button, int x, int y)
    {
    }

    /// <summary>The wheel turned over the window; positive is away from the user, in multiples of 120 per notch.</summary>
    protected virtual void OnWheel(int delta)
    {
    }

    /// <summary>Whether the pointer should be a hand rather than an arrow right now.</summary>
    protected virtual bool ShowsHandCursor => false;

    /// <summary>
    /// The window moved to a display of a different DPI, or its display's DPI changed.
    /// </summary>
    /// <param name="dpi">The new DPI.</param>
    /// <param name="suggested">Where Windows suggests the window should now be, in screen pixels.</param>
    protected virtual void OnDpiChanged(uint dpi, Rect suggested)
    {
    }

    /// <summary>Asked to close. By default the process leaves; see <see cref="RequestShutdown"/>.</summary>
    protected virtual void OnCloseRequested() => RequestShutdown?.Invoke();

    /// <summary>
    /// The session is ending, or an installer replacing the files under this process
    /// has asked it to leave through Restart Manager, which is what a silent
    /// <c>winget upgrade</c> runs. By default the process leaves.
    /// </summary>
    protected virtual void OnSessionEnding() => RequestShutdown?.Invoke();

    /// <summary>The window is gone.</summary>
    protected virtual void OnDestroyed()
    {
    }

    // ---- the window procedure ---------------------------------------------------------

    private static unsafe void EnsureClassRegistered(WindowClassOptions options)
    {
        if (s_registered.Contains(options.Name)) return;

        fixed (char* className = options.Name)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = HINSTANCE.Null,
                lpszClassName = className,

                // A shadow, when asked for, is drawn by the compositor and costs this
                // process nothing - no layered window, no second surface.
                style = options.DropShadow ? WNDCLASS_STYLES.CS_DROPSHADOW : 0,

                // No background brush: every pixel is painted from the off-screen
                // buffer, and letting Windows erase first is a visible flash.
                hbrBackground = HBRUSH.Null,

                // A class with no cursor leaves whatever the pointer was last given,
                // which over a window that never sets one is usually the busy cursor
                // inherited from the application it just left.
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
            };

            const int ClassAlreadyExists = 1410;

            if (PInvoke.RegisterClassEx(in wc) == 0 && Marshal.GetLastWin32Error() != ClassAlreadyExists)
                throw new InvalidOperationException($"RegisterClassEx({options.Name}) failed: error {Marshal.GetLastWin32Error()}");
        }

        s_registered.Add(options.Name);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (s_windows.TryGetValue((nint)hwnd.Value, out CompanionWindow? window))
            {
                if (window.OnMessage(message, wParam.Value, lParam.Value, out nint answered))
                    return new LRESULT(answered);

                if (window.HandleCommon(hwnd, message, wParam, lParam, out LRESULT result))
                    return result;
            }
        }
        catch (Exception ex)
        {
            // An exception escaping an UnmanagedCallersOnly callback tears down the
            // process, and a crashed window is worse than a missed repaint. Said out
            // loud, though: a window whose every click and paint failed silently looked
            // broken for no reason anyone could find in the log.
            Log.Error(LogCategory.Ui, $"the window procedure failed handling message 0x{message:X4}", ex);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private unsafe bool HandleCommon(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = new LRESULT(0);

        switch (message)
        {
            case PInvoke.WM_PAINT:
                PInvoke.BeginPaint(hwnd, out PAINTSTRUCT ps);

                try
                {
                    OnPaint();
                }
                finally
                {
                    PInvoke.EndPaint(hwnd, in ps);
                }

                return true;

            case PInvoke.WM_MOUSEMOVE:
                TrackMouseLeaving(hwnd);
                OnMouseMove(LowShort(lParam.Value), HighShort(lParam.Value));
                return true;

            case PInvoke.WM_MOUSELEAVE:
                _trackingMouse = false;
                OnMouseLeave();
                return true;

            case PInvoke.WM_LBUTTONDOWN:
                OnMouseDown(MouseButton.Left, LowShort(lParam.Value), HighShort(lParam.Value));
                return true;

            case PInvoke.WM_RBUTTONDOWN:
                OnMouseDown(MouseButton.Right, LowShort(lParam.Value), HighShort(lParam.Value));
                return true;

            case PInvoke.WM_MBUTTONDOWN:
                OnMouseDown(MouseButton.Middle, LowShort(lParam.Value), HighShort(lParam.Value));
                return true;

            case PInvoke.WM_MOUSEWHEEL:
                OnWheel((short)((wParam.Value >> 16) & 0xFFFF));
                return true;

            // The hand over anything that can be clicked, the arrow over everything
            // else. The class cursor is the arrow; saying so here for the rest stops
            // the hand lingering after the pointer has left a control for a readout
            // beside it.
            case PInvoke.WM_SETCURSOR:
                PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, ShowsHandCursor ? PInvoke.IDC_HAND : PInvoke.IDC_ARROW));
                result = new LRESULT(1);
                return true;

            case PInvoke.WM_DPICHANGED:
            {
                // The new DPI is in either word of wParam - they are equal - and
                // lParam points at the rectangle Windows suggests for the window at
                // that DPI.
                uint dpi = (uint)(wParam.Value & 0xFFFF);
                RECT* suggested = (RECT*)lParam.Value;

                OnDpiChanged(dpi, suggested is null
                    ? default
                    : Rect.FromEdges(suggested->left, suggested->top, suggested->right, suggested->bottom));

                return true;
            }

            // Broadcast to every top-level window when the accent changes, in
            // Settings or by the wallpaper.
            case PInvoke.WM_DWMCOLORIZATIONCOLORCHANGED:
                SystemColoursChanged?.Invoke();
                return true;

            case PInvoke.WM_CLOSE:
                OnCloseRequested();
                return true;

            // The first is a question, answered yes; the second is the moment to go,
            // and a companion that goes when asked is one the installer does not have
            // to kill.
            case PInvoke.WM_QUERYENDSESSION:
                result = new LRESULT(1);
                return true;

            case PInvoke.WM_ENDSESSION:
                if (wParam.Value != 0) OnSessionEnding();
                return true;

            case PInvoke.WM_DESTROY:
                s_windows.Remove((nint)hwnd.Value);
                OnDestroyed();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Asks for one <c>WM_MOUSELEAVE</c>, which Windows sends only to a window that
    /// asked and forgets having been asked once it has sent it.
    /// </summary>
    private unsafe void TrackMouseLeaving(HWND hwnd)
    {
        if (_trackingMouse) return;

        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)sizeof(TRACKMOUSEEVENT),
            dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
            hwndTrack = hwnd,
        };

        _trackingMouse = PInvoke.TrackMouseEvent(ref track);
    }

    private static int LowShort(nint value) => (short)(value & 0xFFFF);

    private static int HighShort(nint value) => (short)((value >> 16) & 0xFFFF);

    /// <summary>Destroys the window. A derived class releases what it drew with first, then calls this.</summary>
    public virtual unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_handle.IsNull)
        {
            s_windows.Remove((nint)_handle.Value);
            PInvoke.DestroyWindow(_handle);
            _handle = HWND.Null;
        }

        GC.SuppressFinalize(this);
    }
}
