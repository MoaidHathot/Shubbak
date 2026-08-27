using System.Diagnostics;
using Shubbak.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>
/// A Win32 message pump.
/// </summary>
/// <remarks>
/// <para>
/// Both hook kinds require one: <c>WINEVENT_OUTOFCONTEXT</c> callbacks and
/// <c>WH_KEYBOARD_LL</c> callbacks are both delivered through the installing
/// thread's message queue, so a thread that stops pumping stops receiving events -
/// and, for the keyboard hook, gets unhooked entirely.
/// </para>
/// <para>
/// It waits rather than sleeps. A sleeping loop cannot do better than the system
/// timer, which is 15.6 ms unless something raises it: asking for 8 ms measured at
/// p50 15.50 ms, about 65 ticks a second, with the tick body itself taking 0.00 ms.
/// Everything downstream inherited that floor - a 140 ms animation got nine frames
/// where the design assumed twenty, and a keystroke already in the ring waited up to
/// a full sleep before anything looked at it.
/// </para>
/// <para>
/// Waiting on an event fixes both ends at once. There is nothing to do when nothing
/// has happened, so the idle case waits indefinitely and costs no wakeups at all;
/// anything that queues work signals the event and the next pass begins immediately.
/// </para>
/// </remarks>
public sealed class MessageLoop : IDisposable
{
    /// <summary>
    /// Signalled by whoever queues work, so the pump does not wait out its timeout.
    /// </summary>
    /// <remarks>
    /// Auto-reset: each signal releases exactly one pass, and a signal arriving while
    /// the pass is already running is remembered rather than lost.
    /// </remarks>
    private readonly AutoResetEvent _wake = new(false);

    private uint _threadId;
    private volatile bool _running;

    /// <summary>
    /// Set before <see cref="_wake"/> is disposed, so <see cref="Wake"/> mostly
    /// avoids touching a disposed handle.
    /// </summary>
    /// <remarks>
    /// Volatile because it is written on the thread calling <see cref="Dispose"/> and
    /// read on the keyboard hook thread, which without it has no guarantee of ever
    /// seeing the write. It narrows the window rather than closing it - see
    /// <see cref="Wake"/> for why the check alone is not enough.
    /// </remarks>
    private volatile bool _disposed;

    /// <summary>Raised on each pass, after the queue has been emptied.</summary>
    public event Action? Tick;

    /// <summary>
    /// How long the next pass may wait, asked after each tick.
    /// </summary>
    /// <remarks>
    /// The loop cannot know whether anything is in flight; the daemon can. Returning
    /// <see cref="Timeout.InfiniteTimeSpan"/> means "nothing is pending, wake me when
    /// something happens", which is the normal state of a desktop nobody is touching.
    /// </remarks>
    public Func<TimeSpan>? NextTimeout { get; set; }

    public bool IsRunning => _running;

    /// <summary>
    /// Whether the wait that follows is pacing something, rather than idling.
    /// </summary>
    /// <remarks>
    /// Set by whoever supplies <see cref="NextTimeout"/>, because only they know what
    /// the interval they asked for is for. It exists to keep the two kinds of wait
    /// apart in <see cref="WakeOvershootPacing"/> and <see cref="WakeOvershootIdle"/>:
    /// they differ by an order of magnitude in both the interval requested and the
    /// resolution available, so a percentile over the two together describes neither.
    /// </remarks>
    public bool IsPacingFrames { get; set; }

    /// <summary>
    /// How much longer than asked a paced wait that ran to its timeout actually took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The measurement that says whether the fine timer resolution is really in
    /// effect. Windows' default timer granularity is 15.625 ms, so without it a
    /// request for 12 ms comes back at about 15.6 and one for 17 ms at about 31 -
    /// which reproduces every frame rate measured here from a cause that has nothing
    /// to do with the caller's arithmetic.
    /// </para>
    /// <para>
    /// Only waits that timed out are recorded. A wait cut short by a message or a
    /// signal says nothing about the clock.
    /// </para>
    /// </remarks>
    public LatencyStats WakeOvershootPacing { get; } = new(4096, "wake overshoot pacing");

