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
    /// <summary>Whether the execution speed opt-out was applied.</summary>
    public static bool IsOptedOut { get; private set; }

    /// <summary>Why the execution speed opt-out failed, or null.</summary>
    public static string? OptOutFailure { get; private set; }

    /// <summary>
    /// Whether Windows was told to honour this process's timer resolution requests.
    /// </summary>
    public static bool HonorsTimerResolution { get; private set; }

    /// <summary>Why the timer resolution request failed, or null.</summary>
    public static string? TimerResolutionFailure { get; private set; }

    /// <summary>
    /// Asks Windows not to throttle this process's execution speed, and not to
    /// discard its timer resolution requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two calls rather than one combined mask, deliberately.
    /// <c>IGNORE_TIMER_RESOLUTION</c> only exists on Windows 11; an older build that
    /// rejects the unknown control bit would fail the whole call and take the EcoQoS
    /// opt-out down with it. Separately they fail separately, and <c>diagnose</c> can
    /// say which one. The cost is one extra syscall, once, at startup.
    /// </para>
    /// </remarks>
    public static void OptOut()
    {
        (IsOptedOut, OptOutFailure) =
            Apply(PInvoke.PROCESS_POWER_THROTTLING_EXECUTION_SPEED);

        (HonorsTimerResolution, TimerResolutionFailure) =
            Apply(PInvoke.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION);
    }

    /// <summary>
    /// Turns one throttling mechanism off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A control mask names the policy being set; a state mask of zero turns it off.
    /// Setting the control bit without the state bit is the documented way to say
    /// "this one, off" rather than "leave it to the system", which is what clearing
    /// both would mean.
    /// </para>
    /// <para>
    /// For <c>IGNORE_TIMER_RESOLUTION</c> the double negative is worth spelling out,
    /// because it is the whole point of the second call. The mechanism being turned
    /// off is Windows ignoring us, so turning it off means our
    /// <c>timeBeginPeriod</c> is honoured.
    /// </para>
    /// <para>
    /// That call is not redundant with <see cref="TimerResolution"/>, and deleting it
    /// silently halves the animation frame rate on a long-lived session. Since
    /// Windows 11, a process that is fully occluded, minimised, or otherwise
    /// invisible and inaudible to the user gets no guarantee of a resolution finer
    /// than the system default - and this daemon owns no visible window, so it is
    /// permanently in that category. <c>timeBeginPeriod</c> still returns success;
    /// only the guarantee goes away. That is why <c>IsHeld</c> can read True while
    /// waits overshoot by thirteen milliseconds, and why the honest signal is the
    /// tenth-percentile wake overshoot rather than anything this class reports.
    /// </para>
    /// <para>
    /// Order does not matter here. Windows remembers a resolution request made before
    /// the ignore mechanism was turned off and honours it retroactively, so this can
    /// sit at startup while <see cref="TimerResolution.Acquire"/> is called and
    /// released repeatedly for the life of the process.
    /// </para>
    /// </remarks>
    private static unsafe (bool Ok, string? Failure) Apply(uint controlMask)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = controlMask,

            // Must be 0. Setting this to controlMask turns the mechanism *on*, which
            // for IGNORE_TIMER_RESOLUTION means asking Windows to discard our timer
            // requests - the exact opposite of the intent, and the whole reason this
            // class has a second call.
            //
            // No test catches that inversion. It was tried: every test here still
            // passes with this set to controlMask, because SetProcessInformation
            // succeeds either way and the effect is a per-process guarantee that
            // nothing in-process can read back. NtQueryTimerResolution reports the
            // system-wide resolution, so it reads "fine" whenever any other process
            // on the machine is holding a fine timer - which is the same confounder
            // that hid this bug for weeks. A differential timing test fails the same
            // way for the same reason.
            //
            // So this line is verified against the sample in the SetProcessInformation
            // documentation and by the p10 wake overshoot on a long-lived daemon, and
            // by nothing else. Change it only with one of those in hand.
            StateMask = 0,
        };

        bool ok = PInvoke.SetProcessInformation(
            PInvoke.GetCurrentProcess(),
            PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
            &state,
            (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));

        // Not fatal, and not worth refusing to start over. Older builds and some
        // container configurations reject it, and the daemon runs perfectly well
        // throttled - it is simply less punctual, which is what the wake-overshoot
        // measurement is for.
        return ok
            ? (true, null)
            : (false, $"SetProcessInformation failed with error {Marshal.GetLastWin32Error()}");
    }
}
