using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalil.Core;
using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Gdi;
using Shubbak.Ui.Layout;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dalil;

/// <summary>
/// The palette window: a search box and a list, driven entirely by the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// Created hidden at startup and never destroyed until the process ends. Opening is
/// therefore a ShowWindow and a repaint rather than a window creation, which is what
/// keeps it under the threshold where a user notices having waited.
/// </para>
/// <para>
/// <c>WS_EX_TOOLWINDOW</c> keeps it out of Alt+Tab and out of Shubbak's tree - the
/// filter rejects tool windows - while still allowing it to take the keyboard, which
/// <c>WS_EX_NOACTIVATE</c> would not. The bar uses NOACTIVATE precisely because it
/// must never steal focus; this window exists to.
/// </para>
/// </remarks>
public sealed class PaletteWindow : IDisposable
{
    private const string WindowClass = "DalilPaletteWindow";

    /// <summary>Posted by the IPC reader thread to wake the message loop.</summary>
    internal const uint WakeMessage = PInvoke.WM_APP + 1;

    private static readonly Dictionary<nint, PaletteWindow> s_windows = [];
    private static bool s_classRegistered;

    private readonly PaletteModel _model = new();
    private DalilConfig _config;

    private HWND _handle;
    private GdiRenderer? _renderer;
    private Rect _bounds;
    private string _query = string.Empty;
    private bool _open;
    private bool _disposed;

    /// <summary>
    /// True while the palette is deliberately giving focus away.
    /// </summary>
    /// <remarks>
    /// Choosing a row activates another window, which takes the foreground and sends
    /// this one <c>WM_ACTIVATE</c> with <c>WA_INACTIVE</c> - the same message as the
    /// user clicking elsewhere. Without this flag the close-on-blur handler runs in
    /// the middle of acting on the selection and races it.
    /// </remarks>
    private bool _closing;

    /// <summary>Raised with a command string when a row is chosen.</summary>
    public event Action<string>? CommandRequested;

    /// <summary>
    /// Raised when the palette starts showing a different kind of thing.
    /// </summary>
    /// <remarks>
    /// Fired for every route into a mode, not only for Tab. Typing <c>&gt;</c> changes
    /// mode as surely as pressing Tab does, and so does backspacing over it - and a
    /// mode change that did not refill the list would leave the user searching windows
    /// while the box said "commands".
    /// </remarks>
    public event Action<PaletteMode>? ModeChanged;

    /// <summary>Raised when the process should stop.</summary>
    public static event Action? RequestShutdown;

    public PaletteWindow(DalilConfig config) => _config = config;

    /// <summary>Whether the palette is currently on screen.</summary>
    public bool IsOpen => _open;

    /// <summary>Which mode the palette is showing.</summary>
    /// <remarks>
    /// Read by the host when refreshing, so that a window event arriving while the
    /// user is browsing commands does not replace the command list with a window
    /// list underneath them.
    /// </remarks>
    public PaletteMode Mode => _model.Mode;

    public unsafe nint Handle => (nint)_handle.Value;

    /// <summary>Creates the window, hidden.</summary>
    public unsafe bool Create()
    {
        EnsureClassRegistered();

        _handle = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST,
            WindowClass,
            "Dalil",
            WINDOW_STYLE.WS_POPUP,
            0, 0, _config.Width, RequiredHeight(),
            HWND.Null, (SafeHandle?)null, (SafeHandle?)null, null);

        if (_handle.IsNull) return false;

        s_windows[(nint)_handle.Value] = this;
        _renderer = new GdiRenderer((nint)_handle.Value);

