using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
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

    /// <summary>
    /// Raised when Windows says the accent colour has changed.
    /// </summary>
    /// <remarks>
    /// Static for the same reason as the others: the answer is about the machine, not
    /// about one bar. A config that writes <c>accent</c> resolved it when it was read,
    /// so following the change means reading the file again - which the loop already
    /// knows how to do, and does for this the way it does for a saved file.
    /// </remarks>
    public static event Action? SystemColoursChanged;

    private readonly BarModel _model;

    /// <summary>
    /// Which display this bar sits on, for the log: the tail of the GDI device name,
    /// <c>DISPLAY2</c>.
    /// </summary>
    /// <remarks>
    /// Was a position. Positions shift when a monitor is unplugged - every bar after
    /// it moves down one - so "bar 1" in a log written across a dock and an undock
    /// named two different displays, and the device name is what the window manager's
    /// own log calls the same display.
    /// </remarks>
    private readonly string _label;

    private HWND _handle;
    private CompositedGdiRenderer? _renderer;
    private FlexLayout? _layout;
    private VisualNode? _tree;

    /// <summary>The display the bar is on, as last told.</summary>
    private Rect _monitor;

    /// <summary>The window's rectangle: the bar as drawn.</summary>
    private Rect _bounds;

    /// <summary>
    /// The rectangle reserved from other windows, which contains <see cref="_bounds"/>.
    /// </summary>
    /// <remarks>
    /// The same as the window for a docked bar. A floating bar reserves its margin
    /// on both sides of itself as well, so it sits in the middle of the gap it makes.
    /// </remarks>
    private Rect _strip;

    /// <summary>What the compositor was last asked for, so it is asked once per change.</summary>
    private Look? _look;

    private bool _appbarRegistered;
    private bool _refusalReported;
    private VisualNode? _hovered;
    private bool _mouseTracked;
    private bool _disposed;

    /// <summary>Raised when a widget is clicked, with the command to run.</summary>
    public event Action<string>? CommandRequested;

    /// <param name="model">The bar model to draw.</param>
    /// <param name="deviceId">The GDI device name of the display this bar is for.</param>
    public BarWindow(BarModel model, string deviceId)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        _label = deviceId.LastIndexOf('\\') is var slash && slash >= 0 ? deviceId[(slash + 1)..] : deviceId;
    }

    public unsafe nint Handle => (nint)_handle.Value;

    /// <summary>The display this bar sits on, as the log names it.</summary>
    public string Label => _label;

    /// <summary>Creates the window on the given monitor work area.</summary>
    public unsafe bool Create(Rect monitorBounds)
    {
        EnsureClassRegistered();

        BarProfile profile = _model.Profile;

        _monitor = monitorBounds;
        (_strip, _bounds) = Geometry(monitorBounds, profile);

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

        _renderer = new CompositedGdiRenderer((nint)_handle.Value);
        _layout = new FlexLayout(_renderer);

        RegisterAppbar();
        HonourAlpha();
        ApplyLook(profile);

        PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        return true;
    }

    /// <summary>Rebuilds and repaints if the model has changed.</summary>
    public void Update()
    {
        if (_handle.IsNull || _renderer is null || _layout is null) return;

        if (!_model.IsDirty && _tree is not null) return;

        BarProfile profile = _model.Profile;

        // The shape can change when the profile does, e.g. a presentation profile
        // with a slimmer bar, or one that floats where the default is docked.
        (Rect strip, Rect bounds) = Geometry(_monitor, profile);
        if (strip != _strip || bounds != _bounds) Place(strip, bounds);

        ApplyLook(profile);

        _tree = _model.Build();
        _layout.Arrange(_tree, _bounds with { X = 0, Y = 0 });

        // The hovered node belongs to the tree that was just thrown away. Found again
        // by id in the new one, or the highlight lasted exactly until the clock next
        // ticked - half a second - and the hand cursor stayed while the pill went
        // flat, which read as the bar changing its mind about whether it was a control.
        if (_hovered is { } stale)
            _hovered = _tree.SelfAndDescendants().FirstOrDefault(n => n.HoverStyle is not null && n.Id == stale.Id);

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.Debug(LogCategory.Wm,
                $"bar {_label} laid out at {_bounds.Width}x{_bounds.Height}: " +
                string.Join(", ", _tree.SelfAndDescendants()
                    .Where(n => n.Visible && !n.Rect.IsEmpty && n.Kind == VisualKind.Text)
                    .Select(n => $"{n.Id}@{n.Rect.Left}..{n.Rect.Right}")));
        }

        PInvoke.InvalidateRect(_handle, (RECT?)null, false);
        PInvoke.UpdateWindow(_handle);
    }

    /// <summary>
    /// Where a profile puts its bar on a display: the strip it reserves, and the
    /// window inside that strip.
    /// </summary>
    /// <remarks>
    /// A docked bar is its strip. A floating one is inset by its margin from the
    /// screen edge and from both sides, and the strip is deepened by the same margin
    /// on the inner side, so the room above the bar and the room below it match
    /// without the window manager's gaps having to know about either.
    /// </remarks>
    private static (Rect Strip, Rect Window) Geometry(Rect monitor, BarProfile profile)
    {
        int margin = Math.Max(0, profile.Margin);
        int depth = profile.Height + (2 * margin);

        Rect strip = profile.Edge == BarEdge.Top
            ? new Rect(monitor.X, monitor.Y, monitor.Width, depth)
            : new Rect(monitor.X, monitor.Bottom - depth, monitor.Width, depth);

        var window = new Rect(
            strip.X + margin,
            strip.Y + margin,
            Math.Max(0, strip.Width - (2 * margin)),
            profile.Height);

        return (strip, window);
    }

    /// <summary>Moves the window and re-reserves its strip.</summary>
    private void Place(Rect strip, Rect bounds)
    {
        _strip = strip;
        _bounds = bounds;

        PInvoke.SetWindowPos(
            _handle, HWND.Null, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);

        RegisterAppbar();

        Log.Info(LogCategory.Wm, $"bar {_label} placed at {_bounds}, reserving {_strip}");
    }

    /// <summary>
    /// Moves the bar to a display that has changed shape or position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A resolution change, a scaling change, or a monitor to the left being unplugged
    /// so that this one's origin moves to zero: the display is the same, the rectangle
    /// is not. Until this existed the bar stayed where it was created, which after an
    /// undock could be off the edge of every attached display.
    /// </para>
    /// <para>
    /// The strip is reserved again at the new place. The shell keys a reservation on
    /// the window, not the rectangle, so <c>ABM_SETPOS</c> with the new one is a move
    /// rather than a second reservation.
    /// </para>
    /// </remarks>
    /// <returns>Whether anything moved.</returns>
    public bool Relocate(Rect monitorBounds)
    {
        if (_handle.IsNull) return false;

        _monitor = monitorBounds;

        (Rect strip, Rect bounds) = Geometry(monitorBounds, _model.Profile);

        if (strip == _strip && bounds == _bounds) return false;

        Place(strip, bounds);

        // The tree was laid out for the old width. Dropping it makes the next Update
        // rebuild, whether or not the model has changed.
        _tree = null;

        return true;
    }

    private void Paint()
    {
        if (_renderer is null || _tree is null) return;

        // Cleared to nothing rather than to the profile's background: the root node
        // paints the surface, with its corners and border, and painting a translucent
        // colour twice would double it. What the root leaves bare stays see-through.
        VisualPainter.Paint(
            _renderer, _tree, _bounds with { X = 0, Y = 0 }, Colour.Transparent, _hovered);
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

        // The cursor as well as the highlight. WM_SETCURSOR arrives before the
        // WM_MOUSEMOVE that moves the highlight, so it reads the state one movement
        // behind - and the last movement before the pointer comes to rest is the one
        // that shows. Setting it here, after the highlight moved, is what makes the
        // pointer at rest over a pill a hand and at rest beside one an arrow.
        PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, hovered is not null ? PInvoke.IDC_HAND : PInvoke.IDC_ARROW));

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
                left = _strip.Left,
                top = _strip.Top,
                right = _strip.Right,
                bottom = _strip.Bottom,
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
                        $"the shell refused bar {_label}'s reservation; will keep trying");
                }

                return;
            }

            if (_refusalReported)
            {
                _refusalReported = false;

                Log.Info(LogCategory.Wm, $"bar {_label}'s strip is reserved again");
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

    /// <summary>
    /// Tells the shell the bar has moved, so it keeps an auto-hiding taskbar above
    /// ordinary windows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Half of a documented obligation that registering an appbar takes on, and the
    /// half with a symptom that lands nowhere near the bar. From <i>Using Application
    /// Desktop Toolbars</i>: "when an appbar receives a <c>WM_WINDOWPOSCHANGED</c>
    /// message, it must call <c>ABM_WINDOWPOSCHANGED</c>. Sending these messages
    /// ensures that the system properly sets the z-order of any autohide appbars."
    /// </para>
    /// <para>
    /// The shell does not track an appbar's rectangle by watching it. It is told, and
    /// an appbar that moves without saying so leaves the shell recomputing the
    /// auto-hide z-order from a stale picture of the desktop. What breaks is the
    /// taskbar: it still slides out on a hover at the screen edge, because sliding out
    /// is its own animation, but it slides out <i>underneath</i> whatever is in front -
    /// which under a tiling window manager is a window covering the whole work area,
    /// permanently. The taskbar comes back the moment anything else makes the shell
    /// re-assert it, which is why pressing the Windows key appears to fix it, and why
    /// it appears to fix it on that monitor only.
    /// </para>
    /// <para>
    /// Sent even when the reservation is not held. The shell ignores it for a window
    /// it has never heard of, and the alternative is a gap between the window existing
    /// and <c>ABM_NEW</c> being accepted in which the bar moves silently.
    /// </para>
    /// </remarks>
    private unsafe void NotifyAppbarMoved()
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)sizeof(APPBARDATA),
            hWnd = _handle,
        };

        const uint AbmWindowPosChanged = 0x00000009;
        PInvoke.SHAppBarMessage(AbmWindowPosChanged, ref data);
    }

    /// <summary>
    /// Tells the shell the bar has been activated or deactivated, for the same reason
    /// as <see cref="NotifyAppbarMoved"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of the same sentence, and the rarer one: the bar is
    /// <c>WS_EX_NOACTIVATE</c>, so the message it answers mostly does not arrive.
    /// Mostly is not never - a click on a widget is a real interaction with a real
    /// window - and an appbar that answers one half of the contract and not the other
    /// is relying on a distinction the documentation does not draw.
    /// </para>
    /// <para>
    /// Which way round it is has to be said, and said in <c>lParam</c>: the shell
    /// reads the answer out of the structure rather than inferring it from the message
    /// that prompted it, so a deactivation and an activation are the same call with
    /// one field differing. Left unset, every <c>WM_ACTIVATE</c> would report a
    /// deactivation - a wrong answer stated confidently, which leaves the shell worse
    /// off than the silence this replaced.
    /// </para>
    /// </remarks>
    /// <param name="active">Whether the bar is being activated rather than deactivated.</param>
    private unsafe void NotifyAppbarActivated(bool active)
    {
        var data = new APPBARDATA
        {
            cbSize = (uint)sizeof(APPBARDATA),
            hWnd = _handle,
            lParam = new LPARAM(active ? 1 : 0),
        };

        const uint AbmActivate = 0x00000006;
        PInvoke.SHAppBarMessage(AbmActivate, ref data);
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
        Log.Info(LogCategory.Wm, $"the shell restarted; reserving bar {_label}'s strip again");

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

    // ---- the compositor ----------------------------------------------------

    /// <summary>
    /// Tells the compositor to honour the alpha channel of what the bar draws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain top-level window is composed as opaque whatever its pixels say, so a
    /// translucent background painted into it would simply come out solid. The
    /// blur-behind call with an <i>empty</i> region is the documented way of asking
    /// for anything else: the blur itself has not existed since Windows 8, but the
    /// side effect - the window's own alpha being respected - has, and it is what
    /// every transparent window toolkit on Windows does. It costs nothing when every
    /// pixel is opaque, which is how an ordinary solid bar comes out of the renderer.
    /// </para>
    /// <para>
    /// Asked once, at creation. Whether any pixel actually is translucent is then
    /// the profile's business, and a profile switch needs no window work.
    /// </para>
    /// </remarks>
    private void HonourAlpha()
    {
        HRGN empty = PInvoke.CreateRectRgn(0, 0, -1, -1);

        var blur = new DWM_BLURBEHIND
        {
            dwFlags = PInvoke.DWM_BB_ENABLE | PInvoke.DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = empty,
        };

        // Failure is ignored: without composition there is no transparency to have,
        // and the bar draws opaque over its own background as it always did.
        _ = PInvoke.DwmEnableBlurBehindWindow(_handle, in blur);

        if (!empty.IsNull) PInvoke.DeleteObject(empty);
    }

    /// <summary>What the compositor is asked for on the bar's behalf.</summary>
    /// <param name="Backdrop">The material behind translucent pixels.</param>
    /// <param name="Dark">Whether the material should be its dark variant.</param>
    /// <param name="Corners">The corner preference, in the compositor's own numbering.</param>
    private readonly record struct Look(BarBackdrop Backdrop, bool Dark, int Corners);

    /// <summary>
    /// Asks the compositor for the profile's backdrop, corners and tint, when they
    /// differ from what it was last asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows 11 only, and failure is ignored: on Windows 10 the bar simply uses its
    /// configured background colour, which is a perfectly good bar - a translucent one
    /// still, since that part needs no material, only <see cref="HonourAlpha"/>.
    /// </para>
    /// <para>
    /// The dark-mode hint is set from the background's own lightness rather than
    /// from the system theme. It decides whether Mica and Acrylic come out as their
    /// dark or their light variant, and a dark bar over the light variant is muddy -
    /// while the bar's colour is the one thing the config has actually said.
    /// </para>
    /// <para>
    /// Corners are the compositor's to round only when there is a material to clip:
    /// a backdrop fills the window's whole rectangle, and without the clip a rounded
    /// bar would sit on square acrylic. The compositor offers two sizes, and the
    /// nearer one to the profile's radius is chosen; without a backdrop the bar's own
    /// anti-aliased corners are the shape, exactly as drawn, and the compositor is told
    /// to leave them alone.
    /// </para>
    /// </remarks>
    private unsafe void ApplyLook(BarProfile profile)
    {
        const int DoNotRound = 1;
        const int Round = 2;
        const int RoundSmall = 3;

        bool clipToMaterial = profile.IsFloating && profile.Radius > 0 && profile.Backdrop != BarBackdrop.None;

        var look = new Look(
            profile.Backdrop,
            IsDark(profile.Background),
            clipToMaterial ? (profile.Radius >= 6 ? Round : RoundSmall) : DoNotRound);

        if (look == _look) return;
        _look = look;

        const DWMWINDOWATTRIBUTE UseImmersiveDarkMode = (DWMWINDOWATTRIBUTE)20;
        const DWMWINDOWATTRIBUTE CornerPreference = (DWMWINDOWATTRIBUTE)33;
        const DWMWINDOWATTRIBUTE SystemBackdropType = (DWMWINDOWATTRIBUTE)38;

        int dark = look.Dark ? 1 : 0;
        int corners = look.Corners;
        int backdrop = (int)look.Backdrop;

        _ = PInvoke.DwmSetWindowAttribute(_handle, UseImmersiveDarkMode, &dark, sizeof(int));
        _ = PInvoke.DwmSetWindowAttribute(_handle, CornerPreference, &corners, sizeof(int));
        _ = PInvoke.DwmSetWindowAttribute(_handle, SystemBackdropType, &backdrop, sizeof(int));
    }

    /// <summary>Whether a colour reads as dark: relative luminance under a half.</summary>
    private static bool IsDark(Colour colour) =>
        ((0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B)) / 255.0 < 0.5;

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
                        // EndPaint whatever Paint does. Without it a throw skipped the
                        // call that validates the update region, so Windows posted the
                        // WM_PAINT again at once, for ever: a core spent on a bar that
                        // never painted, with nothing in the log to say so.
                        PInvoke.BeginPaint(hwnd, out PAINTSTRUCT ps);

                        try
                        {
                            window.Paint();
                        }
                        finally
                        {
                            PInvoke.EndPaint(hwnd, in ps);
                        }

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

                    // The hand over anything that can be clicked, the arrow over
                    // everything else. The class cursor is the arrow; saying so here
                    // for the rest stops the hand lingering after the pointer has left
                    // a control for a readout beside it.
                    case PInvoke.WM_SETCURSOR:
                        PInvoke.SetCursor(PInvoke.LoadCursor(
                            HINSTANCE.Null, window._hovered is not null ? PInvoke.IDC_HAND : PInvoke.IDC_ARROW));
                        return new LRESULT(1);

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

                    // Both of these are told to the shell and then handed on rather
                    // than answered. Returning zero from WM_WINDOWPOSCHANGED without
                    // reaching DefWindowProc suppresses the WM_SIZE and WM_MOVE it is
                    // responsible for synthesising, so a handler that swallows it has
                    // quietly broken every message that comes after.
                    case PInvoke.WM_WINDOWPOSCHANGED:
                        window.NotifyAppbarMoved();
                        break;

                    case PInvoke.WM_ACTIVATE:
                    {
                        // The state is the low word. The high word says whether the
                        // window was minimised, which this one never is.
                        uint state = (uint)wParam.Value & 0xFFFF;
                        window.NotifyAppbarActivated(state != PInvoke.WA_INACTIVE);
                        break;
                    }

                    // Broadcast to every top-level window when the accent changes, in
                    // Settings or by the wallpaper. Said once to the loop rather than
                    // acted on per bar: one accent, one reload, however many displays.
                    case PInvoke.WM_DWMCOLORIZATIONCOLORCHANGED:
                        SystemColoursChanged?.Invoke();
                        return new LRESULT(0);

                    case PInvoke.WM_CLOSE:
                        // Closing any bar closes the bar. There is one message loop
                        // behind however many monitors, so a window going is the
                        // process going - and without this, closing the window left
                        // Taj running with nothing to show, which is how `taj-exit`
                        // and Task Manager's "End task" both used to do nothing.
                        RequestShutdown?.Invoke();
                        return new LRESULT(0);

                    // The session is ending, or an installer is replacing the files
                    // under this process and has asked it to leave (Restart Manager,
                    // which is what a silent `winget upgrade` runs). The first is a
                    // question, answered yes; the second is the moment to go, and a
                    // bar that goes when asked is one the installer does not have to
                    // kill. Nothing here needs saving; the appbar reservation is
                    // given back on the way out of the loop when there is time.
                    case PInvoke.WM_QUERYENDSESSION:
                        return new LRESULT(1);

                    case PInvoke.WM_ENDSESSION:
                        if (wParam.Value != 0) RequestShutdown?.Invoke();
                        return new LRESULT(0);

                    case PInvoke.WM_DESTROY:
                        s_windows.Remove((nint)hwnd.Value);
                        return new LRESULT(0);

                    default:
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // An exception escaping an UnmanagedCallersOnly callback tears down the
            // process, and a crashed bar is worse than a missed repaint. Said out loud,
            // though: a bar whose every click and paint failed silently was a bar that
            // looked broken for no reason anyone could find in the log.
            Log.Error(LogCategory.Ui, $"the bar's window procedure failed handling message 0x{message:X4}", ex);
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
