using System.Diagnostics;
using Shubbak.Core.Animation;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
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
/// Every rectangle is handed to Windows as one <c>SetWindowPos</c> per window, posted
/// to the window's own thread with <c>SWP_ASYNCWINDOWPOS</c>, and the committer then
/// remembers what it asked for so that nothing downstream has to ask Windows where a
/// window is while the request is still in that thread's queue. Both halves of that
/// sentence replaced something, and both replacements were measured.
/// </para>
/// <para>
/// <b>Why not one <c>DeferWindowPos</c> transaction per frame.</b> That was the design
/// from P0's S2 spike onwards - "the highest-leverage implementation detail in the
/// entire project", per ADR 0001 constraint 3 - and it never ran. <c>DeferWindowPos</c>
/// refuses <c>SWP_NOSENDCHANGING</c> and <c>SWP_ASYNCWINDOWPOS</c> with
/// <c>ERROR_INVALID_PARAMETER</c>, whatever the documentation lists; measured on
/// Windows 11 26200 with every combination of the flags this class uses, and Wine's
/// implementation carries the same accepted-flag mask, so this is not a build quirk.
/// Both flag sets here contained <c>SWP_NOSENDCHANGING</c>, so every batch failed on
/// its first <c>DeferWindowPos</c> and the code fell back to individual
/// <c>SetWindowPos</c> calls - silently, because the fallback existed for resource
/// pressure and nobody checked <c>GetLastError</c>. The spike's batched path had the
/// same flags and <c>return</c>ed on the same failure, which is how it moved twenty
/// windows in 11 microseconds and dropped no frames: it moved none of them. The
/// "33-42% dropped frames" of its unbatched control group measured twenty
/// <i>synchronous</i> <c>SetWindowPos</c> calls into twenty threads, which is the
/// blocking described below and not a property of batching at all. ADR 0001 carries
/// the correction.
/// </para>
/// <para>
/// There is also no batch to want. <c>EndDeferWindowPos</c> moves each window on its
/// own thread in turn, waiting for each, so a transaction is only as fast as its
/// slowest target and offers no visual atomicity across processes in exchange. The
/// one thing it could do that individual calls cannot - give up <c>SWP_NOZORDER</c>
/// and restack several windows together - is nothing a tiling layout needs.
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
    /// <summary>The flags every placement is posted with.</summary>
    /// <remarks>
    /// <para>
    /// <c>SWP_ASYNCWINDOWPOS</c> on every placement, not only on animation frames.
    /// Without it <c>SetWindowPos</c> <i>sends</i> to the target window's thread and
    /// blocks until that thread has processed it, so every window being placed gets to
    /// decide how long the window manager's tick takes - and the tick is also what
    /// delivers animation frames and dispatches keystrokes.
    /// </para>
    /// <para>
    /// This is not a theory. The first run to measure the commit call found a median
    /// of <b>3.71 ms to move a median of one window</b> - 53% of a 7 ms frame budget -
    /// with a p99 of 73.63 ms and a worst case of 138.75 ms, and that was with the
    /// targets settled. The window most likely to be busy is the one that has just
    /// appeared: a File Explorer window placed synchronously on the tick that saw it
    /// shown blocked for <b>98 ms</b>, during which six of the fourteen frames due to
    /// its neighbours' animation were never emitted; a dialog from the shell arriving
    /// stretched a 140 ms motion to 723 ms with six frames in it. Measured directly
    /// against a window whose thread paused 200 ms between pumps: synchronous
    /// <c>SetWindowPos</c> 109-202 ms, asynchronous 0.01-0.14 ms; a thread pumping
    /// every 15 ms still cost the synchronous call 16 ms.
    /// </para>
    /// <para>
    /// Windows ignores the flag when the calling thread and the target window's thread
    /// share an input queue, so it changes nothing for windows in this process - the
    /// tests' - and everything for the cross-process ones that were doing the blocking.
    /// </para>
    /// <para>
    /// What asynchrony costs is that <c>GetWindowRect</c> reports the old rectangle
    /// until the target thread gets round to the request - exactly as long as the
    /// synchronous call would have blocked - and the layout pass reads that rectangle
    /// as the place an animation should start from. Read it stale, and a window that
    /// has just been told to go somewhere is animated there again from where it used
    /// to be; when the application catches up it applies the first instruction, jumps
    /// back for the second's early frames and travels again. Seen as a new window that
    /// shrinks into its tile, grows, shrinks, grows and shrinks. The remedy is
    /// <see cref="CurrentBounds"/>: the committer knows what it asked for, and answers
    /// with that until the window is seen to have arrived or has clearly gone
    /// elsewhere.
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
    /// <para>
    /// A window whose thread has stopped answering needs nothing further. The request
    /// is posted and waits in its queue; it is not going to redraw at the new size
    /// until it starts answering again, and when it does it will process the position
    /// it was sent. Before the flag was universal this case had its own set of flags
    /// and an <c>IsHungAppWindow</c> check per window to choose them, because one
    /// window that was not pumping blocked a whole workspace switch for seconds.
    /// </para>
    /// </remarks>
    private const uint DefaultFlags =
        (uint)(SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
               SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
               SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
               SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING |
               SET_WINDOW_POS_FLAGS.SWP_NOCOPYBITS |
               SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);

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
    /// <para>
    /// <b><c>SWP_NOCOPYBITS</c> on the settling frame only.</b> The flag discards the
    /// window's client area and asks the application to repaint all of it. That is the
    /// right trade for a single placement and the wrong one twenty times in a hundred
    /// and forty milliseconds: an animated move was ordering a full repaint on every
    /// frame, so an application that could not paint that fast showed bare or stale
    /// content for the whole motion rather than at the end of it. Reported against
    /// Elgato Control Center, a <c>WS_EX_LAYERED</c> WPF window - layered windows are
    /// composited from a bitmap the application maintains, and WPF renders them in
    /// software, so a full repaint is about the most expensive thing that can be asked
    /// of one.
    /// </para>
    /// <para>
    /// Without the flag Windows blits the still-valid bits into the new frame and
    /// invalidates only the newly exposed region, so an intermediate frame costs the
    /// application a strip instead of a surface. The interior can be a frame stale
    /// while it moves, which is what the flag existed to prevent - but the frame the
    /// window comes to rest on keeps it, and that is the one anybody looks at.
    /// <c>ideas/analysis3.md</c> listed this as the open question left by
    /// <c>SWP_ASYNCWINDOWPOS</c>; this is the answer.
    /// </para>
    /// <para>
    /// One-shot placement in <see cref="DefaultFlags"/> is untouched. There is one
    /// repaint there and nothing to amortise it over.
    /// </para>
    /// </remarks>
    private static uint FlagsFor(bool isFinal, bool sizeUnchanged)
    {
        uint flags = isFinal
            ? DefaultFlags
            : DefaultFlags & ~(uint)SET_WINDOW_POS_FLAGS.SWP_NOCOPYBITS;

        return sizeUnchanged
            ? flags | (uint)SET_WINDOW_POS_FLAGS.SWP_NOSIZE
            : flags;
    }

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
    /// Each window's resting rectangle that was posted and has not yet been seen to
    /// arrive. Absent once it has, or once the committer has stopped expecting it to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every placement is posted rather than sent, so between the post and the moment
    /// the target thread gets to it the window is, as far as <c>GetWindowRect</c> is
    /// concerned, still wherever it was. For a healthy application that is under a
    /// millisecond; for one that has just opened its window and is busy building its
    /// contents it was measured at 69-108 ms for Firefox and 86 ms for File Explorer,
    /// and a slow machine makes it longer still. Anything that asks "where is this
    /// window" in that gap - the layout pass choosing an animation's origin, the drift
    /// watch, the full-screen detector - gets the old answer, and acts on it.
    /// </para>
    /// <para>
    /// So the committer remembers that it asked, and <see cref="CurrentBounds"/>
    /// answers with what it asked for until the window is seen at the rectangle, is
    /// seen to have gone somewhere else, or <see cref="TransitGrace"/> runs out - see
    /// <see cref="JudgeTransit"/> for the rule. Only resting rectangles are recorded -
    /// a layout placement, or the final frame of a motion - because while a motion is
    /// in flight the animation engine already owns the answer to where the window is.
    /// </para>
    /// <para>
    /// Judged per look rather than on a timer. Each question about a window that has
    /// one of these costs a <c>GetWindowRect</c>, which is a few hundred nanoseconds,
    /// and the entry is dropped the first time the question is settled, so on a
    /// settled desktop the table is empty and nothing is paid at all.
    /// </para>
    /// </remarks>
    private readonly Dictionary<nint, Pending> _posted = [];

    /// <summary>One outstanding placement.</summary>
    /// <param name="Ticks">When it was posted.</param>
    /// <param name="Origin">
    /// The window rectangle Windows reported at the moment of posting, when the post
    /// was the only request of ours outstanding for the window; <see langword="null"/>
    /// for the final frame of a motion, behind which the window may still be working
    /// through earlier frames. See <see cref="JudgeTransit"/> for what each means.
    /// </param>
    private readonly record struct Pending(long Ticks, Rect? Origin);

    /// <summary>
    /// How long a posted placement is trusted over what the window reports before the
    /// committer gives up waiting and believes <c>GetWindowRect</c> again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ambiguity this bounds: a window not at the rectangle it was sent to, and
    /// not moved since, is either still working through its queue or has moved itself
    /// back to exactly where it started, and from outside the two look identical.
    /// Within the grace the first is assumed, which is right for a window whose thread
    /// is merely busy and wrong for one that put itself back - that case is caught by
    /// the drift watch, which is simply deferred until the grace lapses rather than
    /// fooled. After it, the second is assumed, which is right for the self-moving
    /// window and wrong for one that is genuinely this slow; what it costs that one is
    /// the re-animation this whole table exists to prevent, and a window that cannot
    /// process a posted message in a second is close enough to hung that nothing it
    /// does looks right anyway.
    /// </para>
    /// <para>
    /// A second, then: an order of magnitude above the slowest arrival measured and
    /// short enough that a window which did move itself is put back before anyone
    /// decides it is staying there.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan TransitGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How close to its posted rectangle a window has to be before it is taken to have
    /// arrived there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tight, and on every edge. The drift watch's tolerance is a hundred and twenty
    /// pixels of position only, because it asks a different question - "has this gone
    /// somewhere else" - and must not be tripped by a terminal rounding to a character
    /// cell. Asked here, that tolerance says a window halved in place has arrived the
    /// moment it is posted, since halving changes no position, and the layout pass
    /// then animates the resize a second time from the size the window still has.
    /// </para>
    /// <para>
    /// Four pixels, which is below the distance the animation engine bothers to
    /// animate at all. That is what makes a too-tight tolerance harmless: a window
    /// that lands within it is read as being where it is, and whatever few pixels
    /// separate that from the target are too few to move it for. A window whose
    /// application insists on a size of its own never arrives by this measure and is
    /// simply trusted at its target until the grace lapses, which costs nothing.
    /// </para>
    /// </remarks>
    public const int ArrivalTolerance = 4;

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

        /// <summary>
        /// Cloaked through the shell for a moment, while a placement travels to a window
        /// that has only just appeared. See <see cref="Veil"/>.
        /// </summary>
        Veiled,
    }

    /// <summary>When each veiled window was veiled, for the timeout.</summary>
    private readonly Dictionary<nint, long> _veiledSince = [];

    /// <summary>
    /// Windows a veil was asked for and could not yet be applied to, with when it was
    /// first asked. See <see cref="Veil"/>.
    /// </summary>
    private readonly Dictionary<nint, long> _veilWanted = [];

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
    /// Forgets where a window was put, so the next pass places it again rather than
    /// skipping it as already there.
    /// </summary>
    public void Forget(nint handle)
    {
        lock (_lastCommitted)
        {
            _lastCommitted.Remove(handle);
            _lastApplied.Remove(handle);
            _concealed.Remove(handle);
            _posted.Remove(handle);
            _inFlight.Remove(handle);
            _veiledSince.Remove(handle);
            _veilWanted.Remove(handle);
        }

        ForgetShadow(handle);
    }

    /// <summary>
    /// Keeps a newly arrived window out of sight until its placement has landed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An application shows its window where it chooses, and Shubbak is told only
    /// once it is on screen. The placement that follows is posted and takes effect
    /// when the application's thread gets to it - 86 to 150 ms for a File Explorer
    /// window still building its view, measured - and until then the window sits
    /// where the application put it: full-size across its neighbours, or on the
    /// monitor it was last dragged to. The cloak is the same one an inactive
    /// workspace uses, applied on the tick that sees the window and lifted by
    /// <see cref="UnveilDue"/> once the window is seen at its rectangle, so what the
    /// user first sees is the window in its tile.
    /// </para>
    /// <para>
    /// Recorded as its own method rather than as a workspace concealment, because the
    /// two end differently: a concealed window is shown when its workspace is, a
    /// veiled one when its placement lands or a timeout says enough. <see cref="Show"/>
    /// therefore leaves a veiled window alone - the layout pass calls it for every
    /// visible placement, this one included - and <see cref="Hide"/> turns a veil into
    /// an ordinary concealment, since a window whose workspace is switched away while
    /// it is still arriving has acquired the better reason to stay hidden. Restore and
    /// RestoreAll lift it like any other, so a window released or a daemon exiting
    /// mid-arrival leaves nothing cloaked behind.
    /// </para>
    /// <para>
    /// The shell catalogues new windows asynchronously, and on the tick that sees a
    /// window shown it has, every time it was measured, no view for it yet - so the
    /// cloak is refused at the one moment it is wanted most. The request is therefore
    /// remembered and retried from <see cref="UnveilDue"/> on every tick until the
    /// shell has the view, the placement is seen to have landed, or the timeout
    /// lapses; whichever comes first ends it. The debug log says how long the shell
    /// took, which is the number that decides whether this is worth having for a
    /// given application.
    /// </para>
    /// </remarks>
    /// <returns>Whether the window is veiled now. False also when the veil is merely wanted.</returns>
    public bool Veil(nint handle)
    {
        if (handle == 0 || !Win32Window.Exists(handle)) return false;

        lock (_lastCommitted)
        {
            // Already off screen for some reason of its own; a veil would only confuse
            // whose reason ends it.
            if (_concealed.ContainsKey(handle)) return false;
        }

        long now = Stopwatch.GetTimestamp();

        // Asked read-only first. The shell's cloak call refreshes the collection and
        // retries when it has no view, and warns - once per run, with advice to run
        // `shubbak restore` - when it still has none. For a window shown a millisecond
        // ago that is the expected answer rather than a failure, and the warning is
        // kept for a concealment that genuinely cannot be done.
        if (!Win32ApplicationView.HasView(handle) || !Win32ApplicationView.Cloak(handle))
        {
            lock (_lastCommitted) _veilWanted[handle] = now;
            return false;
        }

        RecordVeil(handle, now);

        if (Log.IsEnabled(LogLevel.Debug))
            Log.Debug(LogCategory.Window, $"veiled 0x{handle:X} until it is seen in its tile");

        return true;
    }

    private void RecordVeil(nint handle, long now)
    {
        lock (_lastCommitted)
        {
            _veilWanted.Remove(handle);
            _concealed[handle] = ConcealMethod.Veiled;
            _veiledSince[handle] = now;
        }
    }

    /// <summary>How many windows are veiled or waiting to be, so the loop knows to keep looking.</summary>
    public int VeiledCount
    {
        get { lock (_lastCommitted) return _veiledSince.Count + _veilWanted.Count; }
    }

    /// <summary>
    /// Lifts the veil from every window whose placement has landed, or has been
    /// waited for long enough; and puts one on any window still waiting for the shell
    /// to be able to.
    /// </summary>
    /// <param name="timeout">
    /// The longest a window stays veiled, and the longest a veil is pursued. The
    /// safety valve for a window that never applies the rectangle it was sent; past
    /// it, the window is shown wherever it is.
    /// </param>
    /// <returns>How many windows were unveiled.</returns>
    public int UnveilDue(TimeSpan timeout)
    {
        RetryWantedVeils(timeout);

        nint[] veiled;

        lock (_lastCommitted)
        {
            if (_veiledSince.Count == 0) return 0;

            veiled = [.. _veiledSince.Keys];
        }

        int unveiled = 0;

        foreach (nint handle in veiled)
        {
            long since;

            lock (_lastCommitted)
            {
                // Hidden or released since the list was taken; nothing of ours to lift.
                if (!_veiledSince.TryGetValue(handle, out since)) continue;
            }

            bool waited = Stopwatch.GetElapsedTime(since) >= timeout;

            // IsInTransit is the arrival test: false once the window is seen at its
            // rectangle - or seen to have gone elsewhere, which is also a place it can
            // be shown - and true only while it is believed to be on its way.
            if (!waited && Win32Window.Exists(handle) && IsInTransit(handle))
            {
                // Still waiting, so the veil should still be on. It is not always: File
                // Explorer's window is a view of the shell's own, and the shell lifts
                // the cloak on it a few milliseconds into the window's start-up as part
                // of showing it. Put back, once per tick, for as long as the wait lasts.
                if (Win32Window.GetCloakState(handle) == Win32Window.CloakState.None &&
                    Win32ApplicationView.Cloak(handle) &&
                    Log.IsEnabled(LogLevel.Debug))
                {
                    Log.Debug(LogCategory.Window, $"re-veiled 0x{handle:X}: something had lifted the cloak while its placement was still on its way");
                }

                continue;
            }

            if (Unveil(handle))
            {
                unveiled++;

                if (Log.IsEnabled(LogLevel.Debug))
                {
                    Log.Debug(LogCategory.Window,
                        waited
                            ? $"unveiled 0x{handle:X}: waited {timeout.TotalMilliseconds:F0} ms for its placement; shown where it is"
                            : $"unveiled 0x{handle:X} in its tile after {Stopwatch.GetElapsedTime(since).TotalMilliseconds:F0} ms");
                }
            }
        }

        return unveiled;
    }

    /// <summary>Tries again to veil the windows the shell could not cloak when asked.</summary>
    private void RetryWantedVeils(TimeSpan timeout)
    {
        nint[] wanted;

        lock (_lastCommitted)
        {
            if (_veilWanted.Count == 0) return;

            wanted = [.. _veilWanted.Keys];
        }

        foreach (nint handle in wanted)
        {
            long since;

            lock (_lastCommitted)
            {
                if (!_veilWanted.TryGetValue(handle, out since)) continue;
            }

            double waitedMs = Stopwatch.GetElapsedTime(since).TotalMilliseconds;

            // Landed, gone, or waited for as long as a veil would have been worn: there
            // is nothing left for the veil to hide.
            if (waitedMs >= timeout.TotalMilliseconds || !Win32Window.Exists(handle) || !IsInTransit(handle))
            {
                lock (_lastCommitted) _veilWanted.Remove(handle);

                if (Log.IsEnabled(LogLevel.Debug))
                    Log.Debug(LogCategory.Window, $"veil for 0x{handle:X} not applied: the placement landed, or {waitedMs:F0} ms passed, before the shell had a view for it");

                continue;
            }

            lock (_lastCommitted)
            {
                // Concealed for a real reason in the meantime; that reason wins.
                if (_concealed.ContainsKey(handle))
                {
                    _veilWanted.Remove(handle);
                    continue;
                }
            }

            // One read-only round trip per tick until the shell has catalogued the
            // window; the cloak itself, with its refreshes and its warning, only then.
            if (!Win32ApplicationView.HasView(handle) || !Win32ApplicationView.Cloak(handle)) continue;

            RecordVeil(handle, Stopwatch.GetTimestamp());

            if (Log.IsEnabled(LogLevel.Debug))
                Log.Debug(LogCategory.Window, $"veiled 0x{handle:X} after {waitedMs:F0} ms: the shell took that long to catalogue it");
        }
    }

    private bool Unveil(nint handle)
    {
        lock (_lastCommitted)
        {
            if (!_veiledSince.Remove(handle)) return false;

            // Only a veil is lifted here. Hide may have turned it into a concealment in
            // the meantime, and that one ends when the workspace is shown.
            if (_concealed.TryGetValue(handle, out ConcealMethod method) && method == ConcealMethod.Veiled)
                _concealed.Remove(handle);
            else
                return false;
        }

        if (Win32Window.Exists(handle)) Win32ApplicationView.Uncloak(handle);

        return true;
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

            Undo(handle, method);
            restored++;
        }

        return restored;
    }

    /// <summary>
    /// Brings one window back that this instance concealed, and forgets that it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a window being let go of. <see cref="Reveal"/> is the layout's reveal and
    /// leaves a minimised window minimised, because the layout runs constantly and
    /// must not keep overriding a deliberate choice; but a minimised window that was
    /// also cloaked for its workspace has to be un-cloaked when it is released, or it
    /// comes back from the taskbar to nowhere. So this is <see cref="RestoreAll"/> for
    /// one window: the concealment is reversed by the route that applied it, whatever
    /// else the window is doing.
    /// </para>
    /// <para>
    /// Nothing happens to a window this instance did not conceal. A window its own
    /// application hid is the application's business.
    /// </para>
    /// </remarks>
    /// <returns>Whether the window had been concealed by this instance.</returns>
    public bool Restore(nint handle)
    {
        ConcealMethod method;

        lock (_lastCommitted)
        {
            if (!_concealed.TryGetValue(handle, out method)) return false;

            _concealed.Remove(handle);
        }

        if (Win32Window.Exists(handle)) Undo(handle, method);

        return true;
    }

    /// <summary>Reverses one concealment by the route that applied it.</summary>
    /// <remarks>
    /// Cloaking goes through the shell, so un-cloaking must too: DwmSetWindowAttribute
    /// is scoped to the owning process and silently refuses every window Shubbak
    /// manages, which made shutdown report windows restored while leaving them all
    /// cloaked.
    /// </remarks>
    private void Undo(nint handle, ConcealMethod method)
    {
        switch (method)
        {
            case ConcealMethod.Cloaked:
                Win32ApplicationView.Uncloak(handle);
                RestoreTaskbarButton(handle);
                break;

            case ConcealMethod.Veiled:
                lock (_lastCommitted) _veiledSince.Remove(handle);
                Win32ApplicationView.Uncloak(handle);
                break;

            case ConcealMethod.Minimised:
                PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_RESTORE);
                break;

            default:
                PInvoke.ShowWindowAsync(new HWND(handle), SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
                break;
        }
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

        // Visibility is applied before the moves, and separately: ShowWindow is its
        // own call, and hiding a window we are also moving would make the move
        // pointless.
        (nint Handle, Rect Rect)[]? rentedMoves = placements.Count > 64
            ? System.Buffers.ArrayPool<(nint Handle, Rect Rect)>.Shared.Rent(placements.Count) : null;
        nint[]? rentedRaises = placements.Count > 64 ? System.Buffers.ArrayPool<nint>.Shared.Rent(placements.Count) : null;
        Span<(nint Handle, Rect Rect)> toMove = rentedMoves is null
            ? stackalloc (nint Handle, Rect Rect)[placements.Count] : rentedMoves.AsSpan(0, placements.Count);
        Span<nint> toRaise = rentedRaises is null ? stackalloc nint[placements.Count] : rentedRaises.AsSpan(0, placements.Count);
        int moveCount = 0, raiseCount = 0;
        try
        {

        // Raises are separate from moves, and are applied even when nothing moved.
        // Entering monocle changes no rectangle at all - every window already fills
        // the area - so the stacking change is the entire visible effect.
        for (int i = 0; i < placements.Count; i++)
        {
            Placement placement = placements[i];
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

                if (placement.Raise) toRaise[raiseCount++] = handle;
            }

            // A maximised window is Windows's to size. The tree says Maximised for a
            // window the user or a key maximised, and the honest way to fill the work
            // area with a window and keep its frame is the maximise Windows already has
            // - the compositor draws it without a shadow and with the frame off the top
            // of the screen, which a window merely sized to the work area is not. So the
            // flag is set if it is not already, and the window is never moved: SetWindowPos
            // on a zoomed window is what puts a black strip along the top.
            if (placement.Window.State == WindowState.Maximised)
            {
                if (placement.Visible && !Win32Window.IsMaximised(handle))
                    WindowActions.Maximise(handle);

                lock (_lastCommitted)
                {
                    _lastCommitted[handle] = placement.Rect;
                    _lastApplied[handle] = placement.Rect;

                    // Nothing of ours is on its way to this window any more; the
                    // maximise is Windows's to carry out.
                    _posted.Remove(handle);
                }

                continue;
            }

            // Skip windows already where we want them. This is the difference
            // between a relayout costing one SetWindowPos and costing dozens, and
            // it also suppresses a large share of the LOCATIONCHANGE echo.
            //
            // Judged on the target, not on an exact comparison with where the window
            // actually is. That comparison was tried and reverted: a terminal snapping
            // to whole character cells never lands precisely where it was put, so it
            // failed on every pass and the window was moved again on every layout -
            // and since focus changes trigger a layout, that was a visible jump every
            // time focus moved.
            //
            // But the target alone is not enough either, and the comment that used to
            // sit here said this case was "handled where it should be: the
            // location-change event". It was not. That subscription was removed the
            // following day, for its own good reasons, and nothing replaced it - so a
            // window that moved itself was skipped here on every subsequent pass, for
            // ever, because the target had not changed and therefore there was
            // seemingly nothing to do.
            //
            // That is what left a browser restoring its remembered geometry sitting on
            // the wrong monitor until the user pressed wm-redraw, which cures it only
            // because Redraw forgets every cached rectangle.
            //
            // So the window is asked where it is, but only when it was about to be
            // skipped, and only coarsely enough that character-cell rounding cannot
            // trip it. See PlacementDrift - and HasWandered, which declines to ask at
            // all while the previous placement is still on its way to the window.
            bool alreadyCommanded;

            lock (_lastCommitted)
            {
                alreadyCommanded = _lastCommitted.TryGetValue(handle, out Rect previous)
                    && previous == placement.Rect
                    && _lastApplied.ContainsKey(handle);
            }

            // The geometry read is outside the lock on purpose. It is a syscall, and
            // holding a lock across one that every layout pass performs per window is
            // how a cheap check becomes a contended one.
            if (alreadyCommanded && !HasWandered(handle, placement)) continue;

            toMove[moveCount++] = (handle, placement.Rect);
        }

        toMove = toMove[..moveCount];
        toRaise = toRaise[..raiseCount];
        if (moveCount == 0)
        {
            RaiseAll(toRaise);
            return 0;
        }

        // Nothing is placed while Windows still believes it is maximised.
        //
        // The compositor draws a maximised window as though it fills the monitor - no
        // shadow, and part of the frame deliberately off the top of the screen. Moved
        // to half the screen with the flag still set, that frame lands back on screen
        // as a black strip along the top, and the focus border is drawn around a shape
        // that is not the window. Applications that reopen maximised, which is most of
        // the Store ones, arrived that way and were tiled that way.
        //
        // Here rather than at adoption, because adoption is only one of the routes in.
        // An application maximising itself, or a window the tree has just taken out of
        // Maximised, both carry the flag into a placement that is not maximised, and
        // this is the one place every rectangle passes through. A window the *user*
        // maximised is also caught here if a pass happens to run before the daemon's
        // detection has looked - at most one poll, a fifth of a second - and is then
        // tiled; the next Win+Up is honoured, since the detection sees it first. The
        // alternative, leaving a zoomed window alone here, was tried and made
        // toggle-maximized a one-way door: the tree said Tiling, the window stayed
        // zoomed, and the detection put the tree back to Maximised a second later.
        //
        // The cost is one IsZoomed per window actually being moved - windows already
        // where they belong were skipped above - which is a style read beside a
        // SetWindowPos that is about to cross a process boundary.
        foreach ((nint handle, Rect rect) in toMove)
        {
            if (Win32Window.IsMaximised(handle))
                WindowActions.Unmaximise(handle, Expand(handle, rect));
        }

        long now = Stopwatch.GetTimestamp();

        try
        {
            foreach ((nint handle, Rect rect) in toMove)
            {
                // Read before the post, so the record says where the window was when
                // it was asked to go; see Pending.Origin.
                Rect origin = Win32Window.GetBounds(handle);

                MoveSingle(handle, rect);

                lock (_lastCommitted)
                {
                    _posted[handle] = new Pending(now, origin);

                    // A direct placement supersedes whatever motion the window was in;
                    // the next motion's first frame must not be judged against it.
                    _inFlight.Remove(handle);
                }
            }
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

        return moveCount;
        }
        finally
        {
            if (rentedMoves is not null) System.Buffers.ArrayPool<(nint Handle, Rect Rect)>.Shared.Return(rentedMoves);
            if (rentedRaises is not null) System.Buffers.ArrayPool<nint>.Shared.Return(rentedRaises);
        }
    }

    /// <summary>Brings windows to the front of the ordinary stacking order.</summary>
    /// <remarks>
    /// <para>
    /// <c>HWND_TOP</c>, not <c>HWND_TOPMOST</c>. Topmost would place the window above
    /// every other application permanently, which is a different feature and an
    /// unwelcome one; this only orders a window against its own siblings.
    /// </para>
    /// <para>
    /// Applied after the move so the raise is not undone by it. Posted like the moves
    /// are, so the order the target thread sees is the order given here. The list is
    /// short - only fullscreen, maximised and monocle windows ask.
    /// </para>
    /// </remarks>
    private static void RaiseAll(ReadOnlySpan<nint> handles)
    {
        foreach (nint handle in handles) Raise(handle);
    }

    /// <summary>Brings one window to the front of the ordinary stacking order.</summary>
    /// <remarks>
    /// <para>
    /// Public because an animated window never reaches <see cref="Commit"/>, which is
    /// where <c>Placement.Raise</c> is otherwise honoured. The layout pass raises those
    /// itself, at the moment it hands the window to the animation engine.
    /// </para>
    /// <para>
    /// Posted, for the same reason every placement is: a restack is a request to the
    /// window's own thread, and sent synchronously it waited on that thread - 16 ms
    /// against a window pumping every 15 ms, measured; the whole of a stall against
    /// one that was busy.
    /// </para>
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
            SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING |
            SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>Posts one window's placement to its thread.</summary>
    /// <param name="handle">The window to move.</param>
    /// <param name="rect">Where its visible frame should end up.</param>
    private static void MoveSingle(nint handle, Rect rect)
    {
        Rect target = Expand(handle, rect);

        PInvoke.SetWindowPos(
            new HWND(handle), HWND.Null,
            target.X, target.Y, target.Width, target.Height,
            (SET_WINDOW_POS_FLAGS)DefaultFlags);
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
    public static Rect VisibleBounds(nint handle) =>
        ShrinkToVisible(handle, Win32Window.GetBounds(handle));

    /// <summary>Forgets a window's measured shadow.</summary>
    /// <remarks>
    /// Called when a window is unmanaged, so the table does not grow for the life of
    /// the process and a recycled handle cannot inherit someone else's margins.
    /// </remarks>
    private static void ForgetShadow(nint handle)
    {
        lock (s_shadowGate) s_shadows.Remove(handle);
    }

    /// <summary>
    /// Whether a window we were about to skip has moved itself somewhere else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked only of windows whose target has not changed since they were last
    /// committed - which in a settled desktop is nearly all of them, so it has to stay
    /// cheap. It is one <c>GetWindowRect</c> plus a shadow margin this class has
    /// already measured and cached.
    /// </para>
    /// <para>
    /// Never while the previous placement is still in transit. A window that has not
    /// yet processed the rectangle it was sent is not somewhere else; it is on its way,
    /// and re-sending the same rectangle would only lengthen its queue.
    /// </para>
    /// </remarks>
    private bool HasWandered(nint handle, Placement placement)
    {
        Rect actual = Win32Window.GetBounds(handle);

        if (IsInTransit(handle, actual)) return false;

        return PlacementDrift.HasEscaped(
            ShrinkToVisible(handle, actual), placement.Rect, placement.Window.Monitor?.Bounds);
    }

    /// <summary>
    /// Where a window's visible frame is, as far as the layout should believe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="VisibleBounds"/> asks Windows, and Windows answers with where the
    /// window's thread has got to, which while a placement is in that thread's queue
    /// is where the window used to be. This answers with the rectangle the committer
    /// posted instead, for as long as it is reasonable to believe the window is still
    /// on its way there - see <see cref="_posted"/> and <see cref="TransitGrace"/> -
    /// and with the real rectangle otherwise.
    /// </para>
    /// <para>
    /// This is the origin a layout pass should hand the animation engine. Handing it
    /// the stale answer started a second journey from the window's old rectangle a
    /// few milliseconds after the first was posted, and the window then visibly made
    /// both.
    /// </para>
    /// </remarks>
    public Rect CurrentBounds(nint handle)
    {
        Rect actual = Win32Window.GetBounds(handle);

        if (IsInTransit(handle, actual))
        {
            lock (_lastCommitted)
            {
                if (_lastCommitted.TryGetValue(handle, out Rect target)) return target;
            }
        }

        return ShrinkToVisible(handle, actual);
    }

    /// <summary>
    /// Whether a placement posted to this window has yet to be seen to arrive.
    /// </summary>
    /// <remarks>
    /// Resolves the question as a side effect: a window seen at its rectangle, or one
    /// whose grace has lapsed, stops being in transit from this call onwards. Callers
    /// that want to act on the window's real position - the drift watch, the
    /// full-screen detector - should simply wait while this is true. Costs a
    /// dictionary probe for a window with nothing outstanding, which on a settled
    /// desktop is every window; the geometry is read only when there is something to
    /// compare it against.
    /// </remarks>
    public bool IsInTransit(nint handle)
    {
        lock (_lastCommitted)
        {
            if (!_posted.ContainsKey(handle)) return false;
        }

        return IsInTransit(handle, Win32Window.GetBounds(handle));
    }

    private bool IsInTransit(nint handle, Rect actual)
    {
        Pending pending;
        Rect applied;

        lock (_lastCommitted)
        {
            if (!_posted.TryGetValue(handle, out pending) || !_lastApplied.TryGetValue(handle, out applied))
                return false;
        }

        TransitVerdict verdict = JudgeTransit(actual, applied, pending.Origin, Stopwatch.GetElapsedTime(pending.Ticks));

        if (verdict == TransitVerdict.InTransit) return true;

        lock (_lastCommitted) _posted.Remove(handle);

        return false;
    }

    /// <summary>What a look at a window with an outstanding placement concluded.</summary>
    public enum TransitVerdict
    {
        /// <summary>The window is at, or within rounding of, the rectangle it was sent.</summary>
        Arrived,

        /// <summary>Not there yet, and believed to be on its way.</summary>
        InTransit,

        /// <summary>Not there, and no longer expected to be.</summary>
        Abandoned,
    }

    /// <summary>
    /// Decides whether a window with an outstanding placement has arrived, is still
    /// on its way, or is not coming.
    /// </summary>
    /// <param name="actual">The window rectangle Windows reports, shadow included.</param>
    /// <param name="applied">The window rectangle that was posted, shadow included.</param>
    /// <param name="origin">
    /// Where the window was when the placement was posted, if that is known to have
    /// been the only request of ours it had outstanding; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="sincePosted">How long ago it was posted.</param>
    /// <remarks>
    /// <para>
    /// Pure, so the rule can be stated in a test without a window to post to. In order:
    /// a window within <see cref="ArrivalTolerance"/> of the rectangle on every edge
    /// has arrived, however long it took. One further away than that after
    /// <see cref="TransitGrace"/> is not coming. One that has moved since the post, to
    /// anywhere other than the rectangle, is not coming either - provided the post was
    /// the only thing of ours in its queue, because then its thread has acted and
    /// acted on something else: the application restoring its own remembered
    /// geometry, which is the drift watch's case and must not be made to wait out the
    /// grace. Otherwise it is on its way.
    /// </para>
    /// <para>
    /// The proviso is what the final frame of a motion fails. A window lagging behind
    /// an animation has several of our frames queued, and moving from one waypoint to
    /// the next is progress towards the rectangle rather than a departure from it - so
    /// for that case no origin is given and only the clock can give up on it.
    /// </para>
    /// </remarks>
    public static TransitVerdict JudgeTransit(Rect actual, Rect applied, Rect? origin, TimeSpan sincePosted)
    {
        if (Math.Abs(actual.X - applied.X) <= ArrivalTolerance &&
            Math.Abs(actual.Y - applied.Y) <= ArrivalTolerance &&
            Math.Abs(actual.Width - applied.Width) <= ArrivalTolerance &&
            Math.Abs(actual.Height - applied.Height) <= ArrivalTolerance)
        {
            return TransitVerdict.Arrived;
        }

        if (sincePosted >= TransitGrace) return TransitVerdict.Abandoned;

        if (origin is { } from && actual != from) return TransitVerdict.Abandoned;

        return TransitVerdict.InTransit;
    }

    /// <summary>How many placements are posted and not yet seen to arrive, for the report.</summary>
    /// <remarks>
    /// Resolved on the way, not merely counted: an entry for a window nothing has asked
    /// about since - one on a concealed workspace, typically - would otherwise be
    /// reported as outstanding for ever. A <c>GetWindowRect</c> per entry, on a call
    /// that happens when somebody runs <c>diagnose</c>.
    /// </remarks>
    public int InTransitCount
    {
        get
        {
            nint[] handles;

            lock (_lastCommitted) handles = [.. _posted.Keys];

            int count = 0;

            foreach (nint handle in handles)
                if (IsInTransit(handle)) count++;

            return count;
        }
    }

    private static Rect ShrinkToVisible(nint handle, Rect outer) =>
        outer.IsEmpty ? outer : Shrink(outer, ShadowOf(handle));

    /// <summary>Places a single window immediately, outside any frame.</summary>
    public void CommitOne(nint handle, Rect rect)
    {
        if (handle == 0 || !Win32Window.Exists(handle)) return;

        Rect origin = Win32Window.GetBounds(handle);

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
                _posted[handle] = new Pending(Stopwatch.GetTimestamp(), origin);
                _inFlight.Remove(handle);
            }
        }
    }

    /// <summary>
    /// Applies one frame of animation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hot path: called every tick while anything is moving. Takes a
    /// <see cref="Span{T}"/> over a caller-owned buffer and allocates nothing, per
    /// ADR 0001 constraint 2. One posted <c>SetWindowPos</c> per window, each
    /// measured at about a tenth of a millisecond whatever the target is doing.
    /// </para>
    /// <para>
    /// Unlike <see cref="Commit"/> this does not skip windows already at their
    /// target, because an animation frame is by definition a new position, and it
    /// does not touch visibility, which the layout pass has already settled.
    /// </para>
    /// <para>
    /// <b>A frame is withheld from a window that has fallen two frames behind.</b>
    /// Every frame is a real resize for the application - a terminal reflows its
    /// buffer and resizes its pty, a browser lays its page out again - and one that
    /// cannot finish a resize before the next frame arrives does not fall behind
    /// gracefully: the posted requests queue up and it works through all of them
    /// later, in a burst, each for a rectangle that is already stale. Measured on a
    /// Firefox window still building itself: nine frames posted at a steady 16 ms,
    /// applied at +69, +135, +159, +207, +226, +245 ms; and on one whose thread was
    /// busy for the whole of a motion: ten frames queued, applied in a burst after it.
    /// So before a frame is posted the window is asked where it is, and a window that
    /// has not changed at all across the last two posts has processed neither and is
    /// not given a third. It gets the next frame on the first tick after it moves - a
    /// later point on the same curve, so what it shows is current rather than
    /// backlogged - and the final frame regardless. A window that keeps up is asked
    /// one <c>GetWindowRect</c> per frame and nothing else changes for it; the busy
    /// one above applied two resizes instead of ten. See <see cref="ShouldWithhold"/>
    /// for the rule.
    /// </para>
    /// </remarks>
    public void CommitFrame(ReadOnlySpan<AnimationFrame> frames)
    {
        if (frames.Length == 0) return;

        long now = Stopwatch.GetTimestamp();

        foreach (AnimationFrame frame in frames)
        {
            nint handle = (nint)frame.Handle;

            // Expanded exactly as the layout path expands, so a window is the same
            // size whether it arrived by animation or by a direct placement. Writing
            // raw here made every animated frame land the visible window about
            // fourteen pixels narrower than the layout asked for, and the next
            // placement snapped it back - a visible pop on every focus change.
            Rect target = Expand(handle, frame.Rect);
            Rect actual = Win32Window.GetBounds(handle);

            InFlight previous;
            bool known;

            lock (_lastCommitted) known = _inFlight.TryGetValue(handle, out previous);

            if (known && !frame.IsFinal &&
                ShouldWithhold(actual, previous.ActualAtPost, previous.ActualAtPostBefore, Stopwatch.GetElapsedTime(previous.Ticks)))
            {
                _framesWithheld++;
                continue;
            }

            // Judged against the frame this window was actually sent, not the one the
            // engine computed before this one: with frames withheld the two differ,
            // and SWP_NOSIZE against a size the window never received would leave it
            // the wrong size until the final frame.
            bool sizeUnchanged = !frame.IsFinal && (known
                ? previous.Posted.Width == target.Width && previous.Posted.Height == target.Height
                : frame.SizeUnchanged);

            PInvoke.SetWindowPos(
                new HWND(handle), HWND.Null,
                target.X, target.Y, target.Width, target.Height,
                (SET_WINDOW_POS_FLAGS)FlagsFor(frame.IsFinal, sizeUnchanged));

            lock (_lastCommitted)
            {
                if (!frame.IsFinal)
                {
                    // The sentinel for "no post before this one" is the empty rectangle,
                    // which a window that exists never reports.
                    _inFlight[handle] = new InFlight(target, actual, known ? previous.ActualAtPost : default, now);
                    continue;
                }

                // Only the final frame is recorded as the window's resting place. Every
                // position before it is a waypoint the window is passing through, and
                // recording those would make the skip check believe the window was
                // already where the layout wants it while it was still halfway there.
                _inFlight.Remove(handle);
                _lastCommitted[handle] = frame.Rect;

                // Recorded for the same reason the layout path records it: the skip
                // check treats a missing entry as "never compensated" and places the
                // window again. After an animation that meant one redundant move,
                // which is what the user saw jump.
                _lastApplied[handle] = target;

                // And as outstanding, because the frame was posted like every other
                // and the window will report its previous waypoint until it catches
                // up - which for a window busy repainting through a motion is exactly
                // when the next layout pass tends to ask. No origin: the window may
                // have an earlier frame still queued, so moving is not leaving.
                _posted[handle] = new Pending(now, Origin: null);
            }
        }
    }

    /// <summary>The last intermediate frame posted to a moving window.</summary>
    /// <param name="Posted">The window rectangle it was sent, shadow included.</param>
    /// <param name="ActualAtPost">The window rectangle Windows reported at that moment.</param>
    /// <param name="ActualAtPostBefore">
    /// The same for the post before it; empty when there was none, which no window that
    /// exists ever reports.
    /// </param>
    /// <param name="Ticks">When.</param>
    private readonly record struct InFlight(Rect Posted, Rect ActualAtPost, Rect ActualAtPostBefore, long Ticks);

    /// <summary>
    /// The last intermediate frame posted to each window still in motion. Dropped by
    /// the final frame, by a direct placement, and by <see cref="Forget"/>.
    /// </summary>
    /// <remarks>
    /// Pre-sized so the frame path never grows it: the engine's own track table starts
    /// at the same size, and a desktop with more windows in motion at once than this
    /// has other problems.
    /// </remarks>
    private readonly Dictionary<nint, InFlight> _inFlight = new(64);

    private long _framesWithheld;

    /// <summary>
    /// How many intermediate frames were not posted because their window had not yet
    /// applied the previous one. For the report, and the motion log.
    /// </summary>
    public long FramesWithheld => Interlocked.Read(ref _framesWithheld);

    /// <summary>
    /// How long a window may go without applying a posted frame before the next one
    /// is posted anyway.
    /// </summary>
    /// <remarks>
    /// Not a courtesy to the window - a frame it has not processed is a frame it does
    /// not need another of - but a guard against a stale record. A motion the engine
    /// drops without a final frame leaves its last entry behind, and a window that
    /// happens not to have moved since would otherwise have its next motion's frames
    /// withheld against a post from minutes ago. Frames arrive every few milliseconds
    /// while a window is genuinely behind, so a record this old describes nothing
    /// current.
    /// </remarks>
    public static readonly TimeSpan WithholdingLapses = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Whether the next frame should be kept from a window, because it has fallen two
    /// or more frames behind.
    /// </summary>
    /// <param name="actual">The window rectangle Windows reports now.</param>
    /// <param name="actualAtLastPost">What it reported when the last frame was posted.</param>
    /// <param name="actualAtPostBefore">What it reported when the frame before that was posted.</param>
    /// <param name="sinceLastPost">How long ago the last post was.</param>
    /// <remarks>
    /// <para>
    /// Pure, so the rule can be stated in a test. A window that has not changed at all
    /// across the last two posts has processed neither of them; one that has changed
    /// in any way since the post before last is at worst one frame behind, and one
    /// frame behind is the ordinary state of an application that takes about a frame
    /// to apply a resize - a steady lag, not a growing queue - so it is given the
    /// next frame. Measured on a browser that keeps up: judged on the last post alone,
    /// a check landing a moment before the window applied it cost that window a
    /// frame it would have shown in time.
    /// </para>
    /// <para>
    /// Any change counts as progress, so an application that adjusts the sizes it is
    /// given - and so lands near a frame rather than on it - is never mistaken for one
    /// that is stuck. The clock only guards against a stale record; see
    /// <see cref="WithholdingLapses"/>.
    /// </para>
    /// </remarks>
    public static bool ShouldWithhold(Rect actual, Rect actualAtLastPost, Rect actualAtPostBefore, TimeSpan sinceLastPost) =>
        actual == actualAtLastPost && actualAtLastPost == actualAtPostBefore && sinceLastPost < WithholdingLapses;

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

            // A veil is not the layout's to lift: it ends when the placement lands or
            // the timeout says enough, in UnveilDue. The layout pass calls this for
            // every visible placement, the veiled window's included, and must not undo
            // the veil on the same tick it was put on.
            if (method == ConcealMethod.Veiled) return;

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
        // recovery that makes concealment survivable.
        //
        // The shell's cloak only. A previous run concealed through the shell and that
        // reports as Shell; the per-process DWMWA_CLOAK route reports as App and has
        // never succeeded against a window of another process, so an App cloak on a
        // managed window is the application's own. Firefox and Chromium cloak a new
        // window themselves until its first paint, and reversing that here cost three
        // cross-process calls into the shell per new browser window - 13 ms on the
        // tick that placed it, measured - all of which failed, before logging a
        // warning that told the user to run `shubbak restore` for a window that was
        // about to uncloak itself twenty milliseconds later.
        Win32Window.CloakState cloak = Win32Window.GetCloakState(handle);

        if (cloak == Win32Window.CloakState.Shell)
        {
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
        ConcealMethod.Cloaked or ConcealMethod.Veiled => Win32Window.GetCloakState(handle)
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

            // A veiled window whose workspace has been switched away is already
            // cloaked, and now for the better reason. The veil becomes the ordinary
            // concealment, which ends when the workspace is shown rather than when the
            // placement lands - so UnveilDue will not put it on screen over another
            // workspace a moment from now.
            if (already == ConcealMethod.Veiled)
            {
                _veiledSince.Remove(handle);
                _concealed[handle] = ConcealMethod.Cloaked;
                already = ConcealMethod.Cloaked;
            }
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
