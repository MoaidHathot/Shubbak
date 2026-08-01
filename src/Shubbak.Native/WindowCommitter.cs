using Shubbak.Core.Animation;
using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>
/// Applies computed window rectangles to the screen.
/// </summary>
/// <remarks>
/// <para>
/// Every rectangle for a frame goes into a single
/// <c>BeginDeferWindowPos</c>/<c>DeferWindowPos</c>/<c>EndDeferWindowPos</c>
/// transaction. This is not a micro-optimisation: P0's S2 spike measured the naive
/// per-window <c>SetWindowPos</c> alternative dropping <b>33-42% of frames</b> at
/// 144 Hz with identical managed code, against <b>0%</b> when batched. Batching -
/// not language choice - is what determines whether window movement looks smooth
/// (docs/adr/0001-language-choice.md, constraint 3).
/// </para>
/// <para>
/// The committer also owns <b>feedback suppression</b>. Our own <c>SetWindowPos</c>
/// calls generate <c>EVENT_OBJECT_LOCATIONCHANGE</c>, which S4 measured at
/// 122 events/second from a single dragged window. Without suppression the window
/// manager would react to its own output and fight itself during every relayout.
/// </para>
/// </remarks>
public sealed class WindowCommitter
{
    private const uint DefaultFlags =
        (uint)(SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
               SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
               SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
               SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING |
               SET_WINDOW_POS_FLAGS.SWP_NOCOPYBITS);

    private readonly Dictionary<nint, Rect> _lastCommitted = [];
    private readonly HashSet<nint> _driving = [];

    /// <summary>
    /// Whether a location change for this window was caused by us.
    /// </summary>
    /// <remarks>
    /// Consulted by the event pipeline before reacting to
    /// <c>EVENT_OBJECT_LOCATIONCHANGE</c>. Returns true while a commit is in flight
    /// for the window, and also when the reported rectangle matches what we last
    /// asked for - the second check catches the echo that arrives after the commit
    /// has finished.
    /// </remarks>
    public bool IsSelfInflicted(nint handle, Rect reported)
    {
        lock (_lastCommitted)
        {
            if (_driving.Contains(handle)) return true;
            return _lastCommitted.TryGetValue(handle, out Rect expected) && expected == reported;
        }
    }

    /// <summary>Forgets a window, e.g. once it has closed.</summary>
    public void Forget(nint handle)
    {
        lock (_lastCommitted)
        {
            _lastCommitted.Remove(handle);
            _driving.Remove(handle);
        }
    }

    /// <summary>
    /// Applies a whole frame of placements in one atomic transaction.
    /// </summary>
    /// <param name="placements">Target rectangles and visibility.</param>
    /// <param name="handleOf">Maps a placement's window to its native handle.</param>
    /// <returns>How many windows were actually moved.</returns>
    public int Commit(IReadOnlyList<Placement> placements, Func<Placement, nint> handleOf)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(handleOf);

        if (placements.Count == 0) return 0;

        // Visibility is applied outside the transaction: ShowWindow cannot take part
        // in a DeferWindowPos batch, and hiding a window we are also moving would
        // make the move pointless.
        List<(nint Handle, Rect Rect)> toMove = new(placements.Count);

        foreach (Placement placement in placements)
        {
            nint handle = handleOf(placement);
            if (handle == 0 || !Win32Window.Exists(handle)) continue;

            if (!placement.Visible)
            {
                Hide(handle);
                continue;
            }

            Show(handle);

            // Skip windows already where we want them. This is the difference
            // between a relayout costing one SetWindowPos and costing dozens, and
            // it also suppresses a large share of the LOCATIONCHANGE echo.
            lock (_lastCommitted)
            {
                if (_lastCommitted.TryGetValue(handle, out Rect previous) &&
                    previous == placement.Rect &&
                    Win32Window.GetBounds(handle) == placement.Rect)
                {
                    continue;
                }
            }

            toMove.Add((handle, placement.Rect));
        }

        if (toMove.Count == 0) return 0;

        lock (_lastCommitted)
        {
            foreach ((nint handle, _) in toMove) _driving.Add(handle);
        }

        try
        {
            ApplyBatch(toMove);
        }
        finally
        {
            lock (_lastCommitted)
            {
                foreach ((nint handle, Rect rect) in toMove)
                {
                    _driving.Remove(handle);
                    _lastCommitted[handle] = rect;
                }
            }
        }

