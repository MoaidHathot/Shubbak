using Windows.Win32;

namespace Shubbak.InputProbe;

/// <summary>A bounded high-resolution wait, without changing global timer resolution.</summary>
internal sealed unsafe class Pacer : IDisposable
{
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _timer = PInvoke.CreateWaitableTimerEx(
        null, null, (uint)PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, 0x001F0003);

    public Pacer()
    {
        if (_timer.IsInvalid) throw new InvalidOperationException("High-resolution pacing timer unavailable.");
    }

    public void Wait(int microseconds)
    {
        long due = -(long)microseconds * 10;
        if (!PInvoke.SetWaitableTimerEx(_timer, in due, 0, null, null, null, 0))
            throw new InvalidOperationException("Could not arm pacing timer.");
        if (PInvoke.WaitForSingleObject(_timer, 2000) != Windows.Win32.Foundation.WAIT_EVENT.WAIT_OBJECT_0)
            throw new TimeoutException("Pacing timer did not fire.");
    }

    public void Dispose() => _timer.Dispose();
}
