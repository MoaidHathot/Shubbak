using Shubbak.Core.Geometry;

namespace Shubbak.Core.Animation;

/// <summary>What kind of change is being animated.</summary>
/// <remarks>
/// Each kind gets its own duration and curve, because they carry different
/// meanings: a window opening should feel like an arrival, whereas a tile settling
/// after a resize should be almost instant or it feels laggy.
/// </remarks>
public enum AnimationKind
{
    /// <summary>A window entering the layout.</summary>
    WindowOpen,

    /// <summary>A window changing position or size within the layout.</summary>
    WindowMove,

    /// <summary>Windows resettling because the layout changed.</summary>
    LayoutChange,

    /// <summary>Windows appearing because a workspace became active.</summary>
    WorkspaceSwitch,
}

/// <summary>Duration and curve for one animation kind.</summary>
public readonly record struct AnimationProfile(TimeSpan Duration, Easing Curve)
{
    public static AnimationProfile Instant => new(TimeSpan.Zero, Easing.Linear);
}

/// <summary>Animation tuning.</summary>
public sealed record AnimationOptions
{
    /// <summary>Whether to animate at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether a window joining the layout for the first time animates into its tile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default. The rectangle such a window would travel from is whatever size
    /// the application happened to open at - it was never part of the arrangement, so
    /// the motion describes nothing that happened.
    /// </para>
    /// <para>
    /// It is also the most expensive animation there is: a window that relays out its
    /// contents on every resize does so once per frame, and File Explorer doing that
    /// through a whole animation is a visible stutter rather than a slide. That was
    /// bad enough to be worth turning off entirely while the loop was delivering half
    /// the frames it was supposed to; with that fixed it is worth having as a choice
    /// rather than a decision made for everyone.
    /// </para>
    /// <para>
    /// When on, it uses the <see cref="WindowOpen"/> profile rather than
    /// <see cref="WindowMove"/>, so the two can be tuned apart - a shorter open than
    /// move is usually what stops it feeling sluggish.
    /// </para>
    /// </remarks>
    public bool AnimateNewWindows { get; init; }

    /// <summary>
    /// How many frames a second the daemon aims to commit while anything is moving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sixty, not the panel's refresh rate, and deliberately lower than the 143 this
    /// used to be fixed at. A window manager does not paint anything: it repositions
    /// windows and each application repaints itself, on its own thread, at whatever
    /// rate it can manage. Asking for more frames does not buy smoother motion past
    /// the point where applications keep up - it just asks them to discard and redraw
    /// their contents more often, and the ones that cannot fall behind and show bare
    /// background where their content should be.
    /// </para>
    /// <para>
    /// At 143 the daemon was measured delivering 13 to 16 frames in a 140 ms motion -
    /// about 100 Hz - so sixty costs far less in practice than the numbers suggest
    /// while nearly halving the repaint load on every window being moved. komorebi
    /// defaults to the same sixty and documents the same artifact as a known
    /// limitation.
    /// </para>
    /// <para>
    /// Raise it if your applications keep up and you want the motion finer; the cost
    /// is CPU in this process and repaint pressure in theirs.
    /// </para>
    /// </remarks>
    public int FramesPerSecond { get; init; } = 60;

