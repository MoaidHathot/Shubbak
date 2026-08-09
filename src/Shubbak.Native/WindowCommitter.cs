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

    /// <summary>
    /// <see cref="DefaultFlags"/> plus <c>SWP_ASYNCWINDOWPOS</c>, for animation only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without the flag, <c>EndDeferWindowPos</c> <i>sends</i> to each target window's
    /// thread and blocks until that thread has processed it. Every window being moved
    /// therefore gets to decide how long the window manager's animation loop takes,
    /// and a browser or an Electron app busy on its UI thread stops the loop dead.
    /// </para>
    /// <para>
    /// This is not a theory. The first run to measure the commit call found a median
    /// of <b>3.71 ms to move a median of one window</b> - 53% of a 7 ms frame budget -
    /// with a p99 of 73.63 ms and a worst case of 138.75 ms. The worst tick of that
    /// entire run was 138.76 ms, so ten microseconds of the worst stall the daemon
    /// suffered was something other than this call.
    /// </para>
    /// <para>
    /// ADR 0001 measured the same path at 94.6% of frame time and drew the conclusion
    /// that managed code was not the bottleneck, which was correct. What it could not
    /// see is that its harness moved synthetic windows whose message pumps were idle
    /// and always ready to answer. Real targets are not.
    /// </para>
    /// <para>
    /// Animation only, deliberately. The flag makes the move asynchronous, so
    /// <c>GetWindowRect</c> can briefly report the old rectangle - harmless for a
    /// waypoint that a later frame supersedes, and harmless for the final frame
    /// because "is the window already where we put it?" is answered from
    /// <see cref="_lastApplied"/> rather than by asking Windows. Placement outside an
    /// animation keeps the synchronous flags until there is a measurement saying it
    /// should not.
    /// </para>
    /// <para>
    /// Windows ignores the flag when the calling thread and the target window's thread
    /// share an input queue, so it changes nothing for windows in this process and
    /// everything for the cross-process ones that were doing the blocking.
    /// </para>
    /// <para>
    /// <b>Every animation frame, including the last one.</b> Sending the settling
    /// frame synchronously was tried, on the reasoning that a window coming to rest
    /// should be told properly so it paints promptly rather than showing bare
    /// background. It was measured and reverted: the settling frame cost <b>87 ms</b>,
    /// not the ~3.7 ms the median frame had cost, because the end of a resize is
    /// exactly when an application does its layout and repaint work and so the worst
    /// possible moment to wait on it. Commit p99 went from 1.35 ms to 55.36 ms, half
    /// the frames in each motion were lost, and because the tick thread is what
    /// dispatches commands it added up to 87 ms of keystroke latency after every
    /// motion. The grey it was meant to fix was not visibly better.
    /// </para>
    /// <para>
    /// The remaining flash of unpainted window is not ours to fix here. The
    /// application owns its repaint; a window manager can only ask. komorebi documents
    /// the same artifact and ships its animations at 60 fps rather than 144, which is
    /// the lever that actually works - fewer frames give each one more time to be
    /// painted.
    /// </para>
    /// </remarks>
    private const uint FrameFlags =
        DefaultFlags | (uint)SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS;

    /// <summary>
    /// <see cref="DefaultFlags"/> plus <c>SWP_ASYNCWINDOWPOS</c>, for a window whose
    /// thread has stopped answering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same flags as <see cref="FrameFlags"/> and a different reason, so they are
    /// named apart: one is a decision about animation, this is a defence against one
    /// application taking the desktop down with it.
    /// </para>
    /// <para>
    /// <c>EndDeferWindowPos</c> delivers the batch to each target window's thread and
    /// waits. One thread that is not pumping therefore blocks the move of every other
    /// window in the batch, and since a workspace switch is a batch, it blocks
    /// switching workspaces at all. Reported as a black window - a window that is not
    /// painting is a window that is not pumping - after which no workspace could be
    /// switched to for some seconds, until the application either recovered or was
    /// closed.
    /// </para>
    /// <para>
    /// Asking asynchronously costs nothing that matters for a window in this state.
    /// It is not going to redraw at the new size until it starts answering again, and
    /// when it does it will process the position it was sent.
    /// </para>
    /// </remarks>
    private const uint UnresponsiveFlags =
        DefaultFlags | (uint)SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS;

    /// <summary>
    /// The flags one animation frame is sent with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SWP_NOSIZE</c> when the frame changes only position. Moving a window
    /// translates a quad; resizing it makes DWM reallocate the window's redirection
    /// surface and makes the application process <c>WM_SIZE</c> and lay its own
    /// contents out again. An animation that is a pure translation - a swap between
    /// equally-sized tiles, a workspace slide, a move between monitors of the same
    /// resolution - was asking every window it touched for that work on every frame,
    /// to arrive at the size the window already was.
    /// </para>
    /// <para>
    /// The engine decides, because it is the only thing that knows what the previous
    /// frame said, and it never sets the flag on the frame a window comes to rest on.
    /// </para>
    /// </remarks>
    private static uint FlagsFor(AnimationFrame frame) =>
        frame.SizeUnchanged
            ? FrameFlags | (uint)SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            : FrameFlags;

    private readonly Dictionary<nint, Rect> _lastCommitted = [];

    /// <summary>
    /// The rectangle actually passed to Windows, shadow compensation included.
    /// </summary>
    /// <remarks>
    /// Kept alongside the visible rectangle the layout asked for, because the two are
    /// no longer the same and each answers a different question. GetWindowRect reports
    /// this one, so this is what "is the window already where we put it?" has to
    /// compare against - recomputing the compensation to make that comparison would
    /// give a stale answer whenever a window's frame had changed since it was
    /// measured, and the window would be moved again on every single layout. Which is
    /// visible: a small jump every time focus moved.
    /// </remarks>
    private readonly Dictionary<nint, Rect> _lastApplied = [];

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

    /// <summary>Forgets a window, e.g. once it has closed.</summary>
    public void Forget(nint handle)
    {
        lock (_lastCommitted)
        {
            _lastCommitted.Remove(handle);
            _lastApplied.Remove(handle);
            _concealed.Remove(handle);
        }

        ForgetShadow(handle);
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

            // Reversed by the same route that concealed it. Cloaking goes through the
            // shell, so un-cloaking must too: DwmSetWindowAttribute is scoped to the
            // owning process and silently refuses every window Shubbak manages, which
            // made shutdown report windows restored while leaving them all cloaked.
            switch (method)
            {
                case ConcealMethod.Cloaked:
                    Win32ApplicationView.Uncloak(handle);
                    RestoreTaskbarButton(handle);
                    break;

                case ConcealMethod.Minimised:
                    PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_RESTORE);
                    break;

                default:
                    PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
                    break;
            }

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

        // Raises are separate from moves, and are applied even when nothing moved.
        // Entering monocle changes no rectangle at all - every window already fills
        // the area - so the stacking change is the entire visible effect.
        List<nint>? toRaise = null;

        foreach (Placement placement in placements)
        {
            nint handle = handleOf(placement);
            if (handle == 0 || !Win32Window.Exists(handle)) continue;

            if (!placement.Visible)
            {
                // Concealed, then still moved. A window on an inactive workspace has a
                // rectangle like any other - the layout computes one precisely so that
                // showing the workspace shows a finished arrangement rather than a
                // frame of garbage.
                //
                // Returning here instead left every such window at whatever position
                // and stacking it had before Shubbak started. It was concealed there,
                // and the first time its workspace was shown it appeared at that stale
                // position - windows piled on top of one another - and only then slid
                // into place. Since z-order is never touched, nothing corrected the
                // stacking either.
                Hide(handle);
            }
            else
            {
                Show(handle);

                if (placement.Raise) (toRaise ??= []).Add(handle);
            }

            // Skip windows already where we want them. This is the difference
            // between a relayout costing one SetWindowPos and costing dozens, and
            // it also suppresses a large share of the LOCATIONCHANGE echo.
            //
            // Judged on the target alone, deliberately. Comparing against where the
            // window actually is starts a fight with any application that adjusts its
            // own size - a terminal snapping to whole character cells never lands
            // exactly where it was put, so the comparison failed every time and the
            // window was moved again on every layout. Since focus changes trigger a
            // layout, that was a visible jump every time focus moved.
            //
            // A window moved by something else is handled where it should be: the
            // location-change event, which already knows how to tell a user's drag
            // from our own echo.
            lock (_lastCommitted)
            {
                if (_lastCommitted.TryGetValue(handle, out Rect previous) &&
                    previous == placement.Rect &&
                    _lastApplied.ContainsKey(handle))
                {
                    continue;
                }
            }

            toMove.Add((handle, placement.Rect));
        }

        if (toMove.Count == 0)
        {
            RaiseAll(toRaise);
            return 0;
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
                    _lastCommitted[handle] = rect;
                    _lastApplied[handle] = Expand(handle, rect);
                }
            }
        }

        RaiseAll(toRaise);

        return toMove.Count;
    }

    /// <summary>Brings windows to the front of the ordinary stacking order.</summary>
    /// <remarks>
    /// <para>
    /// <c>HWND_TOP</c>, not <c>HWND_TOPMOST</c>. Topmost would place the window above
    /// every other application permanently, which is a different feature and an
    /// unwelcome one; this only orders a window against its own siblings.
    /// </para>
    /// <para>
    /// Applied after the move so the raise is not undone by it, and outside the
    /// batch: a <c>DeferWindowPos</c> transaction that changes stacking has to give up
    /// <c>SWP_NOZORDER</c> for every window in it, and almost none of them want that.
    /// The list is short - only fullscreen, maximised and monocle windows ask.
    /// </para>
    /// </remarks>
    private static void RaiseAll(List<nint>? handles)
    {
        if (handles is null) return;

        foreach (nint handle in handles) Raise(handle);
    }

    /// <summary>Brings one window to the front of the ordinary stacking order.</summary>
    /// <remarks>
    /// Public because an animated window never reaches <see cref="Commit"/>, which is
    /// where <c>Placement.Raise</c> is otherwise honoured. The layout pass raises those
    /// itself, at the moment it hands the window to the animation engine.
    /// </remarks>
    public static void Raise(nint handle)
    {
        if (!Win32Window.Exists(handle)) return;

        // HWND_TOP = 0. A sentinel rather than a real handle.
        var top = new HWND(0);

        PInvoke.SetWindowPos(
            new HWND(handle), top, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING);
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
            Rect target = Expand(handle, rect);

            // Checked per window, because the batch is only as fast as its slowest
            // target and one stuck application should not decide how long a workspace
            // switch takes for the other seven windows.
            uint flags = Win32Window.IsHung(handle) ? UnresponsiveFlags : DefaultFlags;

            batch = PInvoke.DeferWindowPos(
                batch, new HWND(handle), HWND.Null,
                target.X, target.Y, target.Width, target.Height,
                (SET_WINDOW_POS_FLAGS)flags);

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

    /// <summary>Places one window immediately, without a batch.</summary>
    /// <param name="handle">The window to move.</param>
    /// <param name="rect">Where its visible frame should end up.</param>
    /// <param name="flags">
    /// Defaults to the synchronous <see cref="DefaultFlags"/>. The animation path
    /// passes <see cref="FlagsFor"/>, because a fallback frame is still a frame and
    /// blocking on a busy target is exactly as bad when the batch failed as when it
    /// did not.
    /// </param>
    private static void MoveSingle(nint handle, Rect rect, uint flags = DefaultFlags)
    {
        Rect target = Expand(handle, rect);

        // Same defence as the batch, and it matters more here: the fallback path
        // places every window individually, so without this a single stuck window
        // would block the ones queued behind it one after another.
        if (flags == DefaultFlags && Win32Window.IsHung(handle)) flags = UnresponsiveFlags;

        PInvoke.SetWindowPos(
            new HWND(handle), HWND.Null,
            target.X, target.Y, target.Width, target.Height,
            (SET_WINDOW_POS_FLAGS)flags);
    }

    /// <summary>
    /// Grows a target rectangle to account for the window's invisible shadow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A window's rectangle includes its drop shadow, which is transparent. Asking
    /// for the rectangle the user should see and passing it straight to SetWindowPos
    /// therefore insets the visible frame by the shadow on every side - so two
    /// neighbouring windows show a gap of twice the shadow no matter what the gap is
    /// configured to be. Shrinking the gap in config barely helps, because most of
    /// what is being looked at was never the gap.
    /// </para>
    /// <para>
    /// Measured once per window and cached. It is a compositor round trip, and this
    /// runs for every window of every animation frame - per ADR 0001 the tick may not
    /// allocate, and it should not be making system calls either.
    /// </para>
    /// </remarks>
    private static Rect Expand(nint handle, Rect rect) => Expand(rect, ShadowOf(handle));

    /// <summary>Grows a visible rectangle to the window rectangle that produces it.</summary>
    /// <remarks>
    /// Separated from the handle so the geometry can be tested against margins that
    /// actually exist. A plain test window has no shadow at all, so a test that placed
    /// one and measured it would agree with any implementation, including a broken one.
    /// </remarks>
    public static Rect Expand(Rect rect, Win32Window.ShadowMargins margins)
    {
        if (margins.IsEmpty) return rect;

        return new Rect(
            rect.X - margins.Left,
            rect.Y - margins.Top,
            rect.Width + margins.Left + margins.Right,
            rect.Height + margins.Top + margins.Bottom);
    }

    /// <summary>Shrinks a window rectangle to the visible frame inside it.</summary>
    /// <remarks>
    /// The exact inverse of <see cref="Expand(Rect, Win32Window.ShadowMargins)"/>. The
    /// two must round-trip, because one is used to place a window and the other to
    /// measure it: any disagreement reads as the window having moved on its own, and
    /// the layout then chases it.
    /// </remarks>
    public static Rect Shrink(Rect rect, Win32Window.ShadowMargins margins)
    {
        if (margins.IsEmpty) return rect;

        return new Rect(
            rect.X + margins.Left,
            rect.Y + margins.Top,
            rect.Width - margins.Left - margins.Right,
            rect.Height - margins.Top - margins.Bottom);
    }

    private static Win32Window.ShadowMargins ShadowOf(nint handle)
    {
        lock (s_shadowGate)
        {
            if (!s_shadows.TryGetValue(handle, out Win32Window.ShadowMargins margins))
            {
                margins = Win32Window.GetShadowMargins(handle);
                s_shadows[handle] = margins;
            }

            return margins;
        }
    }

    private static readonly Dictionary<nint, Win32Window.ShadowMargins> s_shadows = [];
    private static readonly Lock s_shadowGate = new();

    /// <summary>
    /// Where a window's visible frame is now, in the same terms the layout uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Win32Window.GetBounds"/> is <c>GetWindowRect</c>, which includes the
    /// invisible resize border a window draws its shadow into. Layout rectangles do
    /// not. Comparing the two directly therefore reports a difference for every window
    /// that has a shadow - typically fourteen pixels of width and seven of height -
    /// even when the window is exactly where it was asked to be.
    /// </para>
    /// <para>
    /// That false difference was read as "the window needs to move". Since a focus
    /// change re-runs the layout, every focus change started an animation that grew the
    /// window by the width of its own shadow and then settled back.
    /// </para>
    /// </remarks>
    public static Rect VisibleBounds(nint handle)
    {
        Rect outer = Win32Window.GetBounds(handle);
        if (outer.IsEmpty) return outer;

        return Shrink(outer, ShadowOf(handle));
    }

    /// <summary>Forgets a window's measured shadow.</summary>
    /// <remarks>
    /// Called when a window is unmanaged, so the table does not grow for the life of
    /// the process and a recycled handle cannot inherit someone else's margins.
    /// </remarks>
    private static void ForgetShadow(nint handle)
    {
        lock (s_shadowGate) s_shadows.Remove(handle);
    }

    /// <summary>Places a single window immediately, outside any frame.</summary>
    public void CommitOne(nint handle, Rect rect)
    {
        if (handle == 0 || !Win32Window.Exists(handle)) return;

        try
        {
            MoveSingle(handle, rect);
        }
        finally
        {
            lock (_lastCommitted)
            {
                _lastCommitted[handle] = rect;
                _lastApplied[handle] = Expand(handle, rect);
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
                MoveSingle((nint)frame.Handle, frame.Rect, FlagsFor(frame));
        }
        else
        {
            bool ok = true;

            foreach (AnimationFrame frame in frames)
            {
                // Expanded exactly as the layout path expands, so a window is the same
                // size whether it arrived by animation or by a direct placement.
                // Writing raw here made every animated frame land the visible window
                // about fourteen pixels narrower than the layout asked for, and the
                // next placement snapped it back - a visible pop on every focus change.
                Rect target = Expand((nint)frame.Handle, frame.Rect);

                batch = PInvoke.DeferWindowPos(
                    batch, new HWND((nint)frame.Handle), HWND.Null,
                    target.X, target.Y, target.Width, target.Height,
                    (SET_WINDOW_POS_FLAGS)FlagsFor(frame));

                if (batch.IsNull)
                {
                    ok = false;
                    break;
                }
            }

            if (ok) PInvoke.EndDeferWindowPos(batch);
            else foreach (AnimationFrame frame in frames) MoveSingle((nint)frame.Handle, frame.Rect, FlagsFor(frame));
        }

        lock (_lastCommitted)
        {
            foreach (AnimationFrame frame in frames)
            {
                // Only the final frame is recorded. Every position before it is a
                // waypoint the window is passing through, and recording those would
                // make the skip check believe the window was already where the layout
                // wants it while it was still halfway there.
                if (!frame.IsFinal) continue;

                nint handle = (nint)frame.Handle;

                _lastCommitted[handle] = frame.Rect;

                // Recorded for the same reason the layout path records it: the skip
                // check treats a missing entry as "never compensated" and places the
                // window again. After an animation that meant one redundant move,
                // which is what the user saw jump.
                _lastApplied[handle] = Expand(handle, frame.Rect);
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
            RestoreTaskbarButton(handle);
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
    /// Concealing a window makes Windows report it back as cloaked or hidden, and the
    /// event pipeline has to be able to tell that echo from a window the user or its
    /// own application really did put away.
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
    /// <summary>
    /// Whether a window this committer concealed is still concealed.
    /// </summary>
    /// <remarks>
    /// Asked per method rather than in general, because the three are undone
    /// differently and only one of them is reversible by the application. A cloak can
    /// be lifted by the window itself; a minimised window can be restored from the
    /// taskbar; an <c>SW_HIDE</c> window practically cannot come back on its own.
    /// </remarks>
    private static bool StillConcealed(nint handle, ConcealMethod method) => method switch
    {
        ConcealMethod.Cloaked => Win32Window.GetCloakState(handle)
            is Win32Window.CloakState.App or Win32Window.CloakState.Shell,
        ConcealMethod.Minimised => Win32Window.IsMinimised(handle),
        ConcealMethod.Hidden => !Win32Window.IsVisible(handle),
        _ => false,
    };

    private void Hide(nint handle)
    {
        ConcealMethod? already;

        lock (_lastCommitted)
        {
            already = _concealed.TryGetValue(handle, out ConcealMethod recorded)
                ? recorded
                : null;
        }

        // The record alone is not enough. It used to be the whole test, and a window
        // that came back on screen without Shubbak asking - which applications do -
        // left the record saying "concealed" while the window sat visible. Every
        // later attempt returned here without looking, so it could never be concealed
        // again: it stayed on screen through every workspace switch for the life of
        // the process. Seen with a Teams meeting window, which uncloaks itself.
        //
        // Checked outside the lock: it is a system call, and the record it is
        // checking has already been read.
        if (already is { } method && StillConcealed(handle, method)) return;

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

    /// <summary>
    /// Puts a taskbar button back, if we were the ones who took it away.
    /// </summary>
    /// <remarks>
    /// ITaskbarList is a cross-process call into the shell. Making one per window on
    /// every workspace switch is enough work to make the shell look busy - which is
    /// what it did, as a spinning pointer over whatever window had focus - and when
    /// the button was never removed it achieved nothing at all.
    /// </remarks>
    private void RestoreTaskbarButton(nint handle)
    {
        if (KeepInTaskbar) return;

        Win32Taskbar.SetVisible(handle, visible: true);
    }

    private void Record(nint handle, ConcealMethod method)
    {
        lock (_lastCommitted) _concealed[handle] = method;
    }
}
