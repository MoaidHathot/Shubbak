using System.Diagnostics;
using Shubbak.Config;
using Shubbak.Core.Animation;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Shubbak.Core.Commands;
using Shubbak.Core.Layouts;
using Shubbak.Core.Rendering;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.Wm;

/// <summary>
/// The Shubbak daemon: wires the platform layer to the state machine.
/// </summary>
/// <remarks>
/// <para>
/// This is the only class that talks to both sides, and it is deliberately thin.
/// Everything it does is translate: a WinEvent becomes a
/// <see cref="WindowManager"/> call, a keystroke becomes a
/// <see cref="WmCommand"/>, and the resulting placements become one
/// <c>DeferWindowPos</c> transaction.
/// </para>
/// <para>
/// All of it runs on a single thread with a message pump. Both hook kinds require
/// one, and single-threading removes the need for locks around the tree - which
/// matters, because a tree mutated concurrently with an arrange pass would produce
/// corrupt geometry that is essentially impossible to reproduce.
/// </para>
/// </remarks>
public sealed class WmDaemon : IDisposable
{
    private readonly WindowManager _wm;
    private readonly CommandExecutor _executor;
    private readonly BindingTable _bindings = new();
    private readonly RuleEngine _rules = new();
    private readonly WindowCommitter _committer = new();
    private readonly MessageLoop _loop = new();
    private readonly AnimationEngine _animation = new();

    /// <summary>
    /// Which windows Shubbak manages, and which it has decided not to.
    /// </summary>
    /// <remarks>
    /// Four sets that have to agree with one another, kept together with the
    /// operations that move a window between them. Nearly every window-lifecycle bug
    /// this program has had lived in their interplay rather than in any one of them.
    /// </remarks>
    private readonly WindowRegistry _windows = new();

    /// <summary>How long each tick took, and how far apart they landed.</summary>
    /// <remarks>
    /// The interval is the one that answers whether the loop runs at the rate it asks
    /// for. It asks for 8 ms and sleeps for it, and a plain sleep is rounded up to the
    /// system timer resolution - so the honest answer has to be measured rather than
    /// assumed from the number in the call.
    /// </remarks>
    private readonly LatencyStats _tickDuration = new(4096, "tick duration");

    private readonly LatencyStats _tickInterval = new(4096, "tick interval");

    /// <summary>
    /// How far apart ticks landed, counted only while something was moving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="_tickInterval"/> because that one is bimodal and
    /// therefore unreadable. The loop waits 250 ms when idle and 7 ms while
    /// animating, so a percentile over both is a statement about the ratio between
    /// them rather than about either - on a desktop nobody is touching it reads about
    /// 4 Hz and means nothing.
    /// </para>
    /// <para>
    /// A percentile over this one is a frame rate, which is the number the animation
    /// work has to be judged by and the number nothing has ever reported.
    /// </para>
    /// </remarks>
    private readonly LatencyStats _frameInterval = new(4096, "frame interval");

    /// <summary>How long committing one frame of motion took.</summary>
    /// <remarks>
    /// Timed on its own rather than as part of the tick, because ADR 0001 measured
    /// 94.6% of frame time inside this call at twenty windows - 93% to 97% across the
    /// configurations it tried - and nothing has checked that since the spike. Paired
    /// with <see cref="_frameBatchSize"/>, without which the duration means nothing.
    /// </remarks>
    private readonly LatencyStats _commitFrameDuration = new(4096, "commit frame");

    /// <summary>How many windows moved in each frame.</summary>
    /// <remarks>
    /// A frame of two windows and a frame of twenty are not the same measurement, and
    /// a duration without this beside it cannot be compared against anything.
    /// </remarks>
    private readonly LatencyStats _frameBatchSize = new(4096, "frame batch");

    /// <summary>Bytes allocated by each tick, on this thread.</summary>
    /// <remarks>
    /// The line ADR 0001 constraint 2 draws, which has never been measured in the
    /// shipping binary. Allocation here means a collection here, and a collection
    /// suspends every thread in the process - including the one holding a keystroke
    /// the user is waiting on.
    /// </remarks>
    private readonly LatencyStats _tickAllocation = new(4096, "tick allocation");

    /// <summary>Wall-clock milliseconds spent with something in motion.</summary>
    private double _animatingMs;

    /// <summary>Frames actually committed, against what the interval asked for.</summary>
    private long _framesDelivered;

    /// <summary>Motions started, so the report can say how many animations that was.</summary>
    private long _animationsStarted;

    private readonly long _startedTicks = Stopwatch.GetTimestamp();

    /// <summary>Window geometry captured when a mouse drag started.</summary>
    private readonly Dictionary<nint, Rect> _dragOrigin = [];

    /// <summary>The window currently wearing the focused border.</summary>
    private WindowNode? _borderedWindow;
    private long _lastBorderRefreshTicks;

    private WinEventSource? _winEvents;
    private KeyboardSource? _keyboard;

    private ShubbakConfig _config = ShubbakConfig.Default;
    private string? _configPath;

    private bool _layoutDirty;

    /// <summary>How the next layout pass should animate whatever it moves.</summary>
    /// <remarks>
    /// Carried from the events that made the layout dirty, because the reason for a
    /// pass is known when it is requested and gone by the time it runs. Without it the
    /// layout-change and workspace-switch profiles were unreachable: both were parsed,
    /// documented and tunable, and nothing ever constructed either.
    /// </remarks>
    private AnimationKind _pendingLayoutKind = AnimationKind.WindowMove;

    private bool _disposed;

    private Session? _session;
    private readonly HashSet<int> _claimedSessionEntries = [];
    private bool _restoring;
    private int _revived;
    private int _revivalBudget;

    private long _lastSessionSaveTicks;
    private long _lastMonitorSyncTicks;

    /// <summary>
    /// Whether the log level came from the command line, and so must not be
    /// overridden by config.
    /// </summary>
    private readonly bool _logLevelFromCommandLine =
        Environment.GetCommandLineArgs().Contains("--log-level", StringComparer.Ordinal);

    private long _lastTickTicks;

    private readonly WinEventNotification[] _eventScratch = new WinEventNotification[256];
    private readonly KeyEvent[] _keyScratch = new KeyEvent[64];

    /// <summary>
    /// Per-frame animation output. Pre-allocated because the tick path must not
    /// allocate (docs/adr/0001-language-choice.md, constraint 2).
    /// </summary>
    private AnimationFrame[] _frameScratch = new AnimationFrame[128];

    /// <summary>Reused across frames so committing never allocates either.</summary>
    private readonly List<Placement> _commitScratch = [];

    private IpcServer? _ipc;

    /// <summary>
    /// Work handed in by IPC threads, to be run on the daemon thread.
    /// </summary>
    /// <remarks>
    /// The tree must never be touched from a pipe thread: an arrange pass running
    /// concurrently with a mutation would observe a half-changed tree and produce
    /// geometry that is essentially impossible to reproduce or debug.
    /// </remarks>
    private readonly Queue<Action> _inbox = new();
    private readonly Lock _inboxGate = new();

    /// <summary>Where the session is stored; null uses the default location.</summary>
    private readonly string? _sessionPath;

    public WmDaemon(string? sessionPath = null)
    {
        _sessionPath = sessionPath;
        _wm = new WindowManager();
        _executor = new CommandExecutor(_wm);
    }

    public WindowManager Manager => _wm;

    /// <summary>Starts the daemon and pumps messages until <see cref="Stop"/>.</summary>
    public void Run(string? configPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Startup is eight distinct pieces of work and used to report none of them.
        // When it took eleven seconds, the log said only that adoption had finished
        // and, much later, that the work area had settled - with no way to tell which
        // of the six things between them was responsible.
        long phase = Stopwatch.GetTimestamp();

        // Before any window or monitor is touched: without it Windows reports
        // virtualised coordinates on scaled displays and every computed rectangle
        // lands in the wrong place.
        MonitorSource.EnableDpiAwareness();
        phase = ReportPhase("dpi awareness", phase);

        _configPath = configPath;
        LoadConfig(configPath, initial: true);
        phase = ReportPhase("config", phase);

        SyncMonitors();
        phase = ReportPhase("monitors", phase);

        AdoptExistingWindows();
        phase = ReportPhase("adopting windows", phase);

        _winEvents = new WinEventSource { WorkQueued = _loop.Wake };
        _winEvents.Start();
        phase = ReportPhase("window event hooks", phase);

        _keyboard = new KeyboardSource { WorkQueued = _loop.Wake };
        _keyboard.Start(_bindings.IsBound);
        phase = ReportPhase("keyboard hook", phase);

        _ipc = new IpcServer { Warn = message => Log.Warn(LogCategory.Ipc, message) };
        _ipc.Start(new WmDaemonIpc(this).HandleAsync);
        phase = ReportPhase("ipc server", phase);

        RunStartupCommands();
        phase = ReportPhase("startup commands", phase);

        SettleWorkArea();
        _ = ReportPhase("settling the work area", phase);

        _layoutDirty = true;
        _loop.Tick += OnTick;
        _loop.NextTimeout = NextTimeout;

        Log.Info(LogCategory.Wm, $"started in {Since(_startedTicks):F0} ms: " +
            $"{_windows.ManagedCount} windows adopted, " +
            $"{_wm.Root.Monitors.Count} monitors, {_config.Keybindings.Count} keybindings, " +
            $"{_config.Rules.Count} rules");

        _loop.Run(TimeSpan.FromMilliseconds(8));

        // Announced first, before the work below, and deliberately so. Publishing only
        // queues the message onto each client's outbox for its writer task to send, and
        // the server does not flush on the way out - so the more real work that happens
        // between saying this and tearing the pipe down, the likelier it is to arrive.
        // Saving the session and un-concealing take tens of milliseconds, which is the
        // margin.
        //
        // Still best-effort. A bar has to cope with the pipe simply going away too,
        // because a kill gives no warning at all; this makes the ordinary case prompt
        // rather than making it certain.
        _ipc?.Publish(IpcProtocol.ShutdownTopic, "{}");

        // A clean shutdown is the one chance to record the arrangement exactly as
        // the user left it, rather than as it was up to thirty seconds earlier.
        if (_windows.ManagedCount > 0) SessionStore.Save(_wm.Root, _sessionPath, focusedMonitor: _wm.FocusedMonitor);

        RestoreConcealedWindows();

        // Log writing is buffered onto its own thread, so the last lines - the ones
        // that say why we are stopping - are still in the queue at this point.
        Log.Flush();
    }

    /// <summary>
    /// Brings every concealed window back before exiting.
    /// </summary>
    /// <remarks>
    /// Without this, every window on an inactive workspace is left off screen when
    /// Shubbak stops - process still running, nothing in Alt+Tab or the taskbar, and
    /// no way for the user to reach it. Cloaking makes that recoverable on the next
    /// run, but leaving the desktop as it was found is the correct behaviour.
    /// </remarks>
    private void RestoreConcealedWindows()
    {
        int restored = _committer.RestoreAll();

        if (restored > 0)
            Log.Info(LogCategory.Window, $"restored {restored} concealed window(s) on shutdown");
    }

    public void Stop() => _loop.Stop();

    /// <summary>Milliseconds since a <see cref="Stopwatch"/> timestamp.</summary>
    private static double Since(long ticks) =>
        (Stopwatch.GetTimestamp() - ticks) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Logs how long one startup phase took, and returns the timestamp for the next.
    /// </summary>
    /// <remarks>
    /// At debug rather than trace: this is the first thing to look at when someone
    /// says starting up is slow, and asking them to reproduce at trace level - which
    /// also logs every window it considers - is asking them to find one number in
    /// several hundred lines.
    /// </remarks>
    private static long ReportPhase(string name, long since)
    {
        double elapsed = Since(since);

        // Only the slow ones. Six lines saying "0 ms" on every start would bury the
        // one line that matters on the start where something goes wrong.
        if (elapsed >= 50) Log.Debug(LogCategory.Wm, $"startup: {name} took {elapsed:F0} ms");

        return Stopwatch.GetTimestamp();
    }

    // ---- the tick ----------------------------------------------------------

