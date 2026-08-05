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
            Assert.Null(PowerThrottling.Failure);
        else
            Assert.False(string.IsNullOrWhiteSpace(PowerThrottling.Failure));
    }

    [Fact]
    public void ItIsSafeToCallMoreThanOnce()
    {
        // Called once from Program, but nothing enforces that, and an API that only
        // works the first time is a trap for whoever adds the second call site.
        PowerThrottling.OptOut();
        bool first = PowerThrottling.IsOptedOut;

        PowerThrottling.OptOut();

        Assert.Equal(first, PowerThrottling.IsOptedOut);
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
            $"power throttling opt-out was refused: {PowerThrottling.Failure}");
    }
}
