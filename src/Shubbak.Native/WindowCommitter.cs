using Shubbak.Core.Animation;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Wm;
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
    /// Windows currently concealed, and how.
    /// </summary>
    /// <remarks>
    /// Tracked so <see cref="RestoreAll"/> can undo exactly what was done. Cloaking
    /// and hiding are not interchangeable at restore time: un-cloaking a window that
    /// was hidden leaves it hidden, and vice versa.
    /// </remarks>
    private readonly Dictionary<nint, ConcealMethod> _concealed = [];

    /// <summary>How a window was taken off screen.</summary>
    private enum ConcealMethod
    {
        /// <summary>Cloaked through the shell. The only method that is fully reversible.</summary>
        Cloaked,

        /// <summary><c>SW_HIDE</c>. Recoverable only by matching against the session.</summary>
        Hidden,

        /// <summary><c>SW_MINIMIZE</c>. Visible to the user in the taskbar.</summary>
        Minimised,
    }

    /// <summary>How windows on inactive workspaces are taken off screen.</summary>
    /// <remarks>
    /// Configurable because the preferred method depends on an undocumented shell
    /// interface. When that is unavailable a config option is far better than a
    /// rebuild - see <see cref="WindowHideMethod"/>.
    /// </remarks>
    public WindowHideMethod HideMethod { get; set; } = WindowHideMethod.Cloak;

    /// <summary>
    /// Whether concealed windows keep their taskbar button.
    /// </summary>
    /// <remarks>
    /// On by default. Cloaking leaves the button in place on its own, so this is
    /// simply not interfering: the taskbar stays a complete list of what is open, and
    /// a window on another workspace is one click away rather than hidden until you
    /// remember which workspace you left it on. Turn it off to make an inactive
    /// workspace disappear completely, at the cost of that discoverability.
    /// </remarks>
    public bool KeepInTaskbar { get; set; } = true;

    private long _cloakedCount;
    private long _hiddenCount;
    private long _minimisedCount;
    private int _cloakFailureReported;

    /// <summary>
    /// How many windows have been concealed each way since start.
    /// </summary>
    /// <remarks>
    /// Reported by <c>shubbak diagnose</c>. Whether concealment is really cloaking or
    /// has silently fallen back to hiding is the difference between recoverable and
    /// unrecoverable, and it is not otherwise observable from outside. The original
    /// bug - every concealed window stranded - was invisible for exactly this reason.
    /// </remarks>
    public (long Cloaked, long Hidden, long Minimised) ConcealmentCounts =>
        (Interlocked.Read(ref _cloakedCount),
         Interlocked.Read(ref _hiddenCount),
         Interlocked.Read(ref _minimisedCount));

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
            _concealed.Remove(handle);
        }
    }

    /// <summary>How many windows are currently concealed.</summary>
    public int ConcealedCount
    {
        get { lock (_lastCommitted) return _concealed.Count; }
    }

    /// <summary>
    /// Brings every concealed window back into view.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called on shutdown - clean exit, Ctrl+C, and after an unhandled exception.
    /// Without it, every window on an inactive workspace stays off screen after
    /// Shubbak stops, with its process still running and no way for the user to
    /// reach it. That is the failure mode that makes people uninstall a window
    /// manager and not come back.
    /// </para>
    /// <para>
    /// Cloaking makes this recoverable even when it is <i>not</i> called - a killed
    /// process leaves cloaked windows that the next run adopts and un-cloaks - but
    /// leaving the desktop tidy on the way out is still the right behaviour.
    /// </para>
    /// </remarks>
    /// <returns>How many windows were restored.</returns>
    public int RestoreAll()
    {
        KeyValuePair<nint, ConcealMethod>[] concealed;

        lock (_lastCommitted)
        {
            concealed = [.. _concealed];
            _concealed.Clear();
        }

        int restored = 0;

        foreach ((nint handle, ConcealMethod method) in concealed)
        {
            if (!Win32Window.Exists(handle)) continue;

            if (method == ConcealMethod.Cloaked) Win32Window.Uncloak(handle);
            else PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

            restored++;
        }

        return restored;
    }

    /// <summary>
    /// Brings a window back into view without moving it.
    /// </summary>
    /// <remarks>
    /// Visibility and geometry have to be separable, because a window whose position
    /// is being animated does not go through <see cref="Commit"/> - the animation
    /// engine drives it frame by frame instead. Without an independent reveal, such
    /// a window is animated into place while still concealed, and the workspace
    /// appears empty.
    /// </remarks>
    public void Reveal(nint handle)
    {
        if (handle == 0 || !Win32Window.Exists(handle)) return;

        Show(handle);
    }

    /// <summary>Takes a window out of view without moving it.</summary>
    public void Conceal(nint handle)
    {
        if (handle == 0 || !Win32Window.Exists(handle)) return;

        Hide(handle);
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

    /// <summary>
    /// Brings a window back into view, undoing whatever concealed it.
    /// </summary>
    private void Show(nint handle)
    {
        // A minimised window was minimised by the user; restoring it here would
        // override a deliberate choice every time the layout was recomputed.
        if (Win32Window.IsMinimised(handle)) return;

        ConcealMethod? method;

        lock (_lastCommitted)
        {
            method = _concealed.TryGetValue(handle, out ConcealMethod recorded)
                ? recorded
                : null;

            _concealed.Remove(handle);
        }

        if (method == ConcealMethod.Cloaked)
        {
            Win32ApplicationView.Uncloak(handle);
            Win32Taskbar.SetVisible(handle, visible: true);
            return;
        }

        if (method == ConcealMethod.Minimised)
        {
            PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_RESTORE);
            return;
        }

        if (method == ConcealMethod.Hidden)
        {
            // Async so a hung application cannot stall the whole relayout.
            PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            return;
        }

        // Not concealed by us. It may still be concealed by a previous run that was
        // killed before it could restore, in which case undoing it here is exactly the
        // recovery that makes concealment survivable. Both cloak routes are reversed:
        // the shell's, and the per-process one used on windows Shubbak owns.
        Win32Window.CloakState cloak = Win32Window.GetCloakState(handle);

        if (cloak is Win32Window.CloakState.App or Win32Window.CloakState.Shell)
        {
            Win32Window.Uncloak(handle);
            Win32ApplicationView.Uncloak(handle);
            Win32Taskbar.SetVisible(handle, visible: true);
            return;
        }

        if (!Win32Window.IsVisible(handle))
            PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    /// <summary>Whether this instance is currently concealing the window.</summary>
    /// <remarks>
    /// The concealment counterpart of <see cref="IsSelfInflicted"/>. Concealing a
    /// window makes Windows report it back as cloaked or hidden, and the event
    /// pipeline has to be able to tell that echo from a window the user or its own
    /// application really did put away.
    /// </remarks>
    public bool IsConcealing(nint handle)
    {
        lock (_lastCommitted) return _concealed.ContainsKey(handle);
    }

    /// <summary>
    /// Brings a window back that some earlier run left concealed.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Reveal"/>, which undoes concealment this instance
    /// performed and therefore knows which method was used. Here nothing is known - the
    /// run that concealed the window is gone - so every reversal is attempted. They are
    /// all safe to apply to a window that did not receive them.
    /// </remarks>
    public static void Revive(nint handle)
    {
        if (handle == 0) return;

        Win32ApplicationView.Uncloak(handle);
        Win32Window.Uncloak(handle);
        Win32Taskbar.SetVisible(handle, visible: true);

        if (!Win32Window.IsVisible(handle))
            PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    /// <summary>Whether a window is off screen by some form of concealment.</summary>
    /// <remarks>
    /// Deliberately does not distinguish who concealed it, because that cannot be
    /// known: a shell cloak looks identical whether Shubbak asked for it or the window
    /// simply lives on another virtual desktop. Callers resolve the ambiguity with the
    /// session, not with this.
    /// </remarks>
    public static bool IsConcealed(nint handle)
    {
        if (handle == 0) return false;

        if (!Win32Window.IsVisible(handle)) return true;

        return Win32Window.GetCloakState(handle)
            is Win32Window.CloakState.App or Win32Window.CloakState.Shell;
    }

    /// <summary>
    /// Takes a window off screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cloaking through the shell is the only method that is cleanly reversible, so it
    /// is preferred and everything else is a fallback. See
    /// <see cref="Win32ApplicationView"/> for why the obvious
    /// <c>DwmSetWindowAttribute</c> route cannot work across processes.
    /// </para>
    /// <para>
    /// The method used is recorded per window: restoring with the wrong one leaves the
    /// window off screen, which is the failure this whole path exists to prevent.
    /// </para>
    /// </remarks>
    private void Hide(nint handle)
    {
        lock (_lastCommitted)
        {
            if (_concealed.ContainsKey(handle)) return;
        }

        if (HideMethod == WindowHideMethod.Cloak && Win32ApplicationView.Cloak(handle))
        {
            Record(handle, ConcealMethod.Cloaked);
            Interlocked.Increment(ref _cloakedCount);

            // Cloaked windows keep their taskbar button unless it is taken away, which
            // is the behaviour most people want: the taskbar stays a complete list of
            // what is open, and a window on another workspace is one click away.
            if (!KeepInTaskbar) Win32Taskbar.SetVisible(handle, visible: false);

            if (Log.IsEnabled(LogLevel.Debug))
                Log.Debug(LogCategory.Window, $"concealed 0x{handle:X} via cloak");

            return;
        }

        if (HideMethod == WindowHideMethod.Minimise)
        {
            if (Win32Window.IsMinimised(handle)) return;

            PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_MINIMIZE);
            Record(handle, ConcealMethod.Minimised);
            Interlocked.Increment(ref _minimisedCount);

            if (Log.IsEnabled(LogLevel.Debug))
                Log.Debug(LogCategory.Window, $"concealed 0x{handle:X} via minimise");

            return;
        }

        if (!Win32Window.IsVisible(handle)) return;

        PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_HIDE);

        Record(handle, ConcealMethod.Hidden);
        Interlocked.Increment(ref _hiddenCount);

        // Warned once rather than per window. Falling back to SW_HIDE matters a great
        // deal - a hidden window is invisible to the filter on the next run, so it can
        // only be recovered by matching against the session - and it must be visible in
        // the log without drowning it.
        if (HideMethod == WindowHideMethod.Cloak &&
            Interlocked.Exchange(ref _cloakFailureReported, 1) == 0)
        {
            Log.Warn(LogCategory.Window,
                "the shell would not cloak a window; falling back to SW_HIDE. " +
                "Run 'shubbak restore' if any windows go missing.");
        }

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.Debug(LogCategory.Window,
                $"concealed 0x{handle:X} via hide" +
                (HideMethod == WindowHideMethod.Cloak ? " (cloak refused)" : " (configured)"));
        }
    }

    private void Record(nint handle, ConcealMethod method)
    {
        lock (_lastCommitted) _concealed[handle] = method;
    }
}