    /// <summary>
    /// How long the pump may wait before the next pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While something is moving, a frame interval - and the finer system timer, which
    /// is what makes a frame interval mean anything. Every wait with a timeout is
    /// quantised by that timer, so without raising it a request to wake in seven
    /// milliseconds returns in fifteen exactly as a sleep did, and the animation runs
    /// at half the rate it was designed for however the waiting is done.
    /// </para>
    /// <para>
    /// Otherwise a long wait, released the moment anything queues work. Periodic
    /// housekeeping - monitors, the focus border, the session - is on the order of
    /// seconds, so waking eight times a second to find nothing to do was the loop's
    /// own invention rather than anything the work required.
    /// </para>
    /// </remarks>
    private TimeSpan NextTimeout()
    {
        // The timer follows motion, and nothing else. It is a process-wide setting
        // that defeats timer coalescing and the deeper idle states, so it is held for
        // the thing it was raised for - animation frames landing when they are due -
        // and not for a pending layout pass, which is one pass rather than a sequence
        // of them and does not care whether it starts seven or fifteen milliseconds
        // from now.
        //
        // _layoutDirty is set by almost everything, so the timer was in practice raised
        // whenever anything happened at all. With `animation { enabled #false }` in the
        // config it was still raised on every dirty tick - for a feature the user had
        // switched off. It is now never raised in that case, which is what the comment
        // on TimerResolution has always claimed.
        if (_animation.IsAnimating)
        {
            _timerResolution.Acquire();

            // Only what is left of the current frame, not a fresh one. The pump is
            // woken by keyboard and window events as well as by its own timeout - and
            // during a workspace switch, by a storm of cloak and uncloak events - so
            // asking for a full interval after each of those pushed the frame out by
            // up to a whole interval every time one arrived. Measured: 14.49 ms
            // between frames against the 11.11 asked for, with 30-40% of each motion's
            // frames never delivered.
            double sinceFrameMs = _lastFrameTicks == 0
                ? 0
                : (Stopwatch.GetTimestamp() - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;

            return RemainingUntilFrame(sinceFrameMs, FrameInterval.TotalMilliseconds);
        }

        _timerResolution.Release();

        // The wait is the other question, and has a different answer: a pending pass
        // does want to run promptly.
        return _layoutDirty ? FrameInterval : IdleInterval;
    }

    /// <summary>
    /// How long to wait before the next animation frame is due.
    /// </summary>
    /// <param name="sinceLastFrameMs">Time since the last committed frame.</param>
    /// <param name="frameMs">One frame, in milliseconds.</param>
    /// <remarks>
    /// <para>
    /// Never negative. A frame already overdue asks for no wait at all, which the pump
    /// treats as "do not wait" - the tick that follows finds the frame due, commits it
    /// and restarts the clock, so this can return zero at most once per frame rather
    /// than spinning.
    /// </para>
    /// <para>
    /// That safety depends on agreeing with <see cref="IsFrameDue"/>: if this returned
    /// zero while that still refused the frame, the loop would zero-wait in a circle
    /// and burn a core. They are held together by a test rather than by hoping.
    /// </para>
    /// </remarks>
    internal static TimeSpan RemainingUntilFrame(double sinceLastFrameMs, double frameMs) =>
        TimeSpan.FromMilliseconds(Math.Max(0, frameMs - sinceLastFrameMs));

    /// <summary>
    /// How long one animation frame lasts, from configuration.
    /// </summary>
    /// <remarks>
    /// Was a fixed 7 ms - "roughly 144 Hz, the rate ADR 0001 gates the animation path
    /// on" - which conflated the rate the design was <i>proved sound at</i> with the
    /// rate it should <i>ask for</i>. The first measurement of the shipping binary
    /// found it delivering about 100 Hz regardless, and the frames it did deliver were
    /// arriving faster than the applications being moved could repaint.
    /// </remarks>
    private TimeSpan FrameInterval => _config.Animation.FramePeriod;

    /// <summary>
    /// The longest the loop sits idle before looking around on its own.
    /// </summary>
    /// <remarks>
    /// Not infinite. Everything that changes the tree signals the pump, but a monitor
    /// being unplugged does not, and neither does a window that resized itself - so
    /// there has to be a floor under how stale the world can get.
    /// </remarks>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimerResolution _timerResolution = new(1);

    /// <summary>
    /// How far an animation may be advanced by a single tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tick measures the real gap since the previous pass and hands it to the
    /// animation engine, which adds it to the elapsed time of every track. That is
    /// right for a track already in flight and wrong for one created on the same tick,
    /// because the gap says nothing about how long <i>that</i> animation has been
    /// running - it has been running for none of it.
    /// </para>
    /// <para>
    /// The idle wait is a quarter of a second, and rightly so. But a layout pass runs
    /// before the animation is advanced, so the order within one tick was: wait up to
    /// 250 ms, create a track with zero elapsed, then add up to 250 ms to it. A
    /// window-move animation is 140 ms by default, so it completed on its first frame
    /// and the window teleported.
    /// </para>
    /// <para>
    /// That is why the animations looked unreliable rather than absent: it needed the
    /// tick that starts the animation to follow a long wait, which means the first
    /// action after the desktop has been idle, and almost never during a burst of
    /// activity. "It animates while I'm working and not when I come back to it" is an
    /// accurate description and points nowhere near a delta.
    /// </para>
    /// <para>
    /// Two frames, so a single missed one is still caught up and anything longer
    /// stretches the animation in wall-clock time rather than collapsing it. Slower is
    /// invisible; instant is the bug.
    /// </para>
    /// </remarks>
    /// <param name="deltaMs">Elapsed time the tick wants to hand to the engine.</param>
    /// <param name="frameMs">
    /// One frame, in milliseconds. Passed rather than read from configuration so the
    /// rule holds at whatever rate the frame clock is running at, and so a test can
    /// state the rate it is asserting about instead of inheriting today's default.
    /// </param>
    internal static double ClampAnimationStep(double deltaMs, double frameMs) =>
        Math.Min(deltaMs, frameMs * 2);

    private void OnTick()
    {
        // A thread-local read, not a collection or a walk. Taken here and again in
        // the finally so the figure covers the whole tick.
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool reported = false;

        try
        {
            long now = Stopwatch.GetTimestamp();
            double deltaMs = _lastTickTicks == 0
                ? 0
                : (now - _lastTickTicks) * 1000.0 / Stopwatch.Frequency;
            _lastTickTicks = now;

            if (deltaMs > 0) _tickInterval.Record(deltaMs);

            // Read before anything can start or finish a motion, so a motion that both
            // begins and ends inside this tick is still recognised as one.
            bool wasAnimating = _animation.IsAnimating;

            DrainKeyboard();
            DrainWindowEvents();
            DrainInbox();

            // Paused means the daemon keeps its hands off the desktop. The flag is left
            // set, so everything that accumulated while paused is applied in a single
            // pass on resuming rather than being lost.
            if (_layoutDirty && !_wm.IsPaused)
            {
                // Cleared only once the pass has finished. Clearing first meant a
                // throw left the desktop in whatever half-applied state the exception
                // produced, with nothing to retry it until some unrelated event
                // happened to set the flag again - and the windows already taken out
                // of the arriving set animated on that eventual retry, which is the
                // stutter that set is there to avoid.
                ApplyLayout();
                _layoutDirty = false;
            }

            // Counted here rather than in Retarget: a layout pass retargets many
            // windows at once and that is one motion as far as the report is concerned.
            bool started = !wasAnimating && _animation.IsAnimating;
            if (started) _animationsStarted++;

            if (_animation.IsAnimating) MaybeAdvanceFrame(now);

            // Reported once per motion, not once per frame - an instrument that logs
            // at frame rate becomes the thing it is measuring.
            //
            // "started" is in the condition for the motion short enough to begin and
            // finish inside one tick, which never satisfies "was animating when the
            // tick opened" and so left its frame count to be added to whatever the
            // next motion reported.
            if ((wasAnimating || started) && !_animation.IsAnimating)
            {
                // Cleared so the next motion's first frame is emitted immediately
                // rather than waiting out a frame interval measured from the previous
                // motion, which could have ended minutes ago.
                _lastFrameTicks = 0;
                reported = ReportMotionEnded();
            }

            MaybeSyncMonitors(now);
            MaybeRefreshFocusBorder(now);
            MaybeSaveSession(now);
        }
        catch (Exception ex)
        {
            // A daemon that dies leaves every managed window stranded, so the tick
            // never propagates. Anything unexpected is logged and the loop carries on.
            Log.Error(LogCategory.Wm, "tick failed", ex);
        }
        finally
        {
            _tickDuration.Record(
                (Stopwatch.GetTimestamp() - _lastTickTicks) * 1000.0 / Stopwatch.Frequency);

            // Skipped on the tick that logged: see ReportMotionEnded.
            if (!reported)
                _tickAllocation.Record(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
    }

    /// <summary>
    /// Emits an animation frame, but no faster than <see cref="FrameInterval"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loop's wait is <c>MsgWaitForMultipleObjectsEx</c> with <c>QS_ALLINPUT</c>,
    /// so it returns on any queue activity and the interval it asks for is an upper
    /// bound, never a pace. Until the commit call stopped blocking, nothing noticed:
    /// <c>EndDeferWindowPos</c> waited on each target window's thread and that wait
    /// was, by accident, the frame clock.
    /// </para>
    /// <para>
    /// Removing the block removed the clock. The first run without it emitted frames
    /// at a p50 of 0.81 ms - about 1230 Hz against the 143 Hz asked for, roughly two
    /// and a half times as many frames as a motion should contain. Every one of those
    /// carries <c>SWP_NOCOPYBITS</c> and so tells the target to discard its client
    /// area and repaint. No application can do that a thousand times a second, and the
    /// visible result was a window whose geometry kept up while its content did not:
    /// bare grey where the content should be.
    /// </para>
    /// <para>
    /// So the pace is now explicit rather than a side effect of blocking on other
    /// people's message loops. The loop may still wake as often as it likes - it has
    /// keyboard and window events to service - but a frame is emitted only when one is
    /// actually due.
    /// </para>
    /// </remarks>
    private void MaybeAdvanceFrame(long now)
    {
        double frameMs = FrameInterval.TotalMilliseconds;

        // A first frame has no previous frame to be spaced from, and is emitted at
        // once: the motion has just been retargeted and the windows are still at their
        // old positions.
        bool first = _lastFrameTicks == 0;

        double sinceFrameMs = first
            ? 0
            : (now - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;

        if (!first && !IsFrameDue(sinceFrameMs, frameMs)) return;

        if (!first)
        {
            // Recorded here rather than per tick, so this is the interval between
            // frames that were actually committed - which is what a frame rate means.
            _frameInterval.Record(sinceFrameMs);
            _animatingMs += sinceFrameMs;
        }

        AdvanceAnimation(ClampAnimationStep(sinceFrameMs, frameMs));

        _lastFrameTicks = now;
    }

    /// <summary>When the last animation frame was committed. Zero between motions.</summary>
    private long _lastFrameTicks;

    /// <summary>
    /// Whether enough time has passed since the last frame to commit another.
    /// </summary>
    /// <remarks>
    /// A floor on the interval, not a ceiling. The pump's wait is only ever an upper
    /// bound - it returns on any queue activity - so without this the frame rate is
    /// whatever the message traffic happens to be, which measured 1230 Hz against a
    /// 143 Hz target and flooded every window being moved with more repaint requests
    /// than it could serve.
    /// </remarks>
    /// <param name="sinceLastFrameMs">Time since the last committed frame.</param>
    /// <param name="frameMs">One frame, in milliseconds.</param>
    /// <remarks>
    /// <para>
    /// Allows a wake that lands up to <see cref="FrameSlackMs"/> early to count. The
    /// pump's timeout is whole milliseconds and a frame interval usually is not, and
    /// the pump is also woken by keyboard and window events that have nothing to do
    /// with the frame clock - during a workspace switch, constantly. Without the
    /// slack, a wake a fraction of a millisecond short is refused and the frame waits
    /// out another entire interval, which halves the rate rather than trimming it.
    /// </para>
    /// <para>
    /// Emitting a frame a fraction early costs a fraction of a frame and corrects
    /// itself on the next one, because the clock measures from the frame actually
    /// committed rather than from a fixed schedule.
    /// </para>
    /// </remarks>
    internal static bool IsFrameDue(double sinceLastFrameMs, double frameMs) =>
        sinceLastFrameMs >= frameMs - FrameSlackMs;

    /// <summary>
    /// How early a wake may be and still count as a frame.
    /// </summary>
    /// <remarks>
    /// One millisecond, which is the resolution the pump can express a timeout in and
    /// the resolution <c>TimerResolution</c> asks the system scheduler for while
    /// anything is animating. Asking for finer than the clock beneath it can deliver
    /// is how the rate got halved in the first place.
    /// </remarks>
    private const double FrameSlackMs = 1.0;

    /// <remarks>
    /// <para>
    /// <see cref="LogCategory.Animation"/> is declared, named in the category guidance
    /// as where to look when motion stutters, and until now written to by nothing.
    /// </para>
    /// <para>
    /// Once per motion, not once per frame. At 144 Hz a per-frame line is 144 strings
    /// and 144 ring writes a second for as long as anything moves, which is an
    /// instrument that becomes the thing it is measuring.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Whether anything was logged, so the caller can drop that tick's allocation
    /// sample. This is the one place in the tick that is *expected* to allocate - it
    /// builds a string - and a measurement of allocation that includes the allocation
    /// performed by the measurement answers a question nobody asked. One dropped
    /// sample per motion, out of a ring of four thousand, costs nothing.
    /// </returns>
    private bool ReportMotionEnded()
    {
        double animatingMs = _animatingMs;
        long delivered = _framesDelivered;

        // Reset first and unconditionally. Behind the level check these grew across
        // every motion of the session, so "delivered against due" stopped describing
        // a motion and started describing the whole run - and _animatingMs never
        // stopped growing.
        _animatingMs = 0;
        _framesDelivered = 0;

        if (!Log.IsEnabled(LogLevel.Debug)) return false;

        // Plus one because N frames span N-1 gaps, and the accumulated time is the
        // span from the first committed frame to the last. Without the correction
        // every motion reported a surplus of exactly one frame, which would have made
        // the ratio useless for spotting the dropped frames it exists to spot.
        //
        // Now that the frame clock enforces a floor on the interval, a surplus is no
        // longer possible and a deficit means frames were genuinely late.
        double due = FrameInterval.TotalMilliseconds > 0
            ? animatingMs / FrameInterval.TotalMilliseconds + 1
            : 0;

        Log.Debug(LogCategory.Animation,
            $"motion ended: {delivered} frames delivered of {due:F0} due " +
            $"over {animatingMs:F0} ms");

        return true;
    }

    private void DrainKeyboard()
    {
        if (_keyboard is null) return;

        int count = _keyboard.Drain(_keyScratch, _keyScratch.Length);

        for (int i = 0; i < count; i++)
        {
            KeyEvent key = _keyScratch[i];
            if (!key.IsKeyDown) continue;

            Keybinding? binding = _bindings.Resolve(key.VirtualKey, key.Modifiers);

            if (binding is null)
            {
                // Reaching here means the hook claimed the keystroke but nothing
                // resolved it - almost always a non-pass-through binding mode
                // swallowing keys, which is worth being able to see.
                if (Log.IsEnabled(LogLevel.Trace))
                    Log.Trace(LogCategory.Hook,
                        $"swallowed vk=0x{key.VirtualKey:X2} mods={key.Modifiers} (no binding)");

                continue;
            }

            if (Log.IsEnabled(LogLevel.Debug))
                Log.Debug(LogCategory.Hook, $"{binding.Key.Display} -> {binding.Commands.Describe()}");

            // Auto-repeat. Windows sends repeated key-downs with no release between,
            // and every one of them used to be executed: holding the close key closed
            // everything on the workspace, and holding a shell-exec key started a
            // process per repeat.
            //
            // Skipped after the resolve rather than before it, so the trace above still
            // shows the key being held - which is the one thing that makes "why did
            // that happen thirty times" answerable.
            if (key.IsRepeat && !binding.RepeatsOnHold)
            {
                if (Log.IsEnabled(LogLevel.Trace))
                    Log.Trace(LogCategory.Hook, $"{binding.Key.Display}: ignoring auto-repeat");

                continue;
            }

            Execute(binding.Commands);
        }
    }

    private void DrainWindowEvents()
    {
        if (_winEvents is null) return;

        int count = _winEvents.Drain(_eventScratch, _eventScratch.Length);

        for (int i = 0; i < count; i++)
            HandleWindowEvent(_eventScratch[i]);
    }

    /// <summary>Runs work queued by IPC threads, on the daemon thread.</summary>
    private void DrainInbox()
    {
        while (true)
        {
            Action? work;

            lock (_inboxGate)
            {
                if (_inbox.Count == 0) return;
                work = _inbox.Dequeue();
            }

            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.Ipc, "request handler failed", ex);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the daemon thread and awaits its result.
    /// </summary>
    internal Task<T> InvokeAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_inboxGate)
        {
            _inbox.Enqueue(() =>
            {
                try { completion.SetResult(work()); }
                catch (Exception ex) { completion.SetException(ex); }
            });
        }

        // The pump waits rather than polls, so a request arriving from a pipe thread
        // has to say so or it sits in the inbox until some unrelated timeout expires.
        _loop.Wake();

        return completion.Task;
    }

    /// <summary>
    /// Decides whether a hidden window has actually gone away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applications that close to the system tray hide their window and keep running,
    /// so it is still a valid handle. Keeping it managed means the next layout reveals
    /// it, the window reappears, and the close button looks broken - which is exactly
    /// what WhatsApp does.
    /// </para>
    /// <para>
    /// The trap is that <c>EVENT_OBJECT_HIDE</c> also arrives when a window is
    /// minimised or destroyed, and komorebi's handler carries a comment saying so.
    /// Unmanaging on every hide would therefore drop a window the moment the user
    /// minimised it. Minimising is checked first for that reason: it has its own event
    /// and its own state, and this must not usurp either.
    /// </para>
    /// <para>
    /// komorebi is more cautious still - it unmanages only for applications on a
    /// curated tray list. That is more accurate and costs the user a config entry per
    /// application. Treating any foreign hide as a departure is the opposite trade:
    /// nothing to configure, and a window that hides itself transiently is re-managed
    /// on the show event that follows.
    /// </para>
    /// </remarks>
    private void HandleWindowHidden(nint handle)
    {
        // Ours. The workspace it lives on was switched away from.
        if (_committer.IsConcealing(handle)) return;

        // Gone for good.
        if (!Win32Window.Exists(handle))
        {
            TryUnmanage(handle);
            return;
        }

        // Minimised, not dismissed. Windows sends this alongside the minimise event,
        // and a minimised window is still the user's - it keeps its place in the tree
        // and its slot in the layout.
        if (Win32Window.IsMinimised(handle)) return;

        TryUnmanage(handle);
    }

    private void HandleWindowEvent(WinEventNotification notification)
    {
        nint handle = notification.Handle;

        // Paused suspends window management: nothing is adopted, released, focused or
        // re-arranged while the user has asked Shubbak to leave the desktop alone.
        //
        // Destruction is the exception, because it is bookkeeping rather than
        // management. A window that has closed is gone whether we are paused or not,
        // and the event saying so arrives exactly once - dropping it would leave a dead
        // node in the tree with nothing left to reap it.
        //
        // Keybindings deliberately keep working. The command that resumes is one of
        // them, and a pause that cannot be undone from the keyboard is a trap.
        if (_wm.IsPaused && notification.Kind != WinEventKind.Destroyed) return;

        // LOCATIONCHANGE used to be excluded here by name: S4 measured 122 of them per
        // second from a single dragged window, and logging them would drown everything
        // else while slowing the very thing being diagnosed. It is no longer subscribed
        // to at all, so there is nothing left to exclude.
        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.Trace(LogCategory.Window,
                $"{notification.Kind} 0x{handle:X} \"{Win32Window.GetTitle(handle).Truncate(48)}\"");
        }

        switch (notification.Kind)
        {
            case WinEventKind.Created:
            case WinEventKind.Shown:
            case WinEventKind.Uncloaked:
                // Coming out of the tray is the evidence that was missing at startup,
                // so a window set aside then gets another look now.
                _windows.NoLongerSetAside(handle);
                TryManage(handle);
                break;

            case WinEventKind.Destroyed:
                TryUnmanage(handle);
                break;

            case WinEventKind.Cloaked:
                // Shubbak conceals inactive workspaces by cloaking, and the shell
                // reports that straight back as an ordinary cloak event. Unmanaging on
                // it would drop every window on the workspace just left, so switching
                // back would find nothing to reveal and the windows would be stranded.
                //
                // The Hidden case below has carried the equivalent guard for a long
                // time. This one did not need it while cloaking silently failed for
                // every window Shubbak managed; now that it works, it does.
                if (!_committer.IsConcealing(handle)) TryUnmanage(handle);
                break;

            case WinEventKind.Hidden:
                HandleWindowHidden(handle);
                break;

            case WinEventKind.TitleChanged:
                if (_windows.TryGet(handle, out WindowNode? titled))
                {
                    Publish(_wm.UpdateTitle(titled, Win32Window.GetTitle(handle)));

                    // on="title-change" rules parsed and were then dispatched from
                    // nowhere, so a rule written against a title that only appears
                    // once the application has loaded - a document name, a call in
                    // progress - silently never ran.
                    if (_rules.HasRulesFor(RuleTrigger.OnTitleChange))
                        ApplyRules(titled, ToAttributes(handle), RuleTrigger.OnTitleChange);
                    break;
                }

                // Not managed yet - so this may be the moment it becomes eligible.
                // Store apps and other late-loading windows are created before they
                // have a title, and an untitled top-level window is rejected: they
                // are overwhelmingly splash screens and invisible helpers.
                //
                // Without this, such a window is judged once, at its least ready, and
                // never looked at again. Settings opened with Win+I stayed unmanaged
                // until it was closed and reopened - by which time the window already
                // existed and passed on the first try, which is what made it look
                // like an intermittent fault rather than a race.
                //
                // Minimised windows are excluded, which is komorebi's lesson rather
                // than ours: they hit a case where Firefox renamed a minimised window
                // as YouTube autoplayed, and treating that as an arrival pulled the
                // window back onto the screen.
                if (!Win32Window.IsMinimised(handle)) TryManage(handle);
                break;

            case WinEventKind.Foreground:
                if (_windows.TryGet(handle, out WindowNode? focused))
                {
                    if (!ReferenceEquals(_wm.FocusedWindow, focused))
                    {
                        Publish(_wm.FocusWindow(focused));

                        // on="focus" rules, likewise dispatched from nowhere until now.
                        if (_rules.HasRulesFor(RuleTrigger.OnFocus))
                            ApplyRules(focused, ToAttributes(handle), RuleTrigger.OnFocus);
                    }
                }
                else
                {
                    TryManage(handle);
                }
                break;

            case WinEventKind.MinimiseStart:
                if (_windows.TryGet(handle, out WindowNode? minimising))
                    Publish(_wm.SetWindowState(minimising, WindowState.Minimised));
                break;

            case WinEventKind.MinimiseEnd:
                if (_windows.TryGet(handle, out WindowNode? restoring))
                    Publish(_wm.SetWindowState(restoring, WindowState.Tiling));
                break;

            case WinEventKind.MoveSizeStart:
                // The starting geometry is what distinguishes a move from a resize,
                // and a real drag from a click on a title bar.
                if (_windows.IsManaged(handle)) _dragOrigin[handle] = Win32Window.GetBounds(handle);
                break;

            case WinEventKind.MoveSizeEnd:
                HandleUserMove(handle);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Handles the user finishing a drag or resize with the mouse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three outcomes, decided by comparing the geometry against what it was when the
    /// drag started:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Floating</b> - the new geometry is simply adopted.</item>
    ///   <item><b>Resized</b> - the new size is converted back into the tree's size
    ///   ratios, so dragging a border behaves as it does in any tiling manager.</item>
    ///   <item><b>Moved</b> - resolved against the tree: dropping on the middle of
    ///   another window swaps them, dropping near an edge inserts beside it.</item>
    /// </list>
    /// <para>
    /// A drag that resolves to nothing simply relayouts, putting the window back -
    /// which is honest, because the alternative is guessing.
    /// </para>
    /// </remarks>
    private void HandleUserMove(nint handle)
    {
        if (!_windows.TryGet(handle, out WindowNode? window)) return;

        Rect before = _dragOrigin.TryGetValue(handle, out Rect recorded) ? recorded : window.Rect;
        _dragOrigin.Remove(handle);

        Rect after = Win32Window.GetBounds(handle);

        if (window.State == WindowState.Floating)
        {
            // Recorded as a visible frame, because that is what a layout rectangle is
            // and FloatingRect is fed straight back to the layout. GetWindowRect
            // includes the shadow, and the commit path adds the shadow on the way out,
            // so storing the raw value grew the window by its own shadow - about
            // fourteen pixels of width - every single time it was dragged.
            window.FloatingRect = WindowCommitter.VisibleBounds(handle);

            // Forgotten so the recorded position is what the next pass compares
            // against; without it the skip check would still be holding the rectangle
            // Shubbak last chose rather than the one the user just made.
            _committer.Forget(handle);
            return;
        }

        if (!window.IsTiled)
        {
            _layoutDirty = true;
            return;
        }

        if (DragResolver.IsResize(before, after))
        {
            Publish(_wm.ResizeFromDrag(window, after));
            _layoutDirty = true;
            return;
        }

        if (!DragResolver.IsMove(before, after))
        {
            // A click on a title bar, not a drag. Nothing to do beyond letting the
            // next layout pass put the window back where it belongs.
            _layoutDirty = true;
            return;
        }

        // The cursor, not the window's corner: the user grabbed the title bar at an
        // arbitrary offset, so the window's top-left may be far from where they are
        // pointing.
        if (Win32Window.GetCursorPosition() is not { } cursor)
        {
            _layoutDirty = true;
            return;
        }

        WmResult result = _wm.DropWindow(window, cursor.X, cursor.Y);
        Publish(result);

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.Debug(LogCategory.Window,
                result.Succeeded
                    ? $"dropped \"{window.Identity.Title.Truncate(32)}\" at {cursor.X},{cursor.Y}"
                    : $"drop rejected: {result.RejectionReason}");
        }

        _layoutDirty = true;
    }

    // ---- window lifecycle --------------------------------------------------

    private void TryManage(nint handle, bool forced = false)
    {
        // Already ours, whoever asked. Everything below assumes the window is not in
        // the tree yet.
        if (_windows.IsManaged(handle)) return;

        // A verdict already reached - refused by a rule, or set aside at startup - is
        // not revisited on every one of the many events a window raises. Asking by
        // hand overrules it, which is the whole point of toggle-managed.
        if (!forced && _windows.AlreadyDecided(handle)) return;

        if (!PassesFilter(handle, forced)) return;

        WindowAttributes attributes = ToAttributes(handle);

        if (!forced && _rules.ShouldIgnore(attributes))
        {
            // Remembered, so the same window is not re-evaluated on every one of the
            // many events it will generate over its lifetime.
            _windows.Exclude(handle);

            Log.Debug(LogCategory.Rule,
                $"ignoring 0x{handle:X} \"{attributes.Title.Truncate(40)}\" ({attributes.ProcessName})");

            return;
        }

        WindowNode window = BuildNode(handle);

        // A saved session wins during the initial adoption pass, so a restart puts
        // windows back where they were rather than piling them onto whichever
        // workspace happens to be active.
        WorkspaceNode? remembered = _restoring ? RestoredWorkspaceFor(window) : null;

        if (!ClaimIfConcealed(handle, window, remembered)) return;

        WorkspaceNode? workspace = remembered ?? WorkspaceFor(handle);

        // The state detected above, and the one the session remembered, are passed
        // through rather than left on the node: adoption used to overwrite whatever
        // was there with the configured default.
        WindowState detected = window.State;

        WmResult adoption = _wm.ManageWindow(window, workspace, detected);

        if (!adoption.Succeeded)
        {
            // Not in the tree, so no placement is ever computed for it, and the guard
            // at the top of this method means we never look at it again. Recording it
            // as managed would leave it on screen at its original position for the
            // life of the process, on top of everything the layout does control.
            Log.Warn(LogCategory.Window,
                $"not managing 0x{handle:X} \"{attributes.Title.Truncate(40)}\": " +
                $"{adoption.RejectionReason ?? "no workspace available"}");

            _windows.Exclude(handle);
            return;
        }

        _windows.Adopt(handle, window);
        Publish(adoption);

        Log.Info(LogCategory.Window,
            $"managed 0x{handle:X} \"{attributes.Title.Truncate(40)}\" " +
            $"({attributes.ProcessName}) [{attributes.ClassName}] {window.State} " +
            $"-> workspace {window.Workspace?.Name ?? "?"}");

        ApplyRules(window, attributes, RuleTrigger.OnManage);

        _layoutDirty = true;
    }

    /// <summary>
    /// Whether the built-in filter, and any rule allowed to overrule it, let this
    /// window through.
    /// </summary>
    private bool PassesFilter(nint handle, bool forced)
    {
        // Concealed windows are considered only during the initial adoption pass, and
        // even then only to be reconciled against the session below.
        ManageDecision decision = WindowFilter.Evaluate(handle, concealedAreEligible: _restoring);

        if (decision.Manageable) return true;

        if (forced)
        {
            // Asked for by hand. Only the rules that keep the desktop itself out are
            // absolute; everything else is a heuristic the user is entitled to overrule.
            if (WindowFilter.CanBeOverridden(decision.Reason)) return true;

            Log.Warn(LogCategory.Window, $"refusing to manage 0x{handle:X}: {decision.Explain()}");
            return false;
        }

        // Attributes are built here rather than at the top of adoption because reading
        // them costs a process handle, and the overwhelming majority of windows are
        // rejected without ever needing them. Paying that only for the rejections a
        // rule is allowed to overturn keeps the common path as cheap as it was.
        if (WindowFilter.CanBeOverridden(decision.Reason) && _rules.ShouldForceManage(ToAttributes(handle)))
        {
            Log.Debug(LogCategory.Rule,
                $"managing 0x{handle:X} \"{Win32Window.GetTitle(handle).Truncate(40)}\" " +
                $"despite {decision.Explain()}: a rule asked for it");

            return true;
        }

        // At trace level this is the answer to "why is that window floating?", recorded
        // as it happens rather than reconstructed afterwards. The class is included
        // because it is what a rule has to match on, and transient windows - shell
        // flyouts especially - are gone long before anything can be pointed at them.
        if (Log.IsEnabled(LogLevel.Trace))
        {
            Log.Trace(LogCategory.Window,
                $"skip 0x{handle:X} \"{Win32Window.GetTitle(handle).Truncate(40)}\" " +
                $"[{Win32Window.GetClassName(handle)}]: {decision.Explain()}");
        }

        return false;
    }

    /// <summary>Builds the tree node for a window about to be adopted.</summary>
    private WindowNode BuildNode(nint handle)
    {
        var window = new WindowNode(handle, Win32Window.BuildIdentity(handle))
        {
            State = WindowFilter.InitialStateFor(handle, _config.InitialWindowState),
        };

        // A window that starts floating has no remembered position, and the layout
        // engine treats a floating window's rectangle as the user's to keep - so
        // without this it would be "kept" at the origin with no size, and the window
        // would be flung into the corner the instant it appeared.
        //
        // Dialogs are the case that matters: Win+R and Save boxes size themselves to
        // their content, which is precisely why they are not tiled.
        if (window.State == WindowState.Floating)
        {
            // Visible frame, matching what the layout means by a rectangle. See the
            // same conversion in HandleUserMove.
            Rect bounds = WindowCommitter.VisibleBounds(handle);
            if (!bounds.IsEmpty) window.FloatingRect = bounds;
        }

        return window;
    }

    /// <summary>
    /// Decides what becomes of a window that is still concealed as it is adopted.
    /// </summary>
    /// <returns>Whether adoption should continue.</returns>
    private bool ClaimIfConcealed(nint handle, WindowNode window, WorkspaceNode? remembered)
    {
        ConcealedVerdict verdict = JudgeConcealed(
            restoring: _restoring,
            concealed: _restoring && WindowCommitter.IsConcealed(handle),
            claimedBySession: remembered is not null,
            revived: _revived,
            revivalBudget: _revivalBudget);

        switch (verdict)
        {
            case ConcealedVerdict.LeaveAlone:
                _windows.SetAside(handle);

                if (Log.IsEnabled(LogLevel.Debug))
                {
                    Log.Debug(LogCategory.Window,
                        $"leaving concealed 0x{handle:X} \"{window.Identity.Title.Truncate(40)}\" " +
                        "alone for now: no session entry claims it");
                }

                return false;

            case ConcealedVerdict.TooManyRevived:
                Log.Error(LogCategory.Window,
                    $"refusing to revive 0x{handle:X}: already revived {_revived} window(s) " +
                    $"for a session of {_revivalBudget}. This is a bug - please report it.");

                _windows.Exclude(handle);
                return false;

            case ConcealedVerdict.Revive:
                // Claimed, but not revealed here. Whether this window belongs on screen
                // is the layout's decision, and the layout has not run yet - the first
                // pass is on the first tick, after the hooks are installed and the
                // startup commands have been shell-executed.
                //
                // Un-cloaking it now put every remembered window on the desktop at once,
                // stacked at whatever positions they had last run, until that first pass
                // hid the ones belonging to other workspaces again. Leaving it concealed
                // costs nothing: Show already reverses a concealment it did not perform,
                // which is the path this window takes the moment its workspace is shown.
                _revived++;

                Log.Info(LogCategory.Window,
                    $"claimed concealed 0x{handle:X} \"{window.Identity.Title.Truncate(40)}\" " +
                    $"-> workspace {remembered!.Name}");

                return true;

            case ConcealedVerdict.Adopt:
            default:
                return true;
        }
    }

    /// <summary>What becomes of a window that is concealed as adoption reaches it.</summary>
    internal enum ConcealedVerdict
    {
        /// <summary>Not concealed, or not a restore. Carry on.</summary>
        Adopt,

        /// <summary>Concealed and unclaimed: not ours to reveal, for now.</summary>
        LeaveAlone,

        /// <summary>Claimed, but by more windows than the session can account for.</summary>
        TooManyRevived,

        /// <summary>Claimed by the session, and within budget.</summary>
        Revive,
    }

    /// <summary>
    /// Whether a concealed window should be revived, set aside, or refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A window still concealed at this point was concealed by whoever ran last - us,
    /// before a crash or a kill. It is revived only when the session names it. Without
    /// that evidence it belongs to the application that hid it: a tray host, a
    /// message-only helper, a media-key listener. A desktop carries dozens.
    /// </para>
    /// <para>
    /// The claim is tested against the session match deliberately, and not against the
    /// resolved workspace. An earlier version checked the latter, which falls back to a
    /// real workspace and so is never null - the guard never fired, and startup
    /// revealed eighty-four background windows on an ordinary desktop.
    /// </para>
    /// <para>
    /// The budget exists not because the claim check is expected to fail, but because
    /// it already did once. The session cannot justify reviving more windows than it
    /// remembers, so exceeding that is proof of a logic error, and the damage is
    /// visible to the user immediately. Refusing costs a window that stays concealed;
    /// not refusing carpets the desktop.
    /// </para>
    /// </remarks>
    internal static ConcealedVerdict JudgeConcealed(
        bool restoring,
        bool concealed,
        bool claimedBySession,
        int revived,
        int revivalBudget)
    {
        if (!restoring || !concealed) return ConcealedVerdict.Adopt;
        if (!claimedBySession) return ConcealedVerdict.LeaveAlone;
        if (revived >= revivalBudget) return ConcealedVerdict.TooManyRevived;

        return ConcealedVerdict.Revive;
    }

    /// <summary>
    /// Lets go of a window.
    /// </summary>
    /// <param name="handle">The window.</param>
    /// <param name="thenExclude">
    /// Whether to refuse it afterwards, for a release the user or a rule asked for
    /// rather than one the window brought on itself by closing. Passed through rather
    /// than done by the caller because releasing forgets every verdict, so excluding
    /// first would be undone here - and the two callers that want it used to have to
    /// remember that for themselves.
    /// </param>
    private void TryUnmanage(nint handle, bool thenExclude = false)
    {
        if (_windows.Release(handle, thenExclude) is not { } window) return;

        if (ReferenceEquals(_borderedWindow, window)) _borderedWindow = null;

        // The border is drawn on the window's own frame, so letting go of the window
        // without clearing it leaves Shubbak's mark on something it no longer controls.
        // It stayed lit for the life of the window, which is precisely the opposite of
        // the signal it exists to give.
        if (_config.Effects.Enabled && Win32Window.Exists(handle))
            WindowActions.ClearBorderColour(handle);

        _committer.Forget(handle);
        _animation.Remove(window.Handle);
        _dragOrigin.Remove(handle);
        Publish(_wm.UnmanageWindow(window));

        Log.Info(LogCategory.Window,
            $"unmanaged 0x{handle:X} \"{window.Identity.Title.Truncate(40)}\"");

        _layoutDirty = true;
    }

    /// <summary>
    /// Builds a diagnostic report describing the live window manager.
    /// </summary>
    /// <remarks>
    /// Must be called on the daemon thread: it walks the tree.
    /// </remarks>
    internal string BuildDiagnosticReport(string reason)
    {
        var report = new DiagnosticReport(reason).AddEnvironment();

        report.AddSection("Window manager", string.Join('\n', new[]
        {
            $"- **Monitors**: {_wm.Root.Monitors.Count}",
            $"- **Workspaces**: {_wm.Root.AllWorkspaces().Count()}",
            $"- **Managed windows**: {_windows.ManagedCount}",
            $"- **Ignored windows**: {_windows.ExcludedCount}",
            $"- **Focused**: {_wm.FocusedWindow?.Identity.Title ?? "(none)"}",
            $"- **Binding mode**: {_wm.BindingMode ?? "(default)"}",
            $"- **Paused**: {_wm.IsPaused}",
            $"- **Animating**: {_animation.ActiveCount}",
            $"- **Keybindings**: {_config.Keybindings.Count}",
            $"- **Rules**: {_config.Rules.Count}",
            $"- **IPC clients**: {_ipc?.ClientCount ?? 0}",
        }));

        report.AddSection("Performance", string.Join('\n', new[]
        {
            $"- **Uptime**: {TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _startedTicks) / (double)Stopwatch.Frequency):hh\\:mm\\:ss}",
            $"- **Ticks**: {_tickInterval.Offered}",

            // The number that says whether the loop runs at the rate it asks for. It
            // asks for 8 ms; a plain sleep is rounded up to the system timer
            // resolution, so this is the only honest answer.
            //
            // Over the most recent samples rather than the whole run, and said so
            // explicitly: these once described the first four thousand ticks and then
            // never moved, so a daemon up for an hour was quoting its own startup.
            $"- **Tick interval** (last {_tickInterval.Count}): p50 {_tickInterval.Percentile(0.5):F2} ms, " +
            $"p99 {_tickInterval.Percentile(0.99):F2} ms, max {_tickInterval.Max:F2} ms all-time " +
            $"(~{(_tickInterval.Percentile(0.5) > 0 ? 1000.0 / _tickInterval.Percentile(0.5) : 0):F0} Hz)",

            $"- **Tick duration** (last {_tickDuration.Count}): p50 {_tickDuration.Percentile(0.5):F2} ms, " +
            $"p99 {_tickDuration.Percentile(0.99):F2} ms, max {_tickDuration.Max:F2} ms all-time",

            $"- **Ticks over 6.94 ms budget**: {_tickDuration.CountOver(6.94)} of the last {_tickDuration.Count}",
            $"- **Allocated per tick** (last {_tickAllocation.Count}): p50 {_tickAllocation.Percentile(0.5):F0} B, " +
            $"p99 {_tickAllocation.Percentile(0.99):F0} B, max {_tickAllocation.Max:F0} B all-time",
            $"- **Dropped keystrokes**: {_keyboard?.Dropped ?? 0}",
            $"- **Dropped log entries**: {Log.Dropped}",
            $"- **GC**: gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)}",
            $"- **Allocated**: {GC.GetTotalAllocatedBytes(precise: false) / (1024 * 1024)} MB",
        }));

        report.AddSection("Animation", DescribeAnimation());

        report.AddCodeSection("Window tree", TreeRenderer.Render(_wm.Root, _wm.FocusedWindow));

        if (_configPath is not null && File.Exists(_configPath))
        {
            try
            {
                report.AddCodeSection($"Config ({_configPath})", File.ReadAllText(_configPath), "kdl");
            }
            catch (IOException ex)
            {
                report.AddSection("Config", $"(could not be read: {ex.Message})");
            }
        }

        return report.AddRecentLog().AddFooter().ToString();
    }

    /// <summary>
    /// What the animation path actually delivered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the Performance section because the tick interval cannot answer
    /// any of it. The loop has two modes - 250 ms idle and 7 ms animating - so every
    /// percentile over the combined figure describes the ratio between them rather
    /// than either one. There was no configuration in which that line answered "did
    /// the last animation deliver its frames".
    /// </para>
    /// <para>
    /// Frames due is derived from the interval the loop asks for, not from the
    /// display. Until the interval comes from a refresh rate, "delivered against due"
    /// says whether the loop met its own target and nothing about whether that target
    /// matches the panel - which is why the line below says the interval is fixed.
    /// </para>
    /// </remarks>
    private string DescribeAnimation()
    {
        if (_frameInterval.Count == 0)
            return "Nothing has been animated yet, so there is nothing to report.";

        double p50 = _frameInterval.Percentile(0.5);

        return string.Join('\n', new[]
        {
            $"- **Enabled**: {_config.Animation.Enabled}" +
            $"{(_config.Animation.AnimateNewWindows ? ", including new windows" : "")}",

            $"- **Asking for**: {FrameInterval.TotalMilliseconds:F2} ms " +
            $"(~{(FrameInterval.TotalMilliseconds > 0 ? 1000.0 / FrameInterval.TotalMilliseconds : 0):F0} Hz), " +
            $"from `animation {{ fps {_config.Animation.FramesPerSecond} }}` - " +
            "not derived from any monitor's refresh rate",

            $"- **Frame interval** (last {_frameInterval.Count}, animating only): " +
            $"p50 {p50:F2} ms, p99 {_frameInterval.Percentile(0.99):F2} ms, " +
            $"max {_frameInterval.Max:F2} ms all-time " +
            $"(~{(p50 > 0 ? 1000.0 / p50 : 0):F0} Hz)",

            $"- **Motions**: {_animationsStarted}",

            // Whether the pump's own clock is doing what the frame rate assumes. The
            // default Windows timer granularity is 15.625 ms, which on its own would
            // hold any rate above about 64 Hz to a fraction of what it asked for -
            // entirely independently of the daemon's arithmetic. Reported so the two
            // explanations can be told apart rather than argued about.
            $"- **Fine timer held**: {_timerResolution.IsHeld}",

            $"- **Wake overshoot** (last {_loop.WakeOvershoot.Count}, timed-out waits only): " +
            $"p50 {_loop.WakeOvershoot.Percentile(0.5):F2} ms, " +
            $"p99 {_loop.WakeOvershoot.Percentile(0.99):F2} ms, " +
            $"max {_loop.WakeOvershoot.Max:F2} ms all-time",

            $"- **Waits**: {_loop.WaitsTimedOut} ran out, {_loop.WaitsInterrupted} cut short " +
            $"by a message or signal",

            $"- **Commit frame** (last {_commitFrameDuration.Count}): " +
            $"p50 {_commitFrameDuration.Percentile(0.5):F2} ms, " +
            $"p99 {_commitFrameDuration.Percentile(0.99):F2} ms, " +
            $"max {_commitFrameDuration.Max:F2} ms all-time",

            $"- **Windows per frame** (last {_frameBatchSize.Count}): " +
            $"p50 {_frameBatchSize.Percentile(0.5):F0}, " +
            $"p99 {_frameBatchSize.Percentile(0.99):F0}, " +
            $"max {_frameBatchSize.Max:F0} all-time",
        });
    }

    /// <summary>
    /// Places a new window on the workspace of the monitor it appeared on.
    /// </summary>
    /// <remarks>
    /// Using the window's own monitor rather than the focused one matters on
    /// multi-monitor setups: an application launched onto a secondary display should
    /// stay there rather than teleporting to wherever focus happened to be.
    /// </remarks>
    private WorkspaceNode? WorkspaceFor(nint handle)
    {
        MonitorInfo? info = MonitorSource.ForWindow(handle);
        if (info is null) return null;

        MonitorNode? monitor = _wm.Root.FindMonitor(info.Value.DeviceId);
        return monitor?.ActiveWorkspace;
    }

    /// <summary>
    /// The workspace a window should be restored to from the saved session.
    /// </summary>
    /// <remarks>
    /// Only consulted while adopting the windows that existed at startup. Applying
    /// it later would mean a window opened an hour into a session could be yanked
    /// onto a workspace the user was not looking at.
    /// </remarks>
    private WorkspaceNode? RestoredWorkspaceFor(WindowNode window)
    {
        if (_session is null) return null;

        RememberedWindow? remembered = SessionStore.Match(_session, window.Identity, _claimedSessionEntries);
        if (remembered is null) return null;

        WorkspaceNode? workspace = _wm.Root.FindWorkspace(remembered.Workspace);
        if (workspace is null) return null;

        foreach (string tag in remembered.Tags) window.AddTagForRestore(tag);
        window.IsSticky = remembered.Sticky;

        if (Enum.TryParse(remembered.State, out WindowState state) && state != WindowState.Minimised)
            window.State = state;

        Log.Debug(LogCategory.Window,
            $"restored \"{window.Identity.Title.Truncate(32)}\" to workspace {workspace.Name}");

        return workspace;
    }

    /// <summary>Brings windows that already exist under management at startup.</summary>
    private void AdoptExistingWindows()
    {
        _session = SessionStore.Load(_sessionPath);

        if (_session is not null)
        {
            Log.Info(LogCategory.Wm,
                $"session loaded: {_session.Windows.Count} remembered windows from {_session.SavedAt:g}");
        }

        _restoring = true;
        _revived = 0;
        _revivalBudget = _session?.Windows.Count ?? 0;

        try
        {
            foreach (nint handle in Win32Window.EnumerateTopLevel()) TryManage(handle);
        }
        finally
        {
            // Always reported, so a recovery that runs away is visible in the log
            // without anyone having to think to look for it.
            if (_revived > 0 || _revivalBudget > 0)
            {
                Log.Info(LogCategory.Wm,
                    $"startup recovery: revived {_revived} of {_revivalBudget} remembered window(s)");
            }

            RestoreTheView();

            // Restoration applies only to the initial adoption pass. A window opened
            // later must land where the user is, not where an old session says.
            _restoring = false;
            _session = null;
            _claimedSessionEntries.Clear();
        }
    }

    /// <summary>
    /// Puts each monitor back on the workspace it was showing, and focus back on the
    /// monitor that had it.
    /// </summary>
    /// <remarks>
    /// Restoring the windows and not the view is only half the job: a restart landed
    /// the user on whichever workspace happened to sort first, with the one they had
    /// been working on still there but somewhere else, and every window in the right
    /// place except the one in front of them.
    /// </remarks>
    private void RestoreTheView()
    {
        if (_session?.Monitors is not { Count: > 0 } monitors) return;

        MonitorNode? focused = null;

        foreach (RememberedMonitor remembered in monitors)
        {
            MonitorNode? monitor = _wm.Root.FindMonitor(remembered.DeviceId);

            // A monitor that is no longer attached takes its view with it. The
            // workspaces themselves have already been re-homed by the config.
            if (monitor is null) continue;

            if (monitor.FindWorkspace(remembered.ActiveWorkspace) is { } workspace)
                Publish(_wm.ActivateWorkspace(workspace));

            if (remembered.Focused) focused = monitor;
        }

        // Focus last, so activating the other monitors cannot steal it back.
        if (focused?.ActiveWorkspace is { } target)
        {
            Publish(_wm.ActivateWorkspace(target));

            Log.Info(LogCategory.Wm,
                $"restored the view: {target.Name} on {focused.DeviceId}");
        }

        KeepTheWindowInFrontInView();
    }

    /// <summary>
    /// Overrides the restored view when it would hide the window the user is looking at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session is a memory of where things were last time; the foreground window
    /// is where the user is now. When they disagree, now wins.
    /// </para>
    /// <para>
    /// This is not hypothetical - it is how Shubbak is almost always started. You
    /// launch it from a terminal, and the session remembers some other workspace as
    /// the one that was showing, so the first thing the window manager does is switch
    /// away from the window you just typed into. From the outside that reads as having
    /// lost the terminal, or as the thing having crashed and taken the desktop with it.
    /// </para>
    /// <para>
    /// Nothing has been moved at this point, so the foreground window really is what is
    /// on screen rather than something Shubbak has just put there.
    /// </para>
    /// </remarks>
    private void KeepTheWindowInFrontInView()
    {
        nint foreground = Win32Window.GetForeground();

        _ = _windows.TryGet(foreground, out WindowNode? inFront);

        if (WorkspaceToKeepInView(inFront) is not { } itsWorkspace) return;

        Publish(_wm.ActivateWorkspace(itsWorkspace));

        Log.Info(LogCategory.Wm,
            $"showing {itsWorkspace.Name} instead: it has the window in front " +
            $"(\"{inFront!.Identity.Title.Truncate(40)}\")");
    }

    /// <summary>
    /// The workspace that must be shown so the window in front stays visible, or null
    /// when the restored view already shows it.
    /// </summary>
    /// <remarks>
    /// Separated from reading the desktop so the decision can be tested. Getting it
    /// wrong is not subtle - it either switches away from the window the user is
    /// looking at, or overrides a perfectly good restored view for no reason.
    /// </remarks>
    internal static WorkspaceNode? WorkspaceToKeepInView(WindowNode? inFront)
    {
        // Nothing in front, or nothing Shubbak manages. A window it does not manage
        // is not on a workspace at all, so there is no view that would show it.
        if (inFront?.Workspace is not { } workspace) return null;

        // A workspace no monitor is hosting cannot be displayed, so activating it
        // would show the user nothing at all. Declared in the config but not yet
        // taken by a monitor is the ordinary way to be in this state.
        if (workspace.Monitor is null) return null;

        // The session and the desktop agree. Activating it again would be a no-op the
        // state machine short-circuits anyway, but saying so here is clearer than
        // relying on that.
        return inFront.IsOnADisplayedWorkspace ? null : workspace;
    }

    // ---- rules -------------------------------------------------------------

    private static WindowAttributes ToAttributes(nint handle)
    {
        uint processId = Win32Window.GetProcessId(handle);
        string? path = Win32Window.GetProcessPath(processId);

        return new WindowAttributes(
            Win32Window.GetTitle(handle),
            Win32Window.GetClassName(handle),
            path is null ? string.Empty : Path.GetFileNameWithoutExtension(path),
            path);
    }

    private void ApplyRules(WindowNode window, WindowAttributes attributes, RuleTrigger trigger)
    {
        IReadOnlyList<WindowRule> rules = _rules.For(trigger);

        // Indexed rather than foreach: this runs on the tick path for every title
        // change once any rule wants them, and the enumerator for an IReadOnlyList is
        // an interface call per element that allocates.
        for (int i = 0; i < rules.Count; i++)
        {
            WindowRule rule = rules[i];

            if (!_rules.Matches(rule, attributes)) continue;

            // Rules act on the window they matched, so focus is moved there first.
            // Otherwise `move --workspace 5` in a rule would move whatever the user
            // happened to be looking at.
            WindowNode? previous = _wm.FocusedWindow;

            Publish(_wm.FocusWindow(window));
            Execute(rule.Commands.Where(c => c is not IgnoreCommand and not ManageCommand));

            if (previous is not null && !ReferenceEquals(previous, window) && previous.Workspace is not null)
                Publish(_wm.FocusWindow(previous));
        }
    }

    // ---- commands ----------------------------------------------------------

    /// <summary>Runs a sequence of commands, as a keybinding or a rule does.</summary>
    /// <remarks>
    /// The outcome is dropped rather than ignored. A command that resolves to nothing
    /// has already published its own rejection, carrying a far better explanation than
    /// anything reconstructed here, and that is what reaches the log and the IPC
    /// subscribers. Only a caller that owes someone an answer - the pipe - needs the
    /// outcome handed back, and it calls <see cref="RunCommand"/> for itself.
    /// </remarks>
    private void Execute(IEnumerable<WmCommand> commands)
    {
        foreach (WmCommand command in commands) _ = RunCommand(command);
    }

    /// <summary>
    /// Points the window manager at whichever window is actually in front, before a
    /// command that acts on one is run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Focus is not Shubbak's to define. The window in front may be a dialog, a tray
    /// popup, or an application the filter passed over - and for those, nothing
    /// updates the focused window, so it still names whatever was focused before.
    /// Every command that acts on "the focused window" then acted on that earlier one:
    /// the float key untiled a window elsewhere on screen, and the close key would
    /// have closed it, with nothing said either way.
    /// </para>
    /// <para>
    /// Commands that act on a workspace, a monitor, or focus itself are left alone.
    /// They stay useful from an unmanaged window - moving focus out of one is exactly
    /// how it is left - and refusing them would break clicking a workspace on the bar,
    /// whose own window is not managed either.
    /// </para>
    /// </remarks>
    /// <returns>Whether the command should run.</returns>
    private bool ResolveTarget(WmCommand command)
    {
        if (!command.TargetsFocusedWindow) return true;

        nint foreground = Win32Window.GetForeground();
        if (foreground == 0) return true;

        // Managed, so Shubbak's own idea of focus is the authority - even when the two
        // disagree. They disagree constantly for a moment at a time: SetForegroundWindow
        // is asynchronous, so immediately after a focus command the desktop still names
        // the previous window.
        //
        // Re-syncing to the desktop here looked harmless and was not. Pressing a focus
        // key and then a move key quickly enough moved the window focus had just left,
        // because the desktop had not caught up yet - and pressing the same key again
        // worked, which is what made it look like a swap rather than a race.
        //
        // A genuine click raises a foreground event, which updates focus properly. This
        // check exists only to notice a window Shubbak does not manage at all.
        if (_windows.IsManaged(foreground)) return true;

        // Toggle-managed is exempt: acting on a window Shubbak does not manage is the
        // entire purpose of it, and it reads the foreground window itself.
        if (command is ToggleManagedCommand) return true;

        // Evaluated once. It is about ten Win32 calls including an OpenProcess, and it
        // was being run twice on the same handle for every refused command - once to
        // decide, and again to explain.
        ManageDecision decision = WindowFilter.Evaluate(foreground);

        // The focused window is one of ours and merely sits behind something that is
        // not - a dialog that has just opened, a flyout. The command was meant for it.
        if (_wm.FocusedWindow is { } focused &&
            Win32Window.Exists((nint)focused.Handle) &&
            decision.Reason is ExclusionReason.ToolWindow)
        {
            return true;
        }

        if (_config.UnmanagedWindowCommands == UnmanagedWindowCommands.Adopt)
        {
            TryManage(foreground, forced: true);

            if (_windows.TryGet(foreground, out WindowNode? adopted))
            {
                Publish(_wm.FocusWindow(adopted));
                return true;
            }
        }

        Publish(new WmResult(false, [new CommandRejected(command.Name, DescribeForeground(foreground, decision))]));
        return false;
    }

    /// <summary>Explains which window is in front and why it was not acted on.</summary>
    /// <remarks>
    /// Names the window rather than saying "no focused window". The whole difficulty
    /// is that the user is looking straight at it, so a message that does not identify
    /// it reads as the keybinding being broken.
    /// </remarks>
    private static string DescribeForeground(nint foreground, ManageDecision decision)
    {
        string title = Win32Window.GetTitle(foreground).Truncate(40);
        string className = Win32Window.GetClassName(foreground);

        return
            $"\"{title}\" [{className}] is not managed ({decision.Explain()}). " +
            "Take it on with toggle-managed, add a rule, or set " +
            "unmanaged-window-commands \"adopt\".";
    }

    /// <summary>
    /// Records which binding mode is active, and how to leave one that swallows keys.
    /// </summary>
    /// <remarks>
    /// At info rather than debug. A mode that makes the keyboard inert is the one
    /// state where the log is the only thing that can still be read, and "which keys
    /// still work" is the only question worth answering at that point.
    /// </remarks>
    private void ReportBindingMode(string? mode)
    {
        if (mode is null)
        {
            Log.Info(LogCategory.Hook, "binding mode cleared; the default bindings are back");
            return;
        }

        BindingMode? declared = _config.BindingModes
            .FirstOrDefault(m => string.Equals(m.Name, mode, StringComparison.OrdinalIgnoreCase));

        if (declared is null || declared.PassThrough)
        {
            Log.Info(LogCategory.Hook, $"binding mode '{mode}' active");
            return;
        }

        string[] exits = [.. declared.Keybindings
            .Where(b => b.Commands.Any(c => c is DisableBindingModeCommand or EnableBindingModeCommand))
            .Select(b => b.Key.Display)];

        Log.Info(LogCategory.Hook,
            $"binding mode '{mode}' active and swallowing every key. " +
            $"Way out: {(exits.Length > 0 ? string.Join(" or ", exits) : "none bound")}, " +
            "or run: shubbak wm-disable-binding-mode");
    }

    /// <summary>Whether the pipe may be used to launch processes.</summary>
    internal bool AllowShellExecOverIpc => _config.AllowShellExecOverIpc;

    internal CommandOutcome RunCommand(WmCommand command)
    {
        if (!ResolveTarget(command))
        {
            return new CommandOutcome(
                new WmResult(false, [new CommandRejected(command.Name, "the focused window is not managed")]));
        }

        CommandOutcome outcome = _executor.Execute(command);

        Publish(outcome.Result);
        PerformHostAction(outcome);

        return outcome;
    }

    /// <summary>
    /// Explains how Shubbak sees a window.
    /// </summary>
    /// <remarks>
    /// Reports every matchable attribute, whether the window is manageable and why
    /// not if it is not, and which configured rules match. This is the answer to
    /// "why is this window not being tiled?", which is otherwise diagnosed only by
    /// trial and error.
    /// </remarks>
    internal string Inspect(nint handle)
    {
        var report = new System.Text.StringBuilder();

        WindowAttributes attributes = ToAttributes(handle);
        ManageDecision decision = WindowFilter.Evaluate(handle);
        Rect bounds = Win32Window.GetBounds(handle);

        report.AppendLine($"handle       0x{handle:X}");
        report.AppendLine($"title        {attributes.Title}");
        report.AppendLine($"class        {attributes.ClassName}");
        report.AppendLine($"process      {attributes.ProcessName}");
        report.AppendLine($"path         {attributes.ProcessPath ?? "(unreadable - elevated process?)"}");
        report.AppendLine($"rect         {bounds}");
        report.AppendLine($"style        0x{Win32Window.GetStyleBits(handle):X8}");
        report.AppendLine($"ex-style     0x{Win32Window.GetExStyleBits(handle):X8}");
        report.AppendLine($"visible      {Win32Window.IsVisible(handle)}");
        report.AppendLine($"cloaked      {Win32Window.GetCloakState(handle)}");
        report.AppendLine($"minimised    {Win32Window.IsMinimised(handle)}");
        report.AppendLine();

        report.AppendLine($"manageable   {(decision.Manageable ? "yes" : "no")} - {decision.Explain()}");

        if (_windows.TryGet(handle, out WindowNode? window))
        {
            report.AppendLine($"managed      yes");
            report.AppendLine($"  node       #{window.Id}");
            report.AppendLine($"  state      {window.State}");
            report.AppendLine($"  workspace  {window.Workspace?.Name ?? "(none)"}");
            report.AppendLine($"  focused    {ReferenceEquals(window, _wm.FocusedWindow)}");
        }
        else
        {
            report.AppendLine($"managed      no{(_windows.IsExcluded(handle) ? " (excluded by a rule)" : "")}");
        }

        report.AppendLine();
        report.AppendLine("rules");

        if (_config.Rules.Count == 0)
        {
            report.AppendLine("  (none configured)");
        }
        else
        {
            foreach (WindowRule rule in _config.Rules)
            {
                bool matched = rule.Matches(attributes, _config.Apps);
                report.AppendLine($"  [{(matched ? "x" : " ")}] {rule.Name} (line {rule.Span.Start.Line})");
            }
        }

        // Listing apps separately is what turns "my rule does not fire" into a
        // one-glance diagnosis: the rule is fine, the app definition is what missed.
        if (_config.Apps.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("apps");

            foreach ((string name, AppDefinition app) in _config.Apps)
            {
                bool matched = app.Matches(attributes);
                report.AppendLine($"  [{(matched ? "x" : " ")}] {name}");

                if (!matched)
                {
                    foreach (WindowMatcher matcher in app.Matchers)
                    {
                        bool ok = matcher.Matches(attributes.Get(matcher.Target));
                        if (!ok) report.AppendLine($"        failed: {matcher}");
                    }
                }
            }
        }

        return report.ToString();
    }

    /// <summary>
    /// Takes the focused window under management, or releases it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Works on the foreground window when nothing is managed, which is the whole
    /// point: the window you want to take on is by definition not one Shubbak knows
    /// about, so there is no node to name it by.
    /// </para>
    /// <para>
    /// Releasing records the window as ignored. Without that it would be picked up
    /// again by the next event it happened to raise - and a window raises a great many
    /// - so letting go would last a fraction of a second.
    /// </para>
    /// </remarks>
    private void ToggleManaged()
    {
        // The window in front, not the one the tree thinks is focused. Written the
        // other way round it was useless for the one job it has: focus sitting on an
        // unmanaged window leaves the tree naming whatever was focused before, so
        // asking to take on the window in front released a different one instead.
        nint handle = Win32Window.GetForeground();

        if (handle == 0 && _wm.FocusedWindow is { } focused) handle = (nint)focused.Handle;

        if (handle == 0)
        {
            Log.Warn(LogCategory.Window, "toggle-managed: no window in front to act on.");
            return;
        }

        if (_windows.IsManaged(handle))
        {
            string title = Win32Window.GetTitle(handle).Truncate(40);

            // Excluded as part of the release rather than after it. Releasing forgets
            // every verdict about a window, so marking it separately beforehand would
            // be wiped, and the window would be taken straight back by the very next
            // event it raised.
            TryUnmanage(handle, thenExclude: true);

            Log.Info(LogCategory.Window, $"released 0x{handle:X} \"{title}\" - now unmanaged");
            return;
        }

        _windows.Reconsider(handle);
        TryManage(handle, forced: true);

        if (_windows.IsManaged(handle))
        {
            Log.Info(LogCategory.Window,
                $"took on 0x{handle:X} \"{Win32Window.GetTitle(handle).Truncate(40)}\"");
        }
    }

    private void PerformHostAction(CommandOutcome outcome)
    {
        switch (outcome.Action)
        {
            case HostAction.CloseFocusedWindow:
                if (_wm.FocusedWindow is { } window) WindowActions.Close((nint)window.Handle);
                break;

            case HostAction.ToggleManaged:
                ToggleManaged();
                break;

            case HostAction.ShellExecute:
                if (outcome.Payload is { } commandLine) ShellExecute(commandLine);
                break;

            case HostAction.ReloadConfig:
                LoadConfig(_configPath, initial: false);
                _layoutDirty = true;

                // Announced so the bar, which is a separate process reading the same
                // file, reloads at the same moment rather than keeping whatever it was
                // launched with.
                //
                // Through Publish, because that is the only path that reaches the IPC
                // subscribers. An earlier version raised a daemon-level event instead,
                // which announced it to nothing at all - that event never had a single
                // subscriber. The bar carried on with its old configuration and said
                // nothing, which looked exactly like a reload that had worked.
                Publish(new WmResult(true, [new ConfigReloaded(_configPath)]));
                break;

            case HostAction.Redraw:
                // Forget every cached rectangle so the next pass re-applies all of
                // them, even the ones already thought to be correct.
                foreach (nint handle in _windows.Handles) _committer.Forget(handle);
                _layoutDirty = true;
                break;

            case HostAction.Exit:
                Log.Info(LogCategory.Wm, "exit requested");
                Stop();
                break;

            case HostAction.None:
            default:
                break;
        }
    }

    /// <summary>Runs a command, detached.</summary>
    private static void ShellExecute(string commandLine)
    {
        try
        {
            (string file, string arguments) = SplitCommandLine(commandLine);

            bool direct = CanLaunchDirectly(file);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = !direct,

                // Only honoured on the direct path; the shell ignores it. That is a
                // behaviour change for a console program launched by full path, which
                // used to show a window despite this being set. The setting says what
                // was always wanted.
                CreateNoWindow = true,
            };

            process.Start();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Command, $"shell-exec failed: {commandLine}", ex);
        }
    }

    /// <summary>
    /// Whether a target can be started directly rather than through the shell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every startup command went through <c>ShellExecuteEx</c>, which is what
    /// <c>UseShellExecute</c> means, and it is slow: launching the bar took
    /// <b>2,126 ms</b> of a 2,472 ms startup on the author's machine - 86% of it -
    /// and the bar had not begun running when the call returned, so none of that time
    /// was the bar starting. It was all shell overhead.
    /// </para>
    /// <para>
    /// <c>CreateProcess</c>, which is what the direct path uses, takes milliseconds.
    /// It cannot do everything the shell can, so it is used only where it is
    /// definitely equivalent: an executable, named by a full path, that exists.
    /// </para>
    /// <para>
    /// Everything else keeps the shell, and needs it. A <c>.bat</c> or <c>.cmd</c>
    /// requires a command processor; a <c>.lnk</c> needs resolving; a URL or a
    /// document needs a verb looked up; and a bare name like <c>notepad</c> needs a
    /// path search this deliberately does not attempt.
    /// </para>
    /// </remarks>
    internal static bool CanLaunchDirectly(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return false;

        // A relative or bare name would have to be resolved, and resolving it the same
        // way the shell does is exactly the work being avoided.
        if (!Path.IsPathFullyQualified(file)) return false;

        if (!string.Equals(Path.GetExtension(file), ".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        // Checked last: it touches the disk, and the cheap tests above rule out most
        // of what reaches here.
        return File.Exists(file);
    }

    private static (string File, string Arguments) SplitCommandLine(string commandLine)
    {
        commandLine = commandLine.Trim();

        if (commandLine.StartsWith('"'))
        {
            int close = commandLine.IndexOf('"', 1);

            if (close > 0)
                return (commandLine[1..close], commandLine[(close + 1)..].Trim());

            // A quote opened and never closed. Falling through to the space split was
            // worse than useless: it returned the opening quote as part of the path,
            // so `"C:\Program Files\app.exe` launched `"C:\Program` and failed with a
            // message naming a file nobody had typed.
            //
            // Quoting is the user saying the path contains spaces, so the remainder is
            // all path and there are no arguments. That is the only reading that can
            // still start the program they meant.
            return (commandLine[1..], string.Empty);
        }

        int space = commandLine.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? (commandLine, string.Empty)
            : (commandLine[..space], commandLine[(space + 1)..]);
    }

    // ---- layout ------------------------------------------------------------

    private void ApplyLayout()
    {
        IReadOnlyList<Placement> placements = _wm.ComputePlacements();

        // Taken once and reset here rather than at the end, so a pass that throws does
        // not leave the next one wearing a reason that has already been spent.
        AnimationKind movementKind = _pendingLayoutKind;
        _pendingLayoutKind = AnimationKind.WindowMove;

        _commitScratch.Clear();

        ConcealOutgoing(placements);

        foreach (Placement placement in placements) Schedule(placement, movementKind);

        CommitScheduled(placements.Count);
        TraceLayout(placements);
        FocusIfDisplayed();

        ApplyFocusBorder(geometryChanged: true);
    }

    /// <summary>Takes every window that should be off screen off it, before anything is shown.</summary>
    /// <remarks>
    /// Revealing as we went meant the incoming workspace was on screen while the
    /// outgoing one still was, so a switch showed both at once for a frame - two sets
    /// of windows overlapping, which is the moment the user notices and the hardest
    /// one to photograph.
    /// </remarks>
    private void ConcealOutgoing(IReadOnlyList<Placement> placements)
    {
        foreach (Placement placement in placements)
        {
            if (placement.Visible) continue;

            _animation.Remove(placement.Window.Handle);
            _committer.Conceal((nint)placement.Window.Handle);
        }
    }

    /// <summary>
    /// Decides whether one window is animated to its new rectangle or simply placed
    /// there, and queues it accordingly.
    /// </summary>
    private void Schedule(Placement placement, AnimationKind movementKind)
    {
        nint handle = (nint)placement.Window.Handle;

        // Hidden windows are never animated: moving something the user cannot see is
        // wasted work, and it would keep the animation engine busy for every window on
        // every inactive workspace. They are still committed, so they hold a correct
        // rectangle for when the workspace is shown.
        if (!placement.Visible)
        {
            _commitScratch.Add(placement);
            return;
        }

        // Visibility is applied here, separately from geometry, because an animated
        // window never reaches Commit - the animation engine drives it frame by frame
        // instead. Leaving the reveal to Commit meant a window whose position changed
        // was animated into place while still concealed, so a workspace that had been
        // switched away from came back empty.
        _committer.Reveal(handle);

        // Where the window is now: mid-flight position if it is already moving,
        // otherwise its real position on screen.
        //
        // Measured as the visible frame, because that is what a layout rectangle
        // describes. GetWindowRect includes the window's shadow, so comparing it
        // against the target reported a difference for every shadowed window even when
        // it had not moved at all - and a focus change re-runs the layout, so every
        // focus change animated the window out by the width of its own shadow and back.
        Rect current = _animation.TryGetCurrent(placement.Window.Handle, out Rect inFlight)
            ? inFlight
            : WindowCommitter.VisibleBounds(handle);

        AnimationKind kind = current.IsEmpty ? AnimationKind.WindowOpen : movementKind;

        // A window joining the layout for the first time.
        //
        // Placed rather than animated unless asked otherwise, because the rectangle it
        // would travel from is whatever size the application opened at - it was never
        // part of the arrangement, so the motion describes nothing that happened. It is
        // also the most expensive animation there is: a window that relays out its
        // contents on every resize does so once per frame, which File Explorer makes
        // very obvious.
        //
        // When it is wanted, it uses the window-open profile rather than window-move,
        // so the two can be tuned apart.
        if (_windows.TakeArriving(handle))
        {
            if (!_config.Animation.AnimateNewWindows)
            {
                _animation.Remove(placement.Window.Handle);
                _commitScratch.Add(placement);
                return;
            }

            kind = AnimationKind.WindowOpen;
        }

        if (_animation.Retarget(placement.Window.Handle, current, placement.Rect, kind))
        {
            // Raised here, because an animated window never reaches Commit and Commit
            // is where Raise is otherwise honoured. The layout engine sets it for
            // exactly two things - a fullscreen or maximised window, and the focused
            // window in a layout whose rectangles overlap, which is monocle - so
            // entering any of those did not bring the window forward whenever the
            // rectangle also moved. Which is almost always: a window already in the
            // right place is not animated at all, so the feature worked only in the
            // cases where it was not needed.
            //
            // Before the motion rather than after it. A window travelling to the front
            // should be in front while it travels.
            if (placement.Raise) WindowCommitter.Raise(handle);

            // Animated: the tick loop drives the geometry from here.
            return;
        }

        _commitScratch.Add(placement);
    }

    /// <summary>Moves every window that is not being animated, in one transaction.</summary>
    private void CommitScheduled(int total)
    {
        if (_commitScratch.Count == 0) return;

        int moved = _committer.Commit(_commitScratch, static p => (nint)p.Window.Handle);

        if (moved > 0 && Log.IsEnabled(LogLevel.Debug))
        {
            Log.Debug(LogCategory.Layout,
                $"placed {moved}/{total} windows, {_animation.ActiveCount} animating");
        }
    }

    /// <summary>
    /// Records what the layout wanted and what the desktop actually shows.
    /// </summary>
    /// <remarks>
    /// A window that is visible when it should not be is either mis-decided by the
    /// layout or mis-applied afterwards, and there is no way to tell which from the
    /// outside. This is the only place both answers appear together.
    /// </remarks>
    private static void TraceLayout(IReadOnlyList<Placement> placements)
    {
        if (!Log.IsEnabled(LogLevel.Trace)) return;

        foreach (Placement placement in placements)
        {
            nint handle = (nint)placement.Window.Handle;

            Log.Trace(LogCategory.Layout,
                $"  0x{handle:X} \"{placement.Window.Identity.Title.Truncate(24)}\" " +
                $"ws={placement.Window.Workspace?.Name ?? "-"} " +
                $"want={(placement.Visible ? "shown" : "hidden")} " +
                $"is={(Win32Window.IsVisible(handle) ? "visible" : "invisible")}/" +
                $"{Win32Window.GetCloakState(handle)} {placement.Rect}");
        }
    }

    /// <summary>
    /// Brings the focused window to the foreground, if the layout just put it on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After geometry, because focusing a window that is about to move makes it flash
    /// at its old position first.
    /// </para>
    /// <para>
    /// Only ever to a window the layout just decided to show. Adoption focuses each
    /// window it takes on, so at startup the focused window is whichever one the
    /// enumeration happened to reach last - frequently on a workspace that is not
    /// displayed. Forcing that to the foreground raised it over the workspace the user
    /// was actually looking at, and the resulting foreground event then switched the
    /// desktop to that workspace, where nothing had been placed yet.
    /// </para>
    /// </remarks>
    private void FocusIfDisplayed()
    {
        if (_wm.FocusedWindow is not { } focused) return;
        if (!focused.IsOnADisplayedWorkspace) return;
        if (Win32Window.GetForeground() == (nint)focused.Handle) return;

        WindowActions.Focus((nint)focused.Handle);
    }

    /// <summary>
    /// Whether a periodic job is due, recording that it ran when it is.
    /// </summary>
    /// <remarks>
    /// The tick carries three jobs on their own timers - monitors, the focus border,
    /// the session - and each had written out this arithmetic for itself. Converting
    /// Stopwatch ticks to milliseconds in three places is three chances to get the
    /// conversion the wrong way round, and the symptom would be a job that silently
    /// never runs or one that runs every pass.
    /// </remarks>
    /// <param name="intervalMs">The shortest gap between runs.</param>
    /// <param name="now">The current timestamp, shared by every job on this tick.</param>
    /// <param name="last">
    /// When the job last ran, updated in place. Zero means it never has, which is
    /// always due - the first pass should not have to wait out an interval.
    /// </param>
    private static bool DueEvery(double intervalMs, long now, ref long last)
    {
        if (last != 0 && (now - last) * 1000.0 / Stopwatch.Frequency < intervalMs) return false;

        last = now;
        return true;
    }

    /// <summary>
    /// Periodically re-reads the monitor layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The work area is not static. Taj reserves its strip through the shell's appbar
    /// API, and if the bar starts after the window manager - which it does, since the
    /// window manager launches it - the work area shrinks <i>after</i> the initial
    /// read. Without this, windows are tiled underneath the bar until something else
    /// happens to trigger a relayout.
    /// </para>
    /// <para>
    /// The same path covers everything else that moves the work area: the taskbar
    /// being resized or set to auto-hide, a resolution change, DPI changes, and
    /// monitors being plugged or unplugged.
    /// </para>
    /// <para>
    /// Polled rather than driven by <c>WM_SETTINGCHANGE</c> because the daemon has no
    /// window to receive it, and enumerating a handful of monitors twice a second is
    /// far cheaper than creating and pumping one just for this.
    /// </para>
    /// </remarks>
    private void MaybeSyncMonitors(long now)
    {
        if (!DueEvery(2_000, now, ref _lastMonitorSyncTicks)) return;

        // Enumerated once and handed to both, rather than each asking the display
        // configuration for itself. Asking twice a second, forever, for the whole life
        // of the process is the same waste SettleWorkArea already avoids.
        IReadOnlyList<MonitorInfo> monitors = MonitorSource.Enumerate();

        if (MonitorLayoutChanged(monitors)) SyncMonitors(monitors);
    }

    /// <summary>
    /// Whether an already-read monitor list differs from the tree.
    /// </summary>
    /// <remarks>
    /// Checked before syncing because <see cref="SyncMonitors(IReadOnlyList{MonitorInfo})"/>
    /// publishes events and marks the layout dirty, and doing that twice a second
    /// regardless would keep the window manager permanently busy on an idle desktop.
    /// </remarks>
    private bool MonitorLayoutChanged(IReadOnlyList<MonitorInfo> current)
    {
        if (current.Count != _wm.Root.Monitors.Count) return true;

        foreach (MonitorInfo info in current)
        {
            MonitorNode? existing = _wm.Root.FindMonitor(info.DeviceId);

            if (existing is null ||
                existing.Bounds != info.Bounds ||
                existing.WorkArea != info.WorkArea ||
                existing.Dpi != info.Dpi)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Periodically writes the session, so a crash or a power cut does not lose the
    /// arrangement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Time-based rather than change-based. Saving on every change would write the
    /// file dozens of times while dragging a window, and the arrangement genuinely
    /// worth preserving is the settled one, not every intermediate state.
    /// </para>
    /// <para>
    /// Skipped while windows are animating, so a save never lands mid-transition and
    /// records geometry that was never really the layout.
    /// </para>
    /// </remarks>
    private void MaybeSaveSession(long now)
    {
        if (_animation.IsAnimating) return;
        if (!DueEvery(30_000, now, ref _lastSessionSaveTicks)) return;

        if (_windows.ManagedCount > 0) SessionStore.Save(_wm.Root, _sessionPath, routine: true, focusedMonitor: _wm.FocusedMonitor);
    }

    /// <summary>Drives one frame of in-flight window motion.</summary>
    private void AdvanceAnimation(double deltaMs)
    {
        if (_frameScratch.Length < _animation.ActiveCount)
            _frameScratch = new AnimationFrame[Math.Max(_animation.ActiveCount * 2, 128)];

        int count = _animation.Tick(deltaMs, _frameScratch);
        if (count == 0) return;

        _framesDelivered++;
        _frameBatchSize.Record(count);

        long committing = Stopwatch.GetTimestamp();

        // One atomic transaction per frame, exactly as in the S2 spike: the
        // unbatched alternative dropped 33-42% of frames at 144 Hz.
        _committer.CommitFrame(_frameScratch.AsSpan(0, count));

        // Timed on its own. ADR 0001 measured 94.6% of frame time inside this call at
        // twenty windows, and that has not been checked in the shipping binary since.
        _commitFrameDuration.Record(
            (Stopwatch.GetTimestamp() - committing) * 1000.0 / Stopwatch.Frequency);

        // A window that has just stopped moving has almost certainly repainted its own
        // frame on the way, and applications that draw their own frame rewrite the
        // border colour when they do. Re-asserted at the end of the movement rather
        // than at the start of it: the layout pass that began the animation ran a
        // hundred and forty milliseconds ago, and anything it set has since been
        // painted over.
        if (!_config.Effects.Enabled) return;

        for (int i = 0; i < count; i++)
        {
            if (_frameScratch[i].IsFinal) RefreshBorderFor((nint)_frameScratch[i].Handle);
        }
    }

    /// <summary>Re-applies whichever border a window should be wearing.</summary>
    /// <remarks>
    /// Both colours, not just the focused one. A move swaps two windows and resizes
    /// both, so the one that is merely alongside loses its unfocused border just as
    /// readily - and the once-a-second refresh only ever heals the focused window, so
    /// nothing put that one back until focus next moved.
    /// </remarks>
    private void RefreshBorderFor(nint handle)
    {
        if (!_windows.TryGet(handle, out WindowNode? window)) return;

        ApplyBorder(window, ColourFor(window, ReferenceEquals(window, _borderedWindow)));
    }

    /// <summary>Which border colour a window should be wearing.</summary>
    /// <remarks>
    /// <para>
    /// A window that is not in the tiling flow can be given its own colour, because
    /// from the outside it looks identical to one that is - same border, same focus -
    /// while behaving completely differently: it ignores the layout, it can be dragged
    /// anywhere, and directional focus skips over it. Not being able to tell which
    /// kind of window is in front turns every one of those differences into a surprise.
    /// </para>
    /// <para>
    /// Falls back to the ordinary colours when unset, so the setting costs nothing to
    /// anyone who does not want it.
    /// </para>
    /// </remarks>
    private string? ColourFor(WindowNode window, bool focused)
    {
        WindowEffects effects = _config.Effects;

        if (window.IsTiled)
            return focused ? effects.FocusedColour : effects.UnfocusedColour;

        return focused
            ? effects.FloatingColour ?? effects.FocusedColour
            : effects.FloatingUnfocusedColour ?? effects.UnfocusedColour;
    }

    /// <summary>
    /// Marks the focused window with a coloured border.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the two windows whose state changed are touched, not every managed
    /// window. Re-applying a border that is already correct is a compositor round
    /// trip per window per focus change, and with a dozen windows open that is
    /// noticeable.
    /// </para>
    /// <para>
    /// When no unfocused colour is configured, unfocused windows have their border
    /// cleared rather than being given one - a border on everything is visual noise
    /// and defeats the purpose of marking the focused window at all.
    /// </para>
    /// <para>
    /// Geometry changes re-assert it even when focus did not move. The border belongs
    /// to the window, not to Shubbak, and an application that draws its own frame
    /// rewrites it while repainting - which a resize reliably provokes. Skipping the
    /// re-assert because the same window was still focused meant that after every
    /// move the border simply went out, and stayed out until the once-a-second
    /// refresh happened to come round. That refresh is phase-locked to startup, so
    /// the gap was anywhere up to a full second.
    /// </para>
    /// </remarks>
    /// <param name="geometryChanged">
    /// True when windows have just been placed, so borders may have been repainted
    /// away even though focus is unchanged.
    /// </param>
    private void ApplyFocusBorder(bool geometryChanged = false)
    {
        if (!_config.Effects.Enabled) return;

        WindowNode? focused = _wm.FocusedWindow;

        if (ReferenceEquals(focused, _borderedWindow))
        {
            // Same window, but it may have just been resized out of its border.
            if (geometryChanged && focused is not null)
                ApplyBorder(focused, ColourFor(focused, focused: true));

            return;
        }

        if (_borderedWindow is { } previous && Win32Window.Exists((nint)previous.Handle))
            ApplyBorder(previous, ColourFor(previous, focused: false));

        if (focused is not null) ApplyBorder(focused, ColourFor(focused, focused: true));

        _borderedWindow = focused;
    }

    /// <summary>
    /// Reasserts the focused window's border.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DWMWA_BORDER_COLOR</c> is a property of the window, not of Shubbak, and
    /// applications are free to set it themselves. Ones that manage their own frame -
    /// Windows Terminal and other WinUI applications especially - reset it as part of
    /// ordinary repainting, which silently clears ours.
    /// </para>
    /// <para>
    /// Setting it once when focus changes is therefore not enough: the border would
    /// vanish partway through using a window and not return until focus moved away
    /// and back. Reasserting on a slow timer costs one DWM call a second and makes
    /// the border heal itself no matter what the application does.
    /// </para>
    /// </remarks>
    private void MaybeRefreshFocusBorder(long now)
    {
        if (!_config.Effects.Enabled) return;
        if (!DueEvery(1_000, now, ref _lastBorderRefreshTicks)) return;

        if (_borderedWindow is not { } window) return;
        if (!Win32Window.Exists((nint)window.Handle)) return;

        ApplyBorder(window, ColourFor(window, focused: true));
    }

    private static void ApplyBorder(WindowNode window, string? colour)
    {
        nint handle = (nint)window.Handle;

        if (!Colour.TryParse(colour, out Colour parsed))
        {
            WindowActions.ClearBorderColour(handle);
            return;
        }

        // Alpha is discarded on purpose here rather than by accident. DWMWA_BORDER_COLOR
        // takes a COLORREF, which has nowhere to put it. The shared parser accepts
        // #RRGGBBAA because the bar can honour it; this is the one caller that cannot,
        // and the copy that used to live here quietly accepted eight digits and threw
        // the alpha away without either of those being a decision.
        WindowActions.SetBorderColour(handle, parsed.R, parsed.G, parsed.B);
    }

    // ---- monitors ----------------------------------------------------------

    private void SyncMonitors() => SyncMonitors(MonitorSource.Enumerate());

    private void SyncMonitors(IReadOnlyList<MonitorInfo> current)
    {
        foreach (MonitorInfo info in current)
        {
            MonitorNode? existing = _wm.Root.FindMonitor(info.DeviceId);

            if (existing is null)
            {
                var monitor = new MonitorNode(info.DeviceId, info.Bounds, info.WorkArea, info.Dpi)
                {
                    IsPrimary = info.IsPrimary,
                };

                Publish(_wm.AddMonitor(monitor));
            }
            else
            {
                Publish(_wm.UpdateMonitor(existing, info.Bounds, info.WorkArea, info.Dpi));
            }
        }

        // Monitors that vanished have their workspaces migrated, never discarded:
        // undocking must not strand every window on a display that came back.
        foreach (MonitorNode monitor in _wm.Root.Monitors.ToArray())
        {
            if (!current.Any(m => string.Equals(m.DeviceId, monitor.DeviceId, StringComparison.OrdinalIgnoreCase)))
                Publish(_wm.RemoveMonitor(monitor));
        }

        CreateConfiguredWorkspaces();

        // The work area may have shrunk - a bar appearing, the taskbar moving - so
        // everything has to be re-placed against the new bounds.
        _layoutDirty = true;
    }

    private void CreateConfiguredWorkspaces()
    {
        for (int index = 0; index < _config.Workspaces.Count; index++)
        {
            WorkspaceConfig declared = _config.Workspaces[index];

            ILayout? layout = declared.Layout is not null &&
                              LayoutRegistry.TryResolve(declared.Layout, out ILayout resolved)
                ? resolved
                : null;

            // Already here, so its settings are brought up to date rather than the
            // whole declaration being skipped. Reloading otherwise appeared to do
            // nothing for the settings people most often change - a workspace's
            // display name, which monitor it prefers, where it sits in the bar -
            // because the workspace it applied to already existed by name.
            if (_wm.Root.FindWorkspace(declared.Name) is { } existing)
            {
                existing.DisplayName = declared.DisplayName;
                existing.PreferredMonitorIndex = declared.BindToMonitor;
                existing.SortIndex = index;
                existing.IsTransient = false;

                // Only when the config names one: a workspace whose layout the user
                // has since changed by keystroke should keep it.
                if (layout is not null) existing.Layout = layout;

                continue;
            }

            var workspace = new WorkspaceNode(declared.Name, layout)
            {
                DisplayName = declared.DisplayName,
                PreferredMonitorIndex = declared.BindToMonitor,
                IsTransient = false,

                // Declaration order, so the bar shows workspaces in the order the
                // user thinks in rather than the order they happened to be created.
                SortIndex = index,
            };

            // Published, so the WorkspaceCreated event reaches subscribers now rather
            // than surfacing later attached to an unrelated operation.
            Publish(_wm.AddWorkspace(workspace));
        }
    }

    // ---- config ------------------------------------------------------------

    private void LoadConfig(string? path, bool initial)
    {
        if (path is null)
        {
            Log.Warn(LogCategory.Config, "no config file found; using defaults");
            _ = _bindings.Load(_config);
            _rules.Load(_config);
            return;
        }

        ConfigLoadResult result = ConfigLoader.LoadFile(path);
        string source = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        foreach (Diagnostic diagnostic in result.Diagnostics)
            Console.Error.Write(diagnostic.Render(source, path));

        if (result.HasErrors && !initial)
        {
            // Keeping the previous config is the safe failure mode: a typo must not
            // leave a running desktop with no keybindings.
            Log.Error(LogCategory.Config, "config has errors; keeping the previously loaded configuration");
            return;
        }

        _config = result.Config;
        _wm.Options = _config.ToWmOptions();
        _animation.Options = _config.Animation;

        // Tick never consults Enabled - only Retarget does - so swapping the options
        // stopped new animations and let every in-flight one run to completion. A
        // reload that turns animation off should stop the ones already moving, which is
        // what Clear is for and what nothing had ever called it for. The reload marks
        // the layout dirty, so the windows are placed at their targets on the next pass
        // rather than left wherever the motion had reached.
        if (!_config.Animation.Enabled) _animation.Clear();
        _committer.HideMethod = _config.HideMethod;
        _committer.KeepInTaskbar = _config.KeepInTaskbar;

        // The active mode is carried across when it still exists, and named when it
        // does not. Silently dropping it left the keyboard on the default bindings
        // while the state machine, the report and the bar all still announced the mode.
        string? lostMode = _bindings.Load(_config);

        _rules.Load(_config);

        // The other moment a key stops being bound. A binding deleted here leaves any
        // swallow flag it set with nothing to clear it, and the next press of that key
        // passes through while its release is still swallowed.
        _keyboard?.ForgetSwallowed();

        if (lostMode is not null)
        {
            Log.Warn(LogCategory.Hook,
                $"binding mode '{lostMode}' is no longer declared; back to the default bindings");

            // Announced, so the state machine and everything reading from it agree with
            // the table. Without this the mode could not even be re-entered: SetBindingMode
            // short-circuits on an unchanged name, so the key that enables it would find
            // it already active and emit nothing at all.
            Publish(_wm.SetBindingMode(null));
        }

        ApplyLoggingConfig(initial);

        if (!initial)
        {
            CreateConfiguredWorkspaces();

            // Forgotten, so rules are re-applied to windows that are already open.
            // The set is a cache of past verdicts, and the verdicts have just changed:
            // keeping it meant deleting an ignore rule and reloading did nothing at all
            // until the window was closed and reopened, which reads as the reload not
            // working.
            //
            // Windows released by hand are re-examined too. That is the honest reading
            // of a reload, and toggle-managed is one key away for anything that should
            // go back.
            int forgotten = _windows.ForgetVerdicts();

            ReconsiderOpenWindows();

            Log.Info(LogCategory.Config,
                $"reloaded: {_config.Keybindings.Count} keybindings, {_config.Rules.Count} rules, " +
                $"{forgotten} previously excluded window(s) re-examined");
        }
    }

    /// <summary>
    /// Re-runs the management decision over every window currently on the desktop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both directions. A rule that has just been added has to be able to release a
    /// window that is already managed, and a rule that has just been deleted has to be
    /// able to take one on - otherwise editing rules appears to do nothing until the
    /// application in question is restarted, which is a miserable way to work out
    /// whether a matcher is right.
    /// </para>
    /// <para>
    /// Windows the built-in filter rejects are not remembered, so they would be
    /// reconsidered on their next event anyway; a settled desktop may simply not
    /// produce one for a long time, and after a reload the user is watching for the
    /// change they just made.
    /// </para>
    /// </remarks>
    private void ReconsiderOpenWindows()
    {
        // Copied: releasing a window mutates the dictionary being walked.
        foreach (nint handle in _windows.HandlesSnapshot())
        {
            if (!Win32Window.Exists(handle)) continue;
            if (!_rules.ShouldIgnore(ToAttributes(handle))) continue;

            Log.Info(LogCategory.Rule,
                $"releasing 0x{handle:X} \"{Win32Window.GetTitle(handle).Truncate(40)}\": " +
                "a rule now excludes it");

            TryUnmanage(handle, thenExclude: true);
        }

        foreach (nint handle in Win32Window.EnumerateTopLevel())
        {
            if (_windows.IsManaged(handle)) continue;

            TryManage(handle);
        }
    }

    /// <summary>
    /// Applies the config's logging settings.
    /// </summary>
    /// <remarks>
    /// Command line flags win. Someone who launched with <c>--log-level trace</c> is
    /// mid-investigation, and having a config reload silently drop them back to
    /// <c>info</c> would throw away exactly the detail they were collecting.
    /// </remarks>
    private void ApplyLoggingConfig(bool initial)
    {
        if (_logLevelFromCommandLine) return;

        if (Log.Level != _config.LogLevel)
        {
            Log.Level = _config.LogLevel;
            Log.Info(LogCategory.Config, $"log level set to {_config.LogLevel} by config");
        }

        if (initial && _config.LogFile is { Length: > 0 } file && Log.FilePath is null)
        {
            try
            {
                Log.OpenFile(file);
                Log.Info(LogCategory.Config, $"logging to {Log.FilePath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(LogCategory.Config, $"could not open log file '{file}'", ex);
            }
        }
    }

    private void RunStartupCommands()
    {
        foreach (string command in _config.StartupCommands)
        {
            const string Prefix = "shell-exec ";

            if (command.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                ShellExecute(command[Prefix.Length..]);
            else
                ShellExecute(command);
        }
    }

    /// <summary>
    /// Waits for the usable area of each monitor to stop changing before the first
    /// layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bar reserves its strip of the screen by registering an appbar, and the shell
    /// only shrinks the work area once it has. Shubbak launches the bar itself, from
    /// the startup commands, so at that instant the work area is still the whole
    /// screen.
    /// </para>
    /// <para>
    /// Laying out immediately therefore placed every window over the full display -
    /// including the strip the bar was about to take - and the periodic monitor check
    /// only noticed up to two seconds later. Every start began with the windows
    /// briefly too big, sliding down and shrinking as the bar appeared underneath
    /// them.
    /// </para>
    /// <para>
    /// Bounded, and skipped entirely when nothing was launched: waiting is only worth
    /// it when something was asked to start. A bar that never appears costs the
    /// deadline once, at startup, rather than a visible jump on every start.
    /// </para>
    /// </remarks>
    private void SettleWorkArea()
    {
        if (_config.StartupCommands.Count == 0) return;

        const double DeadlineMs = 1_000;
        const int PollMs = 50;

        // Real elapsed time, not the sum of the sleeps we asked for.
        //
        // This loop used to count `waited += 50` per iteration and stop at a thousand,
        // which is a limit of twenty iterations wearing a deadline's clothes. When
        // something inside an iteration was slow the loop had no idea: a start that
        // took eleven and a half seconds reported "work area settled after 200 ms",
        // because two hundred was all it had ever been counting.
        long started = Stopwatch.GetTimestamp();

        bool changed = false;
        int stable = 0;

        while (Since(started) < DeadlineMs)
        {
            Thread.Sleep(PollMs);

            // Enumerated once per pass and handed to both, rather than each asking the
            // display configuration for itself - twenty iterations of that is forty
            // full enumerations at every start.
            IReadOnlyList<MonitorInfo> monitors = MonitorSource.Enumerate();

            if (MonitorLayoutChanged(monitors))
            {
                long syncing = Stopwatch.GetTimestamp();

                SyncMonitors(monitors);

                // Narrows a slow settle to its cause. Reconciling monitors republishes
                // every workspace, so it is the expensive half and the one worth timing.
                if (Since(syncing) >= 50)
                    Log.Debug(LogCategory.Monitor, $"monitor sync took {Since(syncing):F0} ms");

                changed = true;
                stable = 0;
                continue;
            }

            // Waits for the change, then for it to stop: returning on stillness alone
            // would return immediately, before the bar had registered anything, which
            // is the state this exists to avoid.
            if (changed && ++stable >= 2)
            {
                Log.Debug(LogCategory.Monitor, $"work area settled after {Since(started):F0} ms");
                return;
            }
        }

        // Neither outcome was reported before. Falling out of the loop having seen the
        // work area move but never settle is the interesting case, and it said nothing
        // at all - so a bar that kept resizing itself looked exactly like a bar that
        // had never registered.
        Log.Debug(
            LogCategory.Monitor,
            changed
                ? $"work area still moving after {Since(started):F0} ms; laying out anyway"
                : "work area unchanged after startup commands");
    }

    // ---- plumbing ----------------------------------------------------------

    private void Publish(WmResult result)
    {
        if (result.Events.Count == 0) return;

        // Corrected after the batch rather than inside it, so the state machine is not
        // mutated while its own events are being walked.
        bool undeclaredMode = false;

        foreach (WmEvent wmEvent in result.Events)
        {
            // Binding mode lives in two places: the state machine, which reports it,
            // and the lookup table, which enforces it.
            if (wmEvent is BindingModeChanged mode)
            {
                if (_bindings.SetMode(mode.Mode))
                {
                    ReportBindingMode(mode.Mode);

                    // A mode change is one of the moments a key stops being bound, which
                    // is precisely when a stranded swallow flag turns into a key the
                    // application believes is still held down.
                    _keyboard?.ForgetSwallowed();
                }
                else
                {
                    // No such mode. The table stays on the defaults, so the state
                    // machine must not be left claiming otherwise - it reported success,
                    // logged the mode as active, and the bar showed it, while every
                    // keystroke went on resolving against the default bindings.
                    Log.Warn(LogCategory.Hook,
                        $"binding mode '{mode.Mode}' is not declared in the config; " +
                        $"staying on the default bindings. Declared: " +
                        $"{(_bindings.ModeNames.Any() ? string.Join(", ", _bindings.ModeNames) : "none")}");

                    undeclaredMode = true;
                }
            }

            if (wmEvent is CommandRejected rejected)
            {
                // At info when the reason is that the window in front is not managed.
                // That refusal is the one a user meets by pressing a key and watching
                // nothing happen, with no way to tell whether the binding is broken,
                // the window is unusual, or the window manager has stopped. The others
                // are ordinary - focusing left from the leftmost window - and stay at
                // debug where they belong.
                if (rejected.Reason.Contains("is not managed", StringComparison.Ordinal))
                    Log.Info(LogCategory.Command, $"{rejected.Command}: {rejected.Reason}");
                else
                    Log.Debug(LogCategory.Command, $"rejected {rejected.Command}: {rejected.Reason}");
            }

            // A window that has just left or rejoined the tiling flow may be owed a
            // different border colour, and that is not a focus change, so nothing else
            // would repaint it - least of all for a window that is not the focused one.
            if (wmEvent is WindowStateChanged changed && _config.Effects.Enabled)
                RefreshBorderFor((nint)changed.Window.Handle);

            _ipc?.Publish(wmEvent.Topic, StateProjection.Payload(wmEvent, _wm));
        }

        // Only when something can actually have moved.
        //
        // Every event used to mark the layout dirty, which is correct and costs more
        // than it looks: a pending pass re-arranges the whole tree, reads the position
        // of every visible window, shortens the pump's wait from 250 ms to 7 ms, and
        // raises the system timer resolution to 1 ms. The last of those is machine-wide
        // rather than ours, so a window retitling itself several times a second - a
        // playing video, a terminal showing its directory - held the whole computer at
        // a fine timer for passes that could not move anything.
        if (result.Events.AffectGeometry()) _layoutDirty = true;

        // Why the pass is happening, recorded while it is still known. A workspace
        // switch and a layout change look different on purpose, and each has its own
        // duration and curve in the config.
        if (result.Events.LayoutAnimationKind() is { } animation) _pendingLayoutKind = animation;

        // One level of recursion, and it terminates: clearing the mode is always
        // accepted by the table, so this cannot come back here a second time.
        if (undeclaredMode) Publish(_wm.SetBindingMode(null));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Belt and braces: Run() restores on its way out, but Dispose is also
        // reached after an exception or a Ctrl+C that unwound differently. Restoring
        // twice is harmless; restoring never is not.
        try
        {
            RestoreConcealedWindows();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Window, "could not restore concealed windows", ex);
        }

        _keyboard?.Dispose();
        _winEvents?.Dispose();

        // Process-wide, so leaving it raised would outlive the reason for it.
        _timerResolution.Dispose();
        _loop.Dispose();

        if (_ipc is not null) _ipc.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