    /// <summary>
    /// The same, for waits that were not pacing anything.
    /// </summary>
    /// <remarks>
    /// Kept apart rather than discarded, because it is the contrast that makes the
    /// other number legible: this is what coarse granularity looks like, measured on
    /// the same machine at the same time. Reported together, the single figure was
    /// dominated by these - long idle waits with the fine timer deliberately released
    /// - and read as though animation frames were arriving twelve milliseconds late.
    /// </remarks>
    public LatencyStats WakeOvershootIdle { get; } = new(4096, "wake overshoot idle");

    /// <summary>Waits that ran to their timeout.</summary>
    public long WaitsTimedOut => Interlocked.Read(ref _waitsTimedOut);

    /// <summary>
    /// Sees every message before it is dispatched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For thread messages, which have no window and so cannot be dispatched at all.
    /// <c>WM_HOTKEY</c> from a <c>RegisterHotKey</c> registered against a thread rather
    /// than a window is exactly that: <c>DispatchMessage</c> has no window procedure to
    /// hand it to and discards it silently, so a loop that only pumps would never see
    /// the one key a suspended window manager is still listening for.
    /// </para>
    /// <para>
    /// Observing rather than filtering, deliberately. The handler is told what arrived
    /// and the message is dispatched regardless, so nothing here can accidentally
    /// swallow a message something else depends on.
    /// </para>
    /// </remarks>
    public Action<uint, nuint, nint>? MessageReceived { get; set; }

    /// <summary>Waits cut short by a message or a signal arriving.</summary>
    /// <remarks>
    /// How often anything paced by the timeout is interrupted mid-interval. High
    /// against <see cref="WaitsTimedOut"/> means the loop is being driven by traffic
    /// rather than by its own clock.
    /// </remarks>
    public long WaitsInterrupted => Interlocked.Read(ref _waitsInterrupted);

    private long _waitsTimedOut;
    private long _waitsInterrupted;

