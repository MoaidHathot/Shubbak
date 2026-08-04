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
    private bool _disposed;

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
    /// Wakes the pump. Safe to call from any thread, including a hook callback.
    /// </summary>
    /// <remarks>
    /// One <c>SetEvent</c>, which does not allocate and returns in about a
    /// microsecond - within what ADR 0001 constraint 1 allows inside the keyboard
    /// hook, and the reason a keystroke no longer waits for a timer.
    /// </remarks>
    public void Wake()
    {
        if (!_disposed) _wake.Set();
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

        PInvoke.MsgWaitForMultipleObjectsEx(
            1,
            &handle,
            timeout,
            QUEUE_STATUS_FLAGS.QS_ALLINPUT,
            MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
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
