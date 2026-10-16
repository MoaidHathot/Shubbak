using Shubbak.Core.Wm;

namespace Shubbak.Native;

/// <summary>
/// Asks the shell what the user is doing, on a thread of its own, so that the answer
/// is always to hand and the question never holds up the window manager.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DisplayPreferences.CurrentActivity"/> is <c>SHQueryUserNotificationState</c>,
/// and that is a call into the shell. It answers in a few hundred microseconds while
/// the shell is idle and for as long as the shell takes otherwise: measured at
/// <b>436 ms</b> on the tick that happened to coincide with a File Explorer window
/// closing, and at 16 ms per call for the second after, while the shell was tearing
/// the window down. It was asked from the monitor poll on the window manager's own
/// thread every two seconds, so about one Explorer close in five stalled the tick -
/// and with it every animation frame and every keystroke - for a third of a second,
/// with nothing in the report to say where the time had gone. A slow-tick line
/// finally did.
/// </para>
/// <para>
/// The answer is reported rather than acted on - an event when it changes and a line
/// in the state snapshot - so it does not need to be fresh to the millisecond. A
/// background thread asks at the same two-second cadence and leaves the latest
/// answer in a field; the tick reads the field. The thread is below normal priority
/// and idle between asks, and whatever the shell does with the question, it does to
/// that thread.
/// </para>
/// </remarks>
public sealed class UserActivityProbe : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;

    /// <summary>The latest answer, or -1 before the first one has arrived.</summary>
    private int _latest = -1;

    public UserActivityProbe(TimeSpan? interval = null) =>
        _interval = interval ?? TimeSpan.FromSeconds(2);

    /// <summary>
    /// The shell's most recent answer, or <see langword="null"/> if it has not answered
    /// yet - which callers treat as "not asked", not as any particular activity.
    /// </summary>
    public UserActivity? Current
    {
        get
        {
            int value = Volatile.Read(ref _latest);
            return value < 0 ? null : (UserActivity)value;
        }
    }

    /// <summary>Starts asking. Idempotent.</summary>
    public void Start()
    {
        if (_thread is not null) return;

        _thread = new Thread(Run)
        {
            Name = "Shubbak user activity probe",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();
    }

    private void Run()
    {
        do
        {
            try
            {
                Volatile.Write(ref _latest, (int)DisplayPreferences.CurrentActivity());
            }
            catch (Exception)
            {
                // A failed ask is a stale answer, which the next ask replaces. Nothing
                // on this thread may take the process down.
            }
        }
        while (!_stop.Wait(_interval));
    }

    public void Dispose()
    {
        _stop.Set();

        // Bounded: the thread may be inside the shell call, and the process is
        // leaving regardless.
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        _stop.Dispose();
    }
}
