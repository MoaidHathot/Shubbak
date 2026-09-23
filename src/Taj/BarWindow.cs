using Shubbak.Companion;
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
public sealed class BarWindow : CompanionWindow
{
    private const string WindowClass = "TajBarWindow";
    private const uint AppbarCallbackMessage = PInvoke.WM_APP + 1;

    /// <summary>
    /// Broadcast to every top-level window when Explorer restarts.
    /// </summary>
    /// <remarks>
    /// Registered once. The system allocates the same value for every process that
    /// asks, which is how one broadcast reaches all of them.
    /// </remarks>
    private static uint s_taskbarCreated;

    /// <summary>
    /// Raised when the shell says a full-screen application has opened or closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static because the answer is about the desktop rather than about one bar: a
    /// game on one monitor covers that bar, and the loop that drives all of them is
    /// one loop.
    /// </para>
    /// <para>
    /// True when one opens, false when one closes. Deliberately treated as a hint
    /// rather than as the truth - see <c>StandDown.StillCovered</c>.
    /// </para>
    /// </remarks>
    public static event Action<bool>? FullScreenAppChanged;

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

    private CompositedGdiRenderer? _renderer;
    private FlexLayout? _layout;
    private VisualNode? _tree;

    /// <summary>The window as the bar's own manifest spells it; see <see cref="CompanionWindow.Handle"/>.</summary>
    private HWND Hwnd => new(Handle);

    /// <summary>The display the bar is on, as last told.</summary>
    private Rect _monitor;

    /// <summary>
    /// Pixels per device-independent pixel on this display: 1 at 96 DPI, 1.5 at 144.
    /// Every size the profile gives is in device-independent pixels and is multiplied
    /// by this on the way to the screen; see <see cref="VisualScaling"/>. Exactly 1
    /// when the configuration turns scaling off.
    /// </summary>
    private double _scale = 1.0;

    /// <summary>Whether sizes are scaled to the display at all; <c>bar { dpi-scaling }</c>.</summary>
    private readonly bool _scalesToDpi;

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

    /// <summary>
    /// The strip the profile asked for, before the shell had its say. Compared against
    /// what the profile asks for on every pass; <see cref="_strip"/> is what the shell
    /// granted, which may differ and must not be compared, or a bar the shell moved
    /// would be moved back and forth on every turn of the loop.
    /// </summary>
    private Rect _requestedStrip;

    /// <summary>What the compositor was last asked for, so it is asked once per change.</summary>
    private Look? _look;

    private bool _appbarRegistered;
    private bool _refusalReported;
    private VisualNode? _hovered;

    /// <summary>Raised when a widget is clicked, with the command to run.</summary>
    public event Action<string>? CommandRequested;

    /// <param name="model">The bar model to draw.</param>
    /// <param name="deviceId">The GDI device name of the display this bar is for.</param>
    /// <param name="scalesToDpi">Whether sizes are device-independent pixels scaled to the display, or raw pixels.</param>
    public BarWindow(BarModel model, string deviceId, bool scalesToDpi = true)
        : base(new WindowClassOptions(WindowClass))
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        _scalesToDpi = scalesToDpi;