    /// <summary>
    /// Wakes the pump. Safe to call from any thread, including a hook callback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One <c>SetEvent</c>, which does not allocate and returns in about a
    /// microsecond - within what ADR 0001 constraint 1 allows inside the keyboard
    /// hook, and the reason a keystroke no longer waits for a timer.
    /// </para>
    /// <para>
    /// The catch is not defensive padding. This is reached from the keyboard hook -
    /// <c>Callback</c> to <c>Enqueue</c> to <c>WorkQueued</c> to here - and the flag
    /// check is check-then-act: a keystroke arriving while <see cref="Dispose"/> runs
    /// can pass the check, lose the race, and call <c>Set</c> on a disposed handle.
    /// The resulting <see cref="ObjectDisposedException"/> would leave a managed
    /// exception propagating out of an <c>UnmanagedCallersOnly</c> callback into
    /// Win32, which is a dead process rather than a lost keystroke.
    /// </para>
    /// <para>
    /// So pressing a key at the moment the daemon shut down could crash it. Rare,
    /// because the window is a few instructions wide - and shutdown is exactly when
    /// somebody is holding the key combination that asked for it.
    /// </para>
    /// </remarks>
    public void Wake()
    {
        if (_disposed) return;

        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Disposal won the race. There is nothing left to wake, which is the
            // outcome the caller wanted anyway.
        }
    }

    /// <summary>
    /// Pumps messages until <see cref="Stop"/> is called.
    /// </summary>
    /// <param name="tickInterval">
    /// The longest a pass may wait when <see cref="NextTimeout"/> says nothing in
    /// particular. Periodic work still happens on an idle desktop.
    /// </param>
    public void Run(TimeSpan tickInterval)
    {
        // Set before _running, so a Stop racing startup finds a thread to post to
        // rather than silently doing nothing and leaving the loop unstoppable.
        _threadId = PInvoke.GetCurrentThreadId();
        _running = true;

        int defaultMs = Math.Max(1, (int)tickInterval.TotalMilliseconds);

        try
        {
            while (_running)
            {
                while (PInvoke.PeekMessage(out MSG msg, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
                {
                    if (msg.message == PInvoke.WM_QUIT)
                    {
                        _running = false;
                        return;
                    }

                    // Before dispatch, because a thread message never reaches a window
                    // procedure and dispatching it is where it would disappear.
                    MessageReceived?.Invoke(msg.message, msg.wParam, msg.lParam);

                    PInvoke.TranslateMessage(in msg);
                    PInvoke.DispatchMessage(in msg);
                }

                Tick?.Invoke();

                if (!_running) return;

                WaitForWork(NextTimeout?.Invoke() ?? tickInterval, defaultMs);
            }
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// Waits for a message, a signal, or the timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MSGWAIT</c> rather than a plain wait, because the queue has to stay serviced:
    /// a thread that waits on a handle alone stops taking hook callbacks and the
    /// keyboard hook is removed for it.
    /// </para>
    /// <para>
    /// <c>MWMO_INPUTAVAILABLE</c> covers the gap between the peek loop above and the
    /// wait below - without it, a message that arrived in between is not counted as
    /// new and the pass waits for something already sitting in the queue.
    /// </para>
    /// </remarks>
    private unsafe void WaitForWork(TimeSpan requested, int defaultMs)
    {
        // Rounded up, not truncated. The timeout is whole milliseconds and a frame
        // interval usually is not: 60 fps is 16.6666 ms, which truncated to 16 meant
        // the pump woke reliably just before a frame was due.
        //
        // Sleeping fractionally longer than asked costs a fraction of a frame.
        // Sleeping fractionally less costs an entire one, because whatever is being
        // paced by the timeout then waits out another whole interval - which is how a
        // third of a millisecond turned 60 fps into a measured 33.
        //
        // Not covered by a test here, deliberately: the loop runs at roughly the same
        // rate either way, so a pass count cannot see the difference. What the
        // truncation broke was the frame clock in the daemon, and that is where the
        // assertion lives. This is the other half of the fix, and correct on its own
        // terms - a wait must not return before it was asked to.
        uint timeout =
            requested == Timeout.InfiniteTimeSpan
                ? 0xFFFFFFFFu
                : (uint)Math.Clamp((int)Math.Ceiling(requested.TotalMilliseconds), 0, defaultMs * 1000);

        if (timeout == 0) return;

        HANDLE handle = (HANDLE)_wake.SafeWaitHandle.DangerousGetHandle();

        long before = Stopwatch.GetTimestamp();

        WAIT_EVENT result = PInvoke.MsgWaitForMultipleObjectsEx(
            1,
            &handle,
            timeout,
            QUEUE_STATUS_FLAGS.QS_ALLINPUT,
            MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);

        // The return value says why the wait ended, and was previously discarded.
        // Without it there is no way to tell a loop running late from a loop being
        // woken early, and those want opposite fixes.
        if (result == WAIT_EVENT.WAIT_TIMEOUT)
        {
            Interlocked.Increment(ref _waitsTimedOut);

            double elapsedMs = (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;

            // Bucketed by what the wait was for. Read together these two answer
            // opposite questions and cancel each other out.
            LatencyStats sink = IsPacingFrames ? WakeOvershootPacing : WakeOvershootIdle;

            sink.Record(elapsedMs - timeout);
        }
        else
        {
            Interlocked.Increment(ref _waitsInterrupted);
        }
    }

    /// <summary>
    /// Asks the loop to exit. Safe to call from any thread.
    /// </summary>
    public void Stop()
    {
        _running = false;

        // Both, because either alone can miss. The post is what unblocks a wait that
        // is already running; the signal is what stops a pass that has not reached
        // the wait yet from settling into one.
        if (_threadId != 0) PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);

        Wake();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _wake.Dispose();
    }
}

/// <summary>
/// Raises the system timer resolution for as long as it is held.
/// </summary>
/// <remarks>
/// <para>
/// Every wait with a timeout is quantised by the system timer, so asking to wake in
/// seven milliseconds gets fifteen just as a sleep does. Animation is the only thing
/// here that needs finer than that, and it needs it badly: at the default resolution
/// a frame budget shorter than 15.6 ms cannot be honoured at all.
/// </para>
/// <para>
/// Held only while something is actually moving. The resolution is process-wide and
/// raising it permanently defeats timer coalescing and the deeper idle states, which
/// for a process that runs all day is a real cost to pay for animations measured in
/// tenths of a second.
/// </para>
/// </remarks>
public sealed class TimerResolution : IDisposable
{
    private readonly uint _period;
    private bool _held;

    public TimerResolution(uint milliseconds = 1) => _period = Math.Max(1, milliseconds);

    /// <summary>Whether the finer resolution is currently held.</summary>
    public bool IsHeld => _held;

    public void Acquire()
    {
        if (_held) return;

        _held = PInvoke.timeBeginPeriod(_period) == 0;
    }

    public void Release()
    {
        if (!_held) return;

        _ = PInvoke.timeEndPeriod(_period);
        _held = false;
    }

    public void Dispose() => Release();
}