    /// <summary>How long one frame lasts, derived from <see cref="FramesPerSecond"/>.</summary>
    public TimeSpan FramePeriod =>
        TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(FramesPerSecond, MinimumFps, MaximumFps));

    /// <summary>
    /// Below this the motion reads as a series of jumps rather than movement.
    /// </summary>
    public const int MinimumFps = 15;

    /// <summary>
    /// Above this the daemon is asking for frames faster than any panel displays them
    /// and faster than any application repaints them, so the extra work is discarded
    /// by the compositor and paid for twice.
    /// </summary>
    public const int MaximumFps = 240;

    public AnimationProfile WindowOpen { get; init; } =
        new(TimeSpan.FromMilliseconds(180), Easing.EaseOutExpo);

    public AnimationProfile WindowMove { get; init; } =
        new(TimeSpan.FromMilliseconds(140), Easing.EaseOut);

    public AnimationProfile LayoutChange { get; init; } =
        new(TimeSpan.FromMilliseconds(180), Easing.EaseOut);

    public AnimationProfile WorkspaceSwitch { get; init; } =
        new(TimeSpan.FromMilliseconds(120), Easing.EaseOut);

    /// <summary>
    /// Movements shorter than this are applied immediately.
    /// </summary>
    /// <remarks>
    /// Animating a three-pixel nudge costs a dozen frames and looks like lag rather
    /// than motion. Snapping small corrections keeps the desktop feeling crisp.
    /// </remarks>
    public int MinimumAnimatedDistance { get; init; } = 8;

    public static AnimationOptions Default => new();

    public static AnimationOptions Disabled => new() { Enabled = false };

    public AnimationProfile ProfileFor(AnimationKind kind) => kind switch
    {
        AnimationKind.WindowOpen => WindowOpen,
        AnimationKind.WindowMove => WindowMove,
        AnimationKind.LayoutChange => LayoutChange,
        AnimationKind.WorkspaceSwitch => WorkspaceSwitch,
        _ => WindowMove,
    };
}

/// <summary>One window's in-flight motion.</summary>
internal struct Track
{
    public long Handle;
    public Rect From;
    public Rect To;

    /// <summary>Where the window was at the moment this track began.</summary>
    public Rect Current;

    public double Elapsed;
    public double Duration;
    public Easing Curve;
    public bool Active;
}

/// <summary>A window's position for the current frame.</summary>
/// <param name="Handle">Native window handle.</param>
/// <param name="Rect">Where to put it this frame.</param>
/// <param name="IsFinal">True on the frame that reaches the target.</param>
public readonly record struct AnimationFrame(long Handle, Rect Rect, bool IsFinal);

/// <summary>
/// Interpolates windows towards their target rectangles.
/// </summary>
/// <remarks>
/// <para>
/// The layout engine decides <i>where</i> windows belong; this decides <i>how they
/// get there</i>. Keeping the two apart is what lets layout stay time-free and
/// deterministically testable while motion remains tunable.
/// </para>
/// <para>
/// <b>Re-targeting blends from the current position, not the original.</b> Layout
/// changes arrive far faster than animations complete - opening three windows in
/// quick succession retargets every tile twice - and restarting from the old origin
/// would make windows visibly jump backwards. Each retarget begins a fresh track
/// from wherever the window actually is.
/// </para>
/// <para>
/// <b>Allocation-free per frame.</b> Tracks live in a pre-allocated array and are
/// reused, per ADR 0001 constraint 2. S2 measured managed work at 2.5-5.3% of frame
/// time with Win32 taking the rest, so the only way to lose that margin is to start
/// allocating here.
/// </para>
/// </remarks>
public sealed class AnimationEngine
{
    private Track[] _tracks = new Track[64];
    private int _count;

    private readonly Dictionary<long, int> _index = [];

    public AnimationEngine(AnimationOptions? options = null) =>
        Options = options ?? AnimationOptions.Default;

    public AnimationOptions Options { get; set; }

    /// <summary>How many windows are currently moving.</summary>
    public int ActiveCount => _count;

    /// <summary>True when at least one window is still in motion.</summary>
    public bool IsAnimating => _count > 0;

    /// <summary>
    /// Points a window at a new target.
    /// </summary>
    /// <param name="handle">Native window handle.</param>
    /// <param name="currentRect">Where the window is right now.</param>
    /// <param name="target">Where it should end up.</param>
    /// <param name="kind">Which profile to use.</param>
    /// <returns>
    /// False when the move was applied instantly - because animation is off, the
    /// distance is negligible, or the window is already there - in which case the
    /// caller should commit <paramref name="target"/> directly.
    /// </returns>
    public bool Retarget(long handle, Rect currentRect, Rect target, AnimationKind kind)
    {
        if (currentRect == target)
        {
            Remove(handle);
            return false;
        }

        AnimationProfile profile = Options.ProfileFor(kind);

        if (!Options.Enabled || profile.Duration <= TimeSpan.Zero || IsNegligible(currentRect, target))
        {
            Remove(handle);
            return false;
        }

        ref Track track = ref GetOrCreate(handle);

        track.Handle = handle;
        track.From = currentRect;
        track.Current = currentRect;
        track.To = target;
        track.Elapsed = 0;
        track.Duration = profile.Duration.TotalMilliseconds;
        track.Curve = profile.Curve;
        track.Active = true;

        return true;
    }

