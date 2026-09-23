using System.Collections.Concurrent;
using Shubbak.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Companion;

/// <summary>
/// A message loop that waits rather than polls, and does other threads' work on
/// its own.
/// </summary>
/// <remarks>
/// <para>
/// The loop pumps the queue, runs whatever other threads have posted, gives the
/// program one pass, and then waits - for a message, for a post, or for a ceiling
/// to expire, whichever is first. <c>MsgWaitForMultipleObjectsEx</c> rather than a
/// sleep: a companion with nothing to do should cost nothing, and a poll would also
/// put up to its own interval in front of every keystroke and every repaint.
/// <c>MWMO_INPUTAVAILABLE</c> matters: without it, input that arrived between the
/// last peek and the wait is not counted as new and the thread sleeps through it.
/// </para>
/// <para>
/// <see cref="Post"/> is how a connection's pump thread reaches the windows, which
/// belong to this thread and must not be touched from another. The work is queued
/// and the loop woken; a post from the loop's own thread runs on its next pass, not
/// at once, which keeps re-entrancy out of every handler.
/// </para>
/// <para>
/// The ceiling is a safety net rather than a schedule. Every path that changes what
/// a companion shows should wake the loop, so the ceiling expires only when nothing
/// is happening - and exists because the failure it guards against, a signal added
/// later that nobody wires up, would otherwise show as a window that has quietly
/// stopped. A second of staleness is the better symptom.
/// </para>
/// </remarks>
public sealed class MessageLoop : IDisposable
{
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<Action> _inbox = new();
    private volatile bool _running = true;

    /// <summary>Whether <see cref="Run"/> is still going, or should be.</summary>
    public bool Running => _running;

    /// <summary>Asks the loop to end after its current pass. Safe from any thread.</summary>
    public void Stop()
    {
        _running = false;
        _wake.Set();
    }

    /// <summary>Wakes the loop so it makes a pass now rather than at the ceiling. Safe from any thread.</summary>
    public void Wake() => _wake.Set();

    /// <summary>Queues work for the loop's thread and wakes it. Safe from any thread.</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        _inbox.Enqueue(work);
        _wake.Set();
    }

    /// <summary>
    /// Runs until <see cref="Stop"/>, a <c>WM_QUIT</c>, or <paramref name="pass"/>
    /// returning false.
    /// </summary>
    /// <param name="pass">
    /// The program's own work, once per turn, after the queue and the inbox have been
    /// drained. Returns whether to carry on.
    /// </param>
    /// <param name="ceiling">
    /// The longest to wait when nothing wakes the loop, in milliseconds, asked each
    /// turn so a program can wait less while something is settling.
    /// </param>
    public void Run(Func<bool> pass, Func<uint> ceiling)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(ceiling);

        while (_running)
        {
            while (PInvoke.PeekMessage(out MSG msg, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                if (msg.message == PInvoke.WM_QUIT)
                {
                    _running = false;
                    break;
                }

                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }

            if (!_running) break;

            Drain();

            if (!_running) break;

            if (!pass())
            {
                _running = false;
                break;
            }

            if (!_running) break;

            Wait(ceiling());
        }
    }

    /// <summary>Runs everything posted since the last pass.</summary>
    private void Drain()
    {
        while (_inbox.TryDequeue(out Action? work))
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // One failed update must not take the process down. A window that
                // vanishes is worse than one that is briefly out of date.
                Log.Error(LogCategory.Ui, "work posted to the message loop failed", ex);
            }
        }
    }

    private void Wait(uint milliseconds)
    {
        PInvoke.MsgWaitForMultipleObjectsEx(
            [(HANDLE)_wake.SafeWaitHandle.DangerousGetHandle()],
            milliseconds,
            QUEUE_STATUS_FLAGS.QS_ALLINPUT,
            MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
    }

    public void Dispose() => _wake.Dispose();
}
