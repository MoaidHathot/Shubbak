namespace Shubbak.Core.Tree;

/// <summary>
/// How a window participates in (or opts out of) tiling.
/// </summary>
public enum WindowState
{
    /// <summary>Participates in the layout tree and is sized by it.</summary>
    Tiling,

    /// <summary>Positioned freely; ignored by layout, but still tracked and focusable.</summary>
    Floating,

    /// <summary>Fills its workspace, covering siblings.</summary>
    Fullscreen,

    /// <summary>
    /// Fills the whole monitor, covering the bar and anything else the work area
    /// was keeping clear.
    /// </summary>
    /// <remarks>
    /// The difference from <see cref="Fullscreen"/> is which rectangle it gets: the
    /// monitor's bounds rather than its work area. That is the whole feature. The
    /// bar is an ordinary non-topmost window, so a window placed over it and raised
    /// covers it with no cooperation from either side.
    /// </remarks>
    MonitorFullscreen,

    /// <summary>Maximised via the native window command.</summary>
    Maximised,

    /// <summary>Minimised to the taskbar; retains its position in the tree.</summary>
    Minimised,
}

/// <summary>
/// A leaf node: one managed top-level window.
/// </summary>
/// <remarks>
/// <para>
/// The native handle is held as an opaque <see cref="long"/> rather than a Win32
/// <c>HWND</c>, because <c>Shubbak.Core</c> must not reference Win32 at all
/// (invariant 5 of docs/adr/0001-language-choice.md). The platform layer maps
/// between the two.
/// </para>
/// <para>
/// <see cref="Tags"/> is present from P1 but unused until P5. The data model has
/// to carry it from the start: retrofitting multi-workspace membership later
/// would disturb focus, close handling, and geometry ownership all at once.
/// </para>
/// </remarks>
public sealed class WindowNode : Node
{
    public WindowNode(long handle, WindowIdentity identity)
    {
        Handle = handle;
        Identity = identity;
    }

    /// <summary>Opaque native window handle (an <c>HWND</c> on Windows).</summary>
    public long Handle { get; }

    /// <summary>Matchable attributes, used by window rules and by the bar.</summary>
    public WindowIdentity Identity { get; set; }

    public WindowState State { get; set; } = WindowState.Tiling;

    /// <summary>True when the layout engine should size this window.</summary>
    public bool IsTiled => State is WindowState.Tiling;

    /// <summary>
    /// True when this window sits on the workspace its monitor is currently showing.
    /// </summary>
    /// <remarks>
    /// Distinct from being focused, and from being on the active workspace of the
    /// focused monitor. Every monitor shows one workspace at a time, so a window can
    /// be perfectly visible on a display nobody is looking at - and one that is not
    /// on any displayed workspace must never be brought to the foreground, however
    /// firmly the tree believes it has focus.
    /// </remarks>
    public bool IsOnADisplayedWorkspace =>
        Workspace is { } workspace &&
        workspace.Monitor is { } monitor &&
        ReferenceEquals(monitor.ActiveWorkspace, workspace);

    /// <summary>Keeps the window above others of the same kind.</summary>
    public bool IsAlwaysOnTop { get; set; }

    /// <summary>
    /// True while the application has taken this window full-screen by itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An observation, not a state. A browser playing a video full-screen resizes its
    /// own window to the monitor and tells nobody: the only event that reports it is
    /// <c>EVENT_OBJECT_LOCATIONCHANGE</c>, which Shubbak deliberately does not
    /// subscribe to, so it is noticed by looking. See <c>NativeFullscreen</c>.
    /// </para>
    /// <para>
    /// Deliberately not a <see cref="WindowState"/>. The window keeps its tile: only
    /// the rectangle it is given is substituted, so nothing else on the workspace
    /// moves while the video plays and leaving full-screen is a return to a tile that
    /// was never given away. Turning it into a state would take the window out of the
    /// tiling flow, and the siblings would then expand to fill the gap and shrink
    /// back afterwards - the whole workspace rearranging itself twice because a video
    /// was watched. GlazeWM does exactly that, and needs a special case on the way
    /// out to work out where the window belongs; there is nothing to work out here.
    /// </para>
    /// <para>
    /// Only ever set for a window in <see cref="WindowState.Tiling"/> or
    /// <see cref="WindowState.Floating"/>. The test that sets it is geometric - does
    /// the window cover its monitor - and that description fits Shubbak's own
    /// <see cref="WindowState.MonitorFullscreen"/> exactly, so the question is never
    /// asked of a window Shubbak put there itself.
    /// </para>
    /// </remarks>
    public bool IsNativeFullscreen { get; set; }

