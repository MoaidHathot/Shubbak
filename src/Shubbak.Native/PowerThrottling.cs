using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Shubbak.Native;

/// <summary>
/// Tells Windows this process is not background work.
/// </summary>
/// <remarks>
/// <para>
/// On a hybrid CPU - every Intel part since Alder Lake, and Windows 11 schedules
/// for them - a long-lived process with low average CPU looks exactly like something
/// that belongs on the efficiency cores. That is the right default for most daemons
/// and the wrong one for this one, which has a keyboard hook with a 300 ms hard
/// deadline before Windows silently unhooks it, and a loop that wants to wake
/// accurately every seven to seventeen milliseconds.
/// </para>
/// <para>
/// EcoQoS is the same judgement applied to clock speed rather than core choice: it
/// caps the frequency of work it considers unimportant. Neither is announced, and
/// neither is visible in a profiler that measures the work rather than when the work
/// was allowed to run.
/// </para>
/// <para>
/// This is the same argument as <c>GCSettings.LatencyMode</c> in <c>Program</c>, and
/// it belongs beside it: both trade a resource the machine has plenty of for
/// predictable latency on a path the user is waiting on.
/// </para>
/// <para>
/// The cost is real and worth stating. Opting out means the scheduler stops
/// economising on a process that is idle most of the time, so on a laptop this is a
/// battery decision rather than a free win. It is reported in <c>diagnose</c> rather
/// than applied silently.
/// </para>
/// </remarks>
public static class PowerThrottling
{
    /// <summary>Whether the opt-out was applied.</summary>
    public static bool IsOptedOut { get; private set; }

    /// <summary>Why the opt-out failed, or null.</summary>
    public static string? Failure { get; private set; }

    /// <summary>
    /// Asks Windows not to throttle this process's execution speed.
    /// </summary>
    /// <remarks>
    /// A control mask names the policy being set; a state mask of zero turns it off.
    /// Setting the control bit without the state bit is the documented way to say
    /// "this one, off" rather than "leave it to the system", which is what clearing
    /// both would mean.
    /// </remarks>
    public static unsafe void OptOut()
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = 0,
        };

        bool ok = PInvoke.SetProcessInformation(
            PInvoke.GetCurrentProcess(),
            PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
            &state,
            (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));

        IsOptedOut = ok;

        // Not fatal, and not worth refusing to start over. Older builds and some
        // container configurations reject it, and the daemon runs perfectly well
        // throttled - it is simply less punctual, which is what the wake-overshoot
        // measurement is for.
        Failure = ok ? null : $"SetProcessInformation failed with error {Marshal.GetLastWin32Error()}";
    }
}