        RoundTheCorners();
        return true;
    }

    /// <summary>Replaces the rows on offer.</summary>
    public void SetEntries(IEnumerable<PaletteEntry> entries)
    {
        _model.SetEntries(entries);
        if (_open) Repaint();
    }

    /// <summary>Applies a reloaded configuration.</summary>
    public void Reconfigure(DalilConfig config)
    {
        _config = config;
        if (_open) Repaint();
    }

    /// <summary>
    /// Shows the palette on the right monitor and takes the keyboard.
    /// </summary>
    /// <remarks>
    /// The query is cleared on every open. A palette that remembers what was typed
    /// last time is one that shows a filtered list to someone who has just asked to
    /// see everything.
    /// </remarks>
    public void Open(PaletteMode mode = PaletteMode.Windows)
    {
        if (_handle.IsNull) return;

        _query = PrefixFor(mode);
        _model.SetQuery(_query);
        _closing = false;

        PositionOnTargetMonitor();

        PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_SHOW);
        PInvoke.SetForegroundWindow(_handle);
        PInvoke.SetFocus(_handle);

        _open = true;
        Repaint();
    }

    /// <summary>Hides the palette.</summary>
    public void Close()
    {
        if (!_open || _handle.IsNull) return;

        _closing = true;
        _open = false;

        PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_HIDE);
    }

    // ---- input ---------------------------------------------------------------

    /// <summary>A printable character was typed.</summary>
    private void OnCharacter(char value)
    {
        // Control characters arrive here too - Enter, Escape, Backspace all produce a
        // WM_CHAR - and every one of them is handled as a key rather than as text.
        if (char.IsControl(value)) return;

        ApplyQuery(_query + value);
    }

    /// <summary>
    /// Replaces the query, announcing a mode change if one fell out of it.
    /// </summary>
    /// <remarks>
    /// The single place the query changes, so no route into a mode can forget to
    /// refill the list. There are four: Tab, typing a prefix, deleting one, and
    /// choosing a row in the help list.
    /// </remarks>
    private void ApplyQuery(string query)
    {
        PaletteMode before = _model.Mode;

        _query = query;
        _model.SetQuery(_query);

        if (_model.Mode != before) ModeChanged?.Invoke(_model.Mode);

        Repaint();
    }

    /// <summary>A key that means something other than a character.</summary>
    /// <returns>Whether the key was handled here.</returns>
    private bool OnKey(VIRTUAL_KEY key)
    {
        bool control = IsDown(VIRTUAL_KEY.VK_CONTROL);
        bool shift = IsDown(VIRTUAL_KEY.VK_SHIFT);

        switch (key)
        {
            case VIRTUAL_KEY.VK_ESCAPE:
                Close();
                return true;

            case VIRTUAL_KEY.VK_RETURN:
                Choose();
                return true;

            case VIRTUAL_KEY.VK_BACK:
                Backspace(wholeWord: control);
                return true;

            case VIRTUAL_KEY.VK_UP:
                Move(-1);
                return true;

            case VIRTUAL_KEY.VK_DOWN:
                Move(1);
                return true;

            case VIRTUAL_KEY.VK_PRIOR:
                Move(-_config.VisibleRows);
                return true;

            case VIRTUAL_KEY.VK_NEXT:
                Move(_config.VisibleRows);
                return true;

            case VIRTUAL_KEY.VK_HOME when control:
                _model.SelectEdge(last: false);
                Repaint();
                return true;

            case VIRTUAL_KEY.VK_END when control:
                _model.SelectEdge(last: true);
                Repaint();
                return true;

            case VIRTUAL_KEY.VK_TAB:
                CycleMode(forward: !shift);
                return true;

            // The Emacs-style pair, because a palette is a text field and every other
            // text field on the machine honours them.
            case VIRTUAL_KEY.VK_N when control:
            case VIRTUAL_KEY.VK_J when control:
                Move(1);
                return true;

            case VIRTUAL_KEY.VK_P when control:
            case VIRTUAL_KEY.VK_K when control:
                Move(-1);
                return true;

            case VIRTUAL_KEY.VK_U when control:
                ApplyQuery(string.Empty);
                return true;

            default:
                return false;
        }
    }

    private void Move(int delta)
    {
        _model.MoveSelection(delta);
        Repaint();
    }

    /// <summary>
    /// Deletes backwards, by character or by word.
    /// </summary>
    /// <remarks>
    /// A mode prefix is deleted as one thing rather than left behind as a lone
    /// punctuation mark, which would leave the palette in a mode the user thought
    /// they had just backed out of.
    /// </remarks>
    private void Backspace(bool wholeWord)
    {
        if (_query.Length == 0) return;

        if (wholeWord)
        {
            int cut = _query.TrimEnd().LastIndexOf(' ');
            ApplyQuery(cut <= 0 ? string.Empty : _query[..(cut + 1)]);
            return;
        }

        ApplyQuery(_query[..^1]);
    }

    private void CycleMode(bool forward)
    {
        PaletteMode[] modes = Enum.GetValues<PaletteMode>();
        int at = Array.IndexOf(modes, _model.Mode);
        int next = ((at + (forward ? 1 : -1)) % modes.Length + modes.Length) % modes.Length;

        SwitchTo(modes[next]);
    }

    /// <summary>
    /// Acts on the selected row.
    /// </summary>
    /// <remarks>
    /// Closed first, and deliberately. The command usually raises another window, and
    /// a palette still on screen and still topmost when that happens covers the thing
    /// the user just asked to see.
    /// </remarks>
    private void Choose()
    {
        if (_model.Selected is not { } row) return;

        // A row that names a mode changes mode. This is what makes the help list
        // usable: somebody reading a list of keys will press Enter on the line they
        // want, and a help screen that ignores that has taught them the key and then
        // refused to honour it.
        if (row.Entry.SwitchesTo is { } mode)
        {
            SwitchTo(mode);
            return;
        }

        string command = row.Entry.Command;

        // A verb that needs arguments is offered as text to complete rather than run.
        // Running it bare would be rejected by the parser and read as a broken
        // palette. A help row that is only a key reference has nothing to run either,
        // and simply does nothing.
        if (command.Length == 0)
        {
            if (_model.Mode is PaletteMode.Help) return;

            _query = ">" + row.Entry.Primary + " ";
            _model.SetQuery(_query);
            Repaint();
            return;
        }

        Close();
        CommandRequested?.Invoke(command);
    }

    /// <summary>Changes mode and tells the host to refill the list.</summary>
    private void SwitchTo(PaletteMode mode)
    {
        _model.SetMode(mode);
        _query = _model.Query;

        ModeChanged?.Invoke(mode);
        Repaint();
    }

    // ---- placement -------------------------------------------------------------

    /// <summary>
    /// How tall the palette needs to be.
    /// </summary>
    /// <remarks>
    /// A search row, the results, and a hint bar. The hint bar is not optional: mode
    /// prefixes are punctuation, and punctuation nobody is shown is punctuation nobody
    /// finds.
    /// </remarks>
    private int RequiredHeight() =>
        (_config.RowHeight * (_config.VisibleRows + 1)) + PaletteRenderer.HintBarHeight + 18;

    /// <summary>
    /// Puts the palette on the monitor the user is looking at.
    /// </summary>
    /// <remarks>
    /// A palette that always opens on the primary monitor is one that appears on a
    /// different screen from the window the user was just using, which is a strange
    /// thing to do to somebody who has asked to find something.
    /// </remarks>
    private unsafe void PositionOnTargetMonitor()
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        HMONITOR monitor = TargetMonitor();

        if (!PInvoke.GetMonitorInfo(monitor, &info)) return;

        RECT work = info.rcWork;
        int width = Math.Min(_config.Width, work.right - work.left - 32);
        int height = RequiredHeight();

        // A third of the way down rather than centred: the eye goes there first, and
        // it leaves the window being searched for visible underneath.
        int x = work.left + ((work.right - work.left - width) / 2);
        int y = work.top + ((work.bottom - work.top - height) / 3);

        _bounds = new Rect(x, y, width, height);

        PInvoke.SetWindowPos(
            _handle, HWND.Null, x, y, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
    }

    private HMONITOR TargetMonitor()
    {
        const MONITOR_FROM_FLAGS Nearest = MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST;

        switch (_config.Placement)
        {
            case PalettePlacement.CursorMonitor:
                return PInvoke.GetCursorPos(out System.Drawing.Point point)
                    ? PInvoke.MonitorFromPoint(point, Nearest)
                    : PInvoke.MonitorFromWindow(_handle, Nearest);

            case PalettePlacement.Primary:
                return PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);

            case PalettePlacement.FocusedMonitor:
            default:
                // Read before the palette takes the foreground, so this is still the
                // window the user was working in.
                HWND foreground = PInvoke.GetForegroundWindow();

                return foreground.IsNull
                    ? PInvoke.MonitorFromWindow(_handle, Nearest)
                    : PInvoke.MonitorFromWindow(foreground, Nearest);
        }
    }

    /// <summary>Asks the compositor for rounded corners.</summary>
    /// <remarks>
    /// Windows 11 only, and failure is ignored. Mica is deliberately not requested:
    /// the palette fills its whole client area, so a backdrop drawn behind it would
    /// never be seen, and GDI cannot leave genuinely transparent pixels without a
    /// layered window.
    /// </remarks>
    private unsafe void RoundTheCorners()
    {
        const DWMWINDOWATTRIBUTE CornerPreference = (DWMWINDOWATTRIBUTE)33;
        const int Round = 2;

        int value = Round;
        PInvoke.DwmSetWindowAttribute(_handle, CornerPreference, &value, sizeof(int));
    }

    // ---- drawing ---------------------------------------------------------------

    private void Repaint()
    {
        if (_handle.IsNull) return;

        PInvoke.InvalidateRect(_handle, (RECT?)null, false);
        PInvoke.UpdateWindow(_handle);
    }

    private void Paint()
    {
        if (_renderer is null || _bounds.Width == 0) return;

        var canvas = new Rect(0, 0, _bounds.Width, _bounds.Height);

        _renderer.BeginFrame(canvas, _config.Background);

        try
        {
            PaletteRenderer.Draw(_renderer, _model, _config, canvas);
        }
        finally
        {
            _renderer.EndFrame();
        }
    }

    // ---- window plumbing --------------------------------------------------------

    private static string PrefixFor(PaletteMode mode) =>
        PaletteModel.PrefixFor(mode) is var prefix && prefix != '\0'
            ? prefix.ToString()
            : string.Empty;

    private static bool IsDown(VIRTUAL_KEY key) => (PInvoke.GetKeyState((int)key) & 0x8000) != 0;

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

                // Every pixel comes from the off-screen buffer. Letting Windows erase
                // first is a visible flash on a window that opens and closes as often
                // as this one.
                hbrBackground = HBRUSH.Null,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
            };

            if (PInvoke.RegisterClassEx(in wc) == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (s_windows.TryGetValue((nint)hwnd.Value, out PaletteWindow? window))
            {
                switch (message)
                {
                    case PInvoke.WM_PAINT:
                        PInvoke.BeginPaint(hwnd, out PAINTSTRUCT ps);
                        window.Paint();
                        PInvoke.EndPaint(hwnd, in ps);
                        return new LRESULT(0);

                    case PInvoke.WM_CHAR:
                        window.OnCharacter((char)wParam.Value);
                        return new LRESULT(0);

                    case PInvoke.WM_KEYDOWN:
                    case PInvoke.WM_SYSKEYDOWN:
                        if (window.OnKey((VIRTUAL_KEY)(ushort)wParam.Value)) return new LRESULT(0);
                        break;

                    case PInvoke.WM_ACTIVATE:
                        // WA_INACTIVE. The user clicked elsewhere, or something else
                        // took the foreground - either way the palette has been
                        // dismissed. Not when it is giving focus away itself, which
                        // produces the identical message.
                        if ((wParam.Value & 0xFFFF) == 0 && window._config.CloseOnBlur && !window._closing)
                            window.Close();

                        return new LRESULT(0);

                    case PInvoke.WM_CLOSE:
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
            // An exception escaping an UnmanagedCallersOnly callback tears the process
            // down. A missed keystroke is better than a palette that vanishes.
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _renderer?.Dispose();

        if (!_handle.IsNull)
        {
            s_windows.Remove((nint)_handle.Value);
            PInvoke.DestroyWindow(_handle);
            _handle = HWND.Null;
        }
    }
}
