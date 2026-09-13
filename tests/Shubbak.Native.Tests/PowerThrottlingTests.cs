using Shubbak.Native;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Shubbak.Native.Tests;

/// <summary>
/// Telling Windows this process is not background work.
/// </summary>
/// <remarks>
/// <para>
/// On a hybrid CPU a long-lived, mostly-idle process is what the scheduler moves onto
/// efficiency cores and what EcoQoS caps the clock of. Neither is announced and
/// neither is visible in a measurement of the work itself - only in when the work was
/// allowed to run, which is why the wake-overshoot figures exist.
/// </para>
/// <para>
/// There is not much to assert here without measuring the scheduler, which a unit test
/// cannot do. What it can do is catch the ways this silently becomes a no-op: a P/Invoke
/// signature that throws, a struct whose size the API rejects, and a call that fails
/// while reporting success.
/// </para>
/// <para>
/// Worth being precise about what the first six tests do not cover, because it is
/// more than it looks. They assert that Windows accepted the request. They do not
/// assert that Windows honoured it - and they do not catch the state mask being
/// inverted, which would ask Windows to discard our timer requests instead of
/// honouring them. That was verified rather than assumed: every one of them still
/// passes with the mask inverted, and <c>HonorsTimerResolution</c> reads True.
/// </para>
/// <para>
/// The last test is the one that does catch it, and for a while it was believed
/// impossible: the effect is a per-process guarantee with no in-process read-back, and
/// <c>NtQueryTimerResolution</c> reports the system-wide figure, so it reads healthy
/// whenever any other process is holding a fine timer - precisely the confounder that
/// hid the original bug. True of the query; not, it turns out, of the waits. Measured
/// on Windows 11 26200 with the system-wide figure at 1.00 ms throughout, a process
/// whose requests are being ignored still waits in 15.6 ms steps, and flipping the
/// mechanism shows on the very next wait. So the mechanism is switched on by hand,
/// the waits are watched going coarse, <see cref="PowerThrottling.OptOut"/> is
/// called, and the waits are watched coming back.
/// </para>
/// </remarks>
public sealed class PowerThrottlingTests
{
    [Fact]
    public void OptingOutSucceedsOrSaysWhyNot()
    {
        // The pair has to stay consistent, because `diagnose` reports both and a
        // "False" with no reason is a dead end for anyone reading it.
        PowerThrottling.OptOut();

        if (PowerThrottling.IsOptedOut)
            Assert.Null(PowerThrottling.OptOutFailure);
        else
            Assert.False(string.IsNullOrWhiteSpace(PowerThrottling.OptOutFailure));
    }

    [Fact]
    public void HonoringTimerResolutionSucceedsOrSaysWhyNot()
    {
        // Same contract as the execution-speed pair, and it matters more here: this
        // is the setting that decides whether holding a fine timer does anything, so
        // a bare "False" in `diagnose` would send the next reader to the timer code,
        // which is not where the problem would be.
        PowerThrottling.OptOut();

        if (PowerThrottling.HonorsTimerResolution)
            Assert.Null(PowerThrottling.TimerResolutionFailure);
        else
            Assert.False(string.IsNullOrWhiteSpace(PowerThrottling.TimerResolutionFailure));
    }

    [Fact]
    public void ItIsSafeToCallMoreThanOnce()
    {
        // Called once from Program, but nothing enforces that, and an API that only
        // works the first time is a trap for whoever adds the second call site.
        PowerThrottling.OptOut();
        bool first = PowerThrottling.IsOptedOut;
        bool firstTimer = PowerThrottling.HonorsTimerResolution;

        PowerThrottling.OptOut();

        Assert.Equal(first, PowerThrottling.IsOptedOut);
        Assert.Equal(firstTimer, PowerThrottling.HonorsTimerResolution);
    }

    [Fact]
    public void OneFailingCallDoesNotTakeTheOtherWithIt()
    {
        // The reason these are two calls rather than one combined control mask.
        // IGNORE_TIMER_RESOLUTION is Windows 11 only, so on an older build the second
        // call is expected to fail - and the first has to survive that. Asserting
        // independence rather than success keeps this meaningful on both.
        PowerThrottling.OptOut();

        Assert.True(
            PowerThrottling.IsOptedOut || PowerThrottling.OptOutFailure is not null,
            "the execution-speed result was left unset");

        Assert.True(
            PowerThrottling.HonorsTimerResolution || PowerThrottling.TimerResolutionFailure is not null,
            "the timer-resolution result was left unset");
    }

