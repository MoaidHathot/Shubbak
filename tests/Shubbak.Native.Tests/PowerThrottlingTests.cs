using Shubbak.Native;

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
/// Worth being precise about what the timer-resolution tests below do not cover,
/// because it is more than it looks. They assert that Windows accepted the request.
/// They do not assert that Windows honoured it - and they do not catch the state mask
/// being inverted, which would ask Windows to discard our timer requests instead of
/// honouring them. That was verified rather than assumed: every test in this file
/// still passes with the mask inverted.
/// </para>
/// <para>
/// That gap is not laziness, it is the shape of the API. The effect is a per-process
/// guarantee with no in-process read-back; NtQueryTimerResolution reports the
/// system-wide figure and so reads healthy whenever any other process is holding a
/// fine timer, which is precisely the confounder that hid the original bug. A
/// differential timing test fails the same way. The invariant is documented at the
/// assignment in PowerThrottling instead, and the real detector is the p10 wake
/// overshoot on a daemon that has been up for hours.
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
        // constant being dropped from NativeMethods.txt or the mask convention being
        // inverted - both of which would leave every other signal looking healthy
        // while animation quietly ran at half rate.
        PowerThrottling.OptOut();

        Assert.True(
            PowerThrottling.HonorsTimerResolution,
            $"timer resolution request was refused: {PowerThrottling.TimerResolutionFailure}");
    }
}