    /// <summary>
    /// When this window last had focus, as a counter rather than a clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zero means never focused. Higher is more recent, and the values are only ever
    /// compared with one another - there is no unit and no epoch.
    /// </para>
    /// <para>
    /// A counter rather than a timestamp on purpose. The question asked of it is
    /// always "which of these was focused more recently", never "how long ago", and
    /// a monotonic counter answers that without a clock that can be adjusted,
    /// without daylight saving, and without two windows focused inside the same
    /// timer tick comparing equal - which on a 15.6 ms system clock is most of a
    /// quick pair of focus changes.
    /// </para>
    /// <para>
    /// <see cref="WorkspaceNode.LastFocused"/> answers a narrower question - the
    /// window to return to within one workspace - and cannot be used for this. It is
    /// a single reference per workspace, so it cannot order windows against each
    /// other at all, let alone across workspaces or monitors.
    /// </para>
    /// </remarks>
    public long FocusSequence { get; internal set; }

    /// <summary>
    /// Workspaces this window belongs to, beyond the one it currently sits in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The AwesomeWM "tag" model: a window can be a member of several workspaces and
    /// appears in whichever of them you are currently viewing.
    /// </para>
    /// <para>
    /// It is worth being precise about what this can and cannot mean. A Windows
    /// window has exactly one position on exactly one monitor - it physically cannot
    /// be drawn in two places at once. So membership of several workspaces does not
    /// duplicate the window; it means the window <i>relocates</i> to whichever
    /// tagged workspace was most recently activated. That is also what AwesomeWM
    /// does, and modelling it any other way would promise something the platform
    /// cannot deliver.
    /// </para>
    /// <para>
    /// Consequently the node still lives in exactly one tree at a time, and every
    /// invariant that depends on that - focus, close handling, geometry ownership -
    /// is untouched.
    /// </para>
    /// </remarks>
    public IReadOnlySet<string> Tags => _tags;
    private readonly HashSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when this window follows every workspace on its monitor.
    /// </summary>
    /// <remarks>
    /// Equivalent to tagging it to all of them, but expressed as a flag so it keeps
    /// working when new workspaces are created on demand.
    /// </remarks>
    public bool IsSticky { get; set; }

    /// <summary>True when the window belongs to more than the workspace it sits in.</summary>
    public bool HasTags => IsSticky || _tags.Count > 0;

    /// <summary>Whether this window should appear on the given workspace.</summary>
    public bool BelongsTo(WorkspaceNode workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (ReferenceEquals(Workspace, workspace)) return true;
        if (IsSticky && ReferenceEquals(Monitor, workspace.Monitor)) return true;

        return _tags.Contains(workspace.Name);
    }

    internal bool AddTag(string tag) => _tags.Add(tag);

    /// <summary>
    /// Restores a tag from a saved session.
    /// </summary>
    /// <remarks>
    /// Public because restoration happens in the daemon rather than the state
    /// machine, and because it must bypass the checks in <c>WindowManager.Tag</c> -
    /// the window has not been placed yet, so "is it already on that workspace?"
    /// has no answer.
    /// </remarks>
    public void AddTagForRestore(string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        _tags.Add(tag);
    }

    internal bool RemoveTag(string tag) => _tags.Remove(tag);

    internal void ClearTags() => _tags.Clear();

    /// <summary>
    /// The scratchpad slot this window is stashed under, or null.
    /// </summary>
    /// <remarks>
    /// Named slots rather than a single scratchpad, so several windows can be
    /// stashed and summoned independently. A single unnamed scratchpad turns into a
    /// junk drawer the moment it holds more than one thing.
    /// </remarks>
    public string? ScratchpadName { get; set; }

    /// <summary>
    /// Geometry the window uses when it is not being sized by the layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serves two related purposes, which are the same value in practice:
    /// the position of the window while <see cref="WindowState.Floating"/>, and the
    /// geometry to restore to when leaving <see cref="WindowState.Fullscreen"/> or
    /// <see cref="WindowState.Maximised"/>.
    /// </para>
    /// <para>
    /// Unlike <see cref="Node.Rect"/> - which is computed output that only the
    /// layout engine writes - this is input, owned by the user and the platform
    /// layer, and so is publicly settable.
    /// </para>
    /// </remarks>
    public Geometry.Rect? FloatingRect { get; set; }

    public override string ToString() =>
        $"Window#{Id}[0x{Handle:X}, {Identity.ProcessName}, \"{Identity.Title}\"]";
}