    [Fact]
    public void ItWorksOnThisMachine()
    {
        // Not a property of the code, and deliberately so: this asserts the call is
        // actually accepted by the Windows this is being built on. If it starts
        // failing, the opt-out has quietly stopped happening and the daemon is being
        // scheduled as background work again - which shows up nowhere else except as
        // waking late.
        PowerThrottling.OptOut();

        Assert.True(
            PowerThrottling.IsOptedOut,
            $"power throttling opt-out was refused: {PowerThrottling.OptOutFailure}");
    }

    [Fact]
    public void ItHonorsTimerResolutionOnThisMachine()
    {
        // Same shape, and the same caveat: this says Windows accepted the request on
        // this build, not that a fine resolution is in force. It is here to catch the
        // constant being dropped from NativeMethods.txt, which would leave every other
        // signal looking healthy while animation quietly ran at half rate. It does not
        // catch the mask convention being inverted - SetProcessInformation accepts
        // that just as happily - which is what the next test is for.
        PowerThrottling.OptOut();

        Assert.True(
            PowerThrottling.HonorsTimerResolution,
            $"timer resolution request was refused: {PowerThrottling.TimerResolutionFailure}");
    }

    [Fact]
    public void OptingOutMakesWindowsHonourTheResolutionAgain()
    {
        using var timer = new TimerResolution(1);

        timer.Acquire();

        Assert.True(timer.IsHeld, "timeBeginPeriod(1) was refused, so there is nothing to be ignored");

        try
        {
            // What this machine can do at all, before anything is asked of it. A virtual
            // machine, or a hosted runner sharing two cores with nine other test hosts,
            // may not deliver a 7 ms wait even with the fine timer held and nothing
            // ignoring it - and then there is nothing for the ignore bit to take away or
            // the opt-out to give back. Without this the next reading looked like the
            // mechanism biting when it was the machine being coarse, and the assertion
            // after it demanded a resolution the machine had never shown.
            int baseline = PassesIn300Ms();

            if (baseline <= 30) return;

            // Windows 10 has no such mechanism and rejects the control bit. Nothing to
            // observe there, and nothing at stake either: the heuristic this guards
            // against does not exist on that build.
            if (!SetIgnoreTimerResolution(on: true)) return;

            int ignored = PassesIn300Ms();

            // A build that accepts the bit but does not enforce it leaves no way to
            // tell the two settings apart, so the test says nothing rather than
            // something it cannot know. The system tick gives 19 or 20 in 300 ms;
            // anything close to that is the mechanism biting.
            if (ignored > 24) return;

            PowerThrottling.OptOut();

            int honoured = PassesIn300Ms();

            Assert.True(
                honoured > 30,
                $"Windows ignored the fine timer ({ignored} passes in 300 ms at 7 ms, against {baseline} before the bit was set) " +
                $"and OptOut did not make it honour it again ({honoured} passes); HonorsTimerResolution={PowerThrottling.HonorsTimerResolution}" +
                $"{(PowerThrottling.TimerResolutionFailure is { } why ? $" ({why})" : "")}");
        }
        finally
        {
            // Process-wide and sticky, so whatever happened above, the rest of the
            // suite runs in the state the daemon runs in. Idempotent.
            PowerThrottling.OptOut();
        }
    }

    /// <summary>
    /// Sets the mechanism the library only ever clears.
    /// </summary>
    /// <returns>False when this Windows does not know the control bit.</returns>
    private static unsafe bool SetIgnoreTimerResolution(bool on)
    {
        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PInvoke.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PInvoke.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
            StateMask = on ? PInvoke.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION : 0,
        };

        return PInvoke.SetProcessInformation(
            PInvoke.GetCurrentProcess(),
            PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
            &state,
            (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));
    }

    /// <summary>
    /// Runs the pump at a 7 ms timeout for 300 ms and counts its passes.
    /// </summary>
    /// <remarks>
    /// Through <see cref="MessageLoop"/> rather than a bare kernel wait, so the test
    /// follows whatever the daemon actually waits on.
    /// </remarks>
    private static int PassesIn300Ms()
    {
        using var loop = new MessageLoop();

        int passes = 0;

        loop.NextTimeout = () => TimeSpan.FromMilliseconds(7);
        loop.Tick += () => Interlocked.Increment(ref passes);

        var thread = new Thread(() => loop.Run(TimeSpan.FromMilliseconds(8))) { IsBackground = true };

        thread.Start();
        SpinWait.SpinUntil(() => loop.IsRunning, TimeSpan.FromSeconds(2));

        try
        {
            Thread.Sleep(300);
            return passes;
        }
        finally
        {
            loop.Stop();
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