        return toMove.Count;
    }

    private static void ApplyBatch(List<(nint Handle, Rect Rect)> moves)
    {
        HDWP batch = PInvoke.BeginDeferWindowPos(moves.Count);

        if (batch.IsNull)
        {
            // The batch API can fail under resource pressure. Falling back to
            // individual calls is slower but correct; dropping the frame is not.
            foreach ((nint handle, Rect rect) in moves) MoveSingle(handle, rect);
            return;
        }

        foreach ((nint handle, Rect rect) in moves)
        {
            batch = PInvoke.DeferWindowPos(
                batch, new HWND(handle), HWND.Null,
                rect.X, rect.Y, rect.Width, rect.Height,
                (SET_WINDOW_POS_FLAGS)DefaultFlags);

            if (batch.IsNull)
            {
                // A failed DeferWindowPos invalidates the whole batch, so the
                // remaining windows have to be placed individually.
                foreach ((nint h, Rect r) in moves) MoveSingle(h, r);
                return;
            }
        }

        PInvoke.EndDeferWindowPos(batch);
    }

    private static void MoveSingle(nint handle, Rect rect) =>
        PInvoke.SetWindowPos(
            new HWND(handle), HWND.Null,
            rect.X, rect.Y, rect.Width, rect.Height,
            (SET_WINDOW_POS_FLAGS)DefaultFlags);

    /// <summary>Places a single window immediately, outside any frame.</summary>
    public void CommitOne(nint handle, Rect rect)
    {
        if (handle == 0 || !Win32Window.Exists(handle)) return;

        lock (_lastCommitted) _driving.Add(handle);

        try
        {
            MoveSingle(handle, rect);
        }
        finally
        {
            lock (_lastCommitted)
            {
                _driving.Remove(handle);
                _lastCommitted[handle] = rect;
            }
        }
    }

    /// <summary>
    /// Applies one frame of animation as a single atomic transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hot path: called every tick while anything is moving. Takes a
    /// <see cref="Span{T}"/> over a caller-owned buffer and allocates nothing, per
    /// ADR 0001 constraint 2.
    /// </para>
    /// <para>
    /// Unlike <see cref="Commit"/> this does not skip windows already at their
    /// target, because an animation frame is by definition a new position, and it
    /// does not touch visibility, which the layout pass has already settled.
    /// </para>
    /// </remarks>
    public void CommitFrame(ReadOnlySpan<AnimationFrame> frames)
    {
        if (frames.Length == 0) return;

        HDWP batch = PInvoke.BeginDeferWindowPos(frames.Length);

        if (batch.IsNull)
        {
            foreach (AnimationFrame frame in frames)
                MoveSingle((nint)frame.Handle, frame.Rect);
        }
        else
        {
            bool ok = true;

            foreach (AnimationFrame frame in frames)
            {
                batch = PInvoke.DeferWindowPos(
                    batch, new HWND((nint)frame.Handle), HWND.Null,
                    frame.Rect.X, frame.Rect.Y, frame.Rect.Width, frame.Rect.Height,
                    (SET_WINDOW_POS_FLAGS)DefaultFlags);

                if (batch.IsNull)
                {
                    ok = false;
                    break;
                }
            }

            if (ok) PInvoke.EndDeferWindowPos(batch);
            else foreach (AnimationFrame frame in frames) MoveSingle((nint)frame.Handle, frame.Rect);
        }

        lock (_lastCommitted)
        {
            foreach (AnimationFrame frame in frames)
            {
                nint handle = (nint)frame.Handle;

                // While a window is mid-flight every position is ours, so it stays
                // in the driving set until the final frame lands.
                if (frame.IsFinal)
                {
                    _driving.Remove(handle);
                    _lastCommitted[handle] = frame.Rect;
                }
                else
                {
                    _driving.Add(handle);
                }
            }
        }
    }

    private static void Show(nint handle)
    {
        if (Win32Window.IsMinimised(handle)) return;
        if (Win32Window.IsVisible(handle)) return;

        // Async so a hung application cannot stall the whole relayout.
        PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    private static void Hide(nint handle)
    {
        if (!Win32Window.IsVisible(handle)) return;

        PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_HIDE);
    }
}