    /// <summary>
    /// Advances every track and writes this frame's rectangles.
    /// </summary>
    /// <param name="deltaMilliseconds">Time since the previous tick.</param>
    /// <param name="destination">Receives one entry per moving window.</param>
    /// <returns>How many entries were written.</returns>
    public int Tick(double deltaMilliseconds, Span<AnimationFrame> destination)
    {
        if (_count == 0) return 0;

        int written = 0;
        int surviving = 0;

        for (int i = 0; i < _count; i++)
        {
            ref Track track = ref _tracks[i];
            if (!track.Active) continue;

            track.Elapsed += deltaMilliseconds;

            double progress = track.Duration <= 0 ? 1 : Math.Clamp(track.Elapsed / track.Duration, 0, 1);
            double eased = track.Curve.Evaluate(progress);

            Rect rect = progress >= 1 ? track.To : Interpolate(track.From, track.To, eased);
            track.Current = rect;

            if (written < destination.Length)
                destination[written++] = new AnimationFrame(track.Handle, rect, progress >= 1);

            if (progress < 1)
            {
                // Compact in place: finished tracks are dropped by not copying them
                // forward, which keeps the array dense with no allocation.
                if (surviving != i) _tracks[surviving] = track;
                surviving++;
            }
        }

        if (surviving != _count)
        {
            _count = surviving;
            RebuildIndex();
        }

        return written;
    }

    /// <summary>The rectangle a window currently occupies, if it is moving.</summary>
    public bool TryGetCurrent(long handle, out Rect rect)
    {
        if (_index.TryGetValue(handle, out int i) && i < _count)
        {
            rect = _tracks[i].Current;
            return true;
        }

        rect = default;
        return false;
    }

    /// <summary>Stops animating a window, e.g. because it closed.</summary>
    public void Remove(long handle)
    {
        if (!_index.TryGetValue(handle, out int i) || i >= _count) return;

        _tracks[i] = _tracks[_count - 1];
        _count--;
        RebuildIndex();
    }

    /// <summary>Stops everything, e.g. when animation is turned off.</summary>
    public void Clear()
    {
        _count = 0;
        _index.Clear();
    }

    // ---- internals ---------------------------------------------------------

    private ref Track GetOrCreate(long handle)
    {
        if (_index.TryGetValue(handle, out int existing) && existing < _count)
            return ref _tracks[existing];

        if (_count == _tracks.Length)
        {
            // Growth happens at most a handful of times in a session, and never on
            // the tick path.
            Array.Resize(ref _tracks, _tracks.Length * 2);
        }

        int slot = _count++;
        _index[handle] = slot;
        return ref _tracks[slot];
    }

    private void RebuildIndex()
    {
        _index.Clear();
        for (int i = 0; i < _count; i++) _index[_tracks[i].Handle] = i;
    }

    private bool IsNegligible(Rect from, Rect to)
    {
        int threshold = Options.MinimumAnimatedDistance;

        return Math.Abs(from.X - to.X) < threshold &&
               Math.Abs(from.Y - to.Y) < threshold &&
               Math.Abs(from.Width - to.Width) < threshold &&
               Math.Abs(from.Height - to.Height) < threshold;
    }

    /// <summary>Interpolates a rectangle, rounding once at the end.</summary>
    private static Rect Interpolate(Rect from, Rect to, double t) => new(
        (int)Math.Round(from.X + ((to.X - from.X) * t)),
        (int)Math.Round(from.Y + ((to.Y - from.Y) * t)),
        Math.Max(0, (int)Math.Round(from.Width + ((to.Width - from.Width) * t))),
        Math.Max(0, (int)Math.Round(from.Height + ((to.Height - from.Height) * t))));
}