        _label = deviceId.LastIndexOf('\\') is var slash && slash >= 0 ? deviceId[(slash + 1)..] : deviceId;
    }

    /// <summary>The display this bar sits on, as the log names it.</summary>
    public string Label => _label;

    /// <summary>Creates the window on the given monitor.</summary>
    /// <param name="monitorBounds">The display's rectangle, in screen pixels.</param>
    /// <param name="dpi">The display's DPI, which decides how large a device-independent pixel is here.</param>
    public bool Create(Rect monitorBounds, uint dpi)
    {
        BarProfile profile = _model.Profile;

        _monitor = monitorBounds;
        _scale = ScaleFor(dpi);
        (_strip, _bounds) = Geometry(monitorBounds, profile, _scale);
        _requestedStrip = _strip;

        if (!CreateWindow(
                (uint)(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE),
                (uint)WINDOW_STYLE.WS_POPUP,
                "Taj",
                _bounds))
        {
            return false;
        }

        AllowShellRestartBroadcast();

        _renderer = new CompositedGdiRenderer(Handle);
        _layout = new FlexLayout(_renderer);

        RegisterAppbar();
        HonourAlpha();
        ApplyLook(profile);

        ShowWithoutActivating();

        return true;
    }

    /// <summary>Rebuilds and repaints if the model has changed.</summary>
    public void Update()
    {
        if (!Exists || _renderer is null || _layout is null) return;

        if (!_model.IsDirty && _tree is not null) return;

        BarProfile profile = _model.Profile;

        // The shape can change when the profile does, e.g. a presentation profile
        // with a slimmer bar, or one that floats where the default is docked.
        (Rect strip, Rect bounds) = Geometry(_monitor, profile, _scale);
        if (strip != _requestedStrip) Place(strip, bounds);

        ApplyLook(profile);

        // Built in device-independent pixels, scaled to this display, then laid out -
        // so the text is measured in the font it is drawn in.
        _tree = _model.Build();
        VisualScaling.Scale(_tree, _scale);
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

        Repaint();
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
    /// <param name="monitor">The display's rectangle, in screen pixels.</param>
    /// <param name="profile">The profile, whose sizes are in device-independent pixels.</param>
    /// <param name="scale">Pixels per device-independent pixel on this display.</param>
    internal static (Rect Strip, Rect Window) Geometry(Rect monitor, BarProfile profile, double scale = 1.0)
    {
        int margin = VisualScaling.Scale(Math.Max(0, profile.Margin), scale);
        int height = VisualScaling.Scale(profile.Height, scale);
        int depth = height + (2 * margin);

        Rect strip = profile.Edge == BarEdge.Top
            ? new Rect(monitor.X, monitor.Y, monitor.Width, depth)
            : new Rect(monitor.X, monitor.Bottom - depth, monitor.Width, depth);

        var window = new Rect(
            strip.X + margin,
            strip.Y + margin,
            Math.Max(0, strip.Width - (2 * margin)),
            height);

        return (strip, window);
    }

    /// <summary>The scale for a display, or exactly 1 when the configuration says sizes are raw pixels.</summary>
    private double ScaleFor(uint dpi) => _scalesToDpi ? VisualScaling.FactorFor(dpi) : 1.0;

    /// <summary>Moves the window and re-reserves its strip.</summary>
    private void Place(Rect strip, Rect bounds)
    {
        _requestedStrip = strip;
        _strip = strip;
        _bounds = bounds;

        PInvoke.SetWindowPos(
            Hwnd, HWND.Null, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
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
    /// <param name="monitorBounds">The display's rectangle, in screen pixels.</param>
    /// <param name="dpi">The display's DPI now, which a scaling change alters without the rectangle moving.</param>
    /// <returns>Whether anything moved.</returns>
    public bool Relocate(Rect monitorBounds, uint dpi)
    {
        if (!Exists) return false;

        _monitor = monitorBounds;

        double scale = ScaleFor(dpi);
        bool rescaled = Math.Abs(scale - _scale) > 0.0005;
        _scale = scale;

        (Rect strip, Rect bounds) = Geometry(monitorBounds, _model.Profile, _scale);

        if (strip == _requestedStrip && !rescaled) return false;

        if (strip != _requestedStrip) Place(strip, bounds);

        // The tree was laid out for the old width, or the old scale. Dropping it makes
        // the next Update rebuild, whether or not the model has changed.
        _tree = null;

        if (rescaled) Log.Info(LogCategory.Wm, $"bar {_label} now draws at {_scale:F2}x ({dpi} dpi)");

        return true;
    }

    /// <summary>
    /// Windows says this window's display changed DPI - a scaling change in Settings,
    /// or the display being replaced by one of a different density under the same
    /// name. The bar is re-sized and re-drawn at the new scale.
    /// </summary>
    protected override void OnDpiChanged(uint dpi, Rect suggested) => Relocate(_monitor, dpi);

    protected override void OnPaint()
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
    protected override void OnMouseMove(int x, int y)
    {
        _lastMouse = (x, y);

        VisualNode? hovered = Interactive(_tree?.HitTest(x, y));

        if (ReferenceEquals(hovered, _hovered)) return;

        _hovered = hovered;

        // The cursor as well as the highlight. WM_SETCURSOR arrives before the
        // WM_MOUSEMOVE that moves the highlight, so it reads the state one movement
        // behind - and the last movement before the pointer comes to rest is the one
        // that shows. Setting it here, after the highlight moved, is what makes the
        // pointer at rest over a pill a hand and at rest beside one an arrow.
        PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, hovered is not null ? PInvoke.IDC_HAND : PInvoke.IDC_ARROW));

        PInvoke.InvalidateRect(Hwnd, (RECT?)null, false);
    }

    protected override void OnMouseLeave()
    {
        _lastMouse = (-1, -1);

        if (_hovered is null) return;

        _hovered = null;

        PInvoke.InvalidateRect(Hwnd, (RECT?)null, false);
    }

    /// <summary>The hand over anything that can be clicked; see <see cref="CompanionWindow"/>.</summary>
    protected override bool ShowsHandCursor => _hovered is not null;

    /// <summary>The nearest ancestor that reacts to the pointer, if any.</summary>
    private VisualNode? Interactive(VisualNode? node)
    {
        if (_tree is null) return null;

        for (VisualNode? current = node; current is not null; current = FindParent(_tree, current))
            if (current.HoverStyle is not null) return current;

        return null;
    }

    /// <summary>Where the pointer last was, for the wheel, which Windows reports in screen coordinates.</summary>
    private (int X, int Y) _lastMouse = (-1, -1);

    protected override void OnMouseDown(MouseButton button, int x, int y) =>
        Perform(x, y, button switch
        {
            MouseButton.Left => static n => n.OnClick,
            MouseButton.Right => static n => n.OnRightClick,
            MouseButton.Middle => static n => n.OnMiddleClick,
            _ => static _ => null,
        });

    /// <summary>
    /// The wheel over a widget. The gesture lands where the pointer last moved, since
    /// the wheel message carries screen coordinates and the pointer has to be over the
    /// bar for the bar to receive it at all.
    /// </summary>
    protected override void OnWheel(int delta)
    {
        if (delta == 0) return;

        Perform(_lastMouse.X, _lastMouse.Y, delta > 0 ? static n => n.OnScrollUp : static n => n.OnScrollDown);
    }

    /// <summary>
    /// Runs the command a gesture names on the widget under the pointer, or on the
    /// nearest ancestor that names one - a click usually lands on a text node inside
    /// the element that carries the command.
    /// </summary>
    private void Perform(int x, int y, Func<VisualNode, string?> command)
    {
        if (x < 0 || _tree?.HitTest(x, y) is not { } node) return;

        for (VisualNode? current = node; current is not null; current = FindParent(_tree, current))
        {
            if (command(current) is { Length: > 0 } run)
            {
                CommandRequested?.Invoke(run);
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
            hWnd = Hwnd,
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

        // The documented handshake: propose the rectangle with ABM_QUERYPOS, which the
        // shell adjusts if another appbar - a taskbar docked to the same edge - is
        // already there; then reserve what came back with ABM_SETPOS, which the shell
        // may adjust once more; then put the window where the reservation actually is.
        // Asking for a strip and drawing the bar in it regardless of the answer had the
        // bar sitting under a top-docked taskbar, with its own strip reserved below
        // the taskbar's where nothing was drawn.
        const uint AbmQueryPos = 0x00000002;

        PInvoke.SHAppBarMessage(AbmQueryPos, ref data);
        PInvoke.SHAppBarMessage(AbmSetPos, ref data);

        Rect granted = Rect.FromEdges(data.rc.left, data.rc.top, data.rc.right, data.rc.bottom);

        if (granted != _strip && !granted.IsEmpty)
            FollowTheShell(granted);
    }

    /// <summary>
    /// Moves the window into the strip the shell granted, when it is not the one that
    /// was asked for.
    /// </summary>
    /// <remarks>
    /// The window keeps its height and its margin; only where the strip is changes. The
    /// requested strip is left as it was, so the next pass does not read the difference
    /// as the profile asking to move.
    /// </remarks>
    private void FollowTheShell(Rect granted)
    {
        int margin = VisualScaling.Scale(Math.Max(0, _model.Profile.Margin), _scale);

        _strip = granted;
        _bounds = new Rect(
            granted.X + margin,
            granted.Y + margin,
            Math.Max(0, granted.Width - (2 * margin)),
            _bounds.Height);

        PInvoke.SetWindowPos(
            Hwnd, HWND.Null, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);

        // The tree was laid out for the old rectangle.
        _tree = null;

        Log.Info(LogCategory.Wm, $"bar {_label}: the shell granted {_strip} rather than {_requestedStrip}; following it");
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
        if (_appbarRegistered || !Exists) return;

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
            hWnd = Hwnd,
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
            hWnd = Hwnd,
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
            hWnd = Hwnd,
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
            Hwnd, s_taskbarCreated, WINDOW_MESSAGE_FILTER_ACTION.MSGFLT_ALLOW, null);
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
            Hwnd, band, 0, 0, 0, 0,
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
        _ = PInvoke.DwmEnableBlurBehindWindow(Hwnd, in blur);

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
            profile.Background.IsDark,
            clipToMaterial ? (profile.Radius >= 6 ? Round : RoundSmall) : DoNotRound);

        if (look == _look) return;
        _look = look;

        const DWMWINDOWATTRIBUTE UseImmersiveDarkMode = (DWMWINDOWATTRIBUTE)20;
        const DWMWINDOWATTRIBUTE CornerPreference = (DWMWINDOWATTRIBUTE)33;
        const DWMWINDOWATTRIBUTE SystemBackdropType = (DWMWINDOWATTRIBUTE)38;

        int dark = look.Dark ? 1 : 0;
        int corners = look.Corners;
        int backdrop = (int)look.Backdrop;

        HWND hwnd = Hwnd;
        _ = PInvoke.DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, &dark, sizeof(int));
        _ = PInvoke.DwmSetWindowAttribute(hwnd, CornerPreference, &corners, sizeof(int));
        _ = PInvoke.DwmSetWindowAttribute(hwnd, SystemBackdropType, &backdrop, sizeof(int));
    }

    // ---- window plumbing ---------------------------------------------------

    /// <summary>
    /// The messages the bar answers itself: the shell's appbar callback, the
    /// Explorer-restart broadcast, and the two the shell must be told about.
    /// Everything else - painting, the pointer, closing, the session ending, the
    /// accent changing - is the base class's.
    /// </summary>
    protected override bool OnMessage(uint message, nuint wParam, nint lParam, out nint result)
    {
        result = 0;

        // Ahead of the switch because its value is allocated at run time by
        // RegisterWindowMessage, and a case label has to be a constant.
        if (s_taskbarCreated != 0 && message == s_taskbarCreated)
        {
            OnShellRestarted();
            return true;
        }

        switch (message)
        {
            case AppbarCallbackMessage:
                switch (wParam)
                {
                    case AppbarNotification.PositionChanged:
                        OnAppbarPositionChanged();
                        break;

                    case AppbarNotification.FullScreenApp:
                        OnFullScreenApp(lParam != 0);
                        break;

                    default:
                        break;
                }

                return true;

            // Both of these are told to the shell and then handed on rather than
            // answered. Returning zero from WM_WINDOWPOSCHANGED without reaching
            // DefWindowProc suppresses the WM_SIZE and WM_MOVE it is responsible for
            // synthesising, so a handler that swallows it has quietly broken every
            // message that comes after.
            case PInvoke.WM_WINDOWPOSCHANGED:
                NotifyAppbarMoved();
                return false;

            case PInvoke.WM_ACTIVATE:
            {
                // The state is the low word. The high word says whether the window
                // was minimised, which this one never is.
                uint state = (uint)wParam & 0xFFFF;
                NotifyAppbarActivated(state != PInvoke.WA_INACTIVE);
                return false;
            }

            default:
                return false;
        }
    }

    public override void Dispose()
    {
        if (!Exists) return;

        UnregisterAppbar();

        _renderer?.Dispose();
        _renderer = null;

        base.Dispose();
    }
}