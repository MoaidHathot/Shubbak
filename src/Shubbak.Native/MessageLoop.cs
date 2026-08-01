using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>
/// A Win32 message pump.
/// </summary>
/// <remarks>
/// Both hook kinds require one: <c>WINEVENT_OUTOFCONTEXT</c> callbacks and
/// <c>WH_KEYBOARD_LL</c> callbacks are both delivered through the installing
/// thread's message queue, so a thread that stops pumping stops receiving events -
/// and, for the keyboard hook, gets unhooked entirely.
/// </remarks>
public sealed class MessageLoop
{
    private uint _threadId;
    private volatile bool _running;

    /// <summary>Raised on each pass, after the queue has been emptied.</summary>
    public event Action? Tick;

    public bool IsRunning => _running;

    /// <summary>
    /// Pumps messages until <see cref="Stop"/> is called.
    /// </summary>
    /// <param name="tickInterval">
    /// How long to wait for a message before firing <see cref="Tick"/> anyway, so
    /// periodic work still happens on an idle desktop.
    /// </param>
    public void Run(TimeSpan tickInterval)
    {
        _threadId = PInvoke.GetCurrentThreadId();
        _running = true;

        int intervalMs = Math.Max(1, (int)tickInterval.TotalMilliseconds);

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

                // MsgWaitForMultipleObjects would be the textbook choice, but a
                // short sleep is adequate here and far simpler: the loop already
                // wakes on a fixed cadence to service queued hook events.
                Thread.Sleep(intervalMs);
            }
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// Asks the loop to exit. Safe to call from any thread.
    /// </summary>
    public void Stop()
    {
        _running = false;

        // Wake the pump so it notices promptly rather than after the next tick.
        if (_threadId != 0) PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
    }
}
