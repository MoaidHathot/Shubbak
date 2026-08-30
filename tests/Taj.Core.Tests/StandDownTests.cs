using System.Globalization;
using Taj.Core;
using Taj.Core.Sources;

namespace Taj.Core.Tests;

/// <summary>
/// When the bar stops polling, and what happens to its sources while it has.
/// </summary>
/// <remarks>
/// <para>
/// The bar spends more CPU than the window manager it reports on - measured at
/// 46.9 ms against 31.2 ms over the same 25 seconds of an idle desktop - and it spent
/// exactly as much behind a full-screen game, redrawing a strip of screen that was
/// completely covered.
/// </para>
/// <para>
/// The risk in fixing that is a bar which stops and does not start again, so the
/// rule is stated here rather than left to the executable, which has no test project.
/// </para>
/// </remarks>
public sealed class StandDownTests
{
    // ---- the rule ----------------------------------------------------------

    [Fact]
    public void AVisibleBarOnAnOrdinaryDesktopKeepsWorking()
    {
        Assert.False(StandDown.ShouldStandDown(
            windowManagerSuspended: false, fullScreenApp: false, confirmed: false));
    }

    [Fact]
    public void ASuspendedWindowManagerIsEnoughOnItsOwn()
    {
        // It needs no confirming: the daemon said so about itself, over its own pipe.
        // And it is the case that matters most, because suspending is what someone
        // does before playing a game.
        Assert.True(StandDown.ShouldStandDown(
            windowManagerSuspended: true, fullScreenApp: false, confirmed: false));
    }

    [Fact]
    public void AFullScreenApplicationStandsTheBarDownWhenConfirmed()
    {
        Assert.True(StandDown.ShouldStandDown(
            windowManagerSuspended: false, fullScreenApp: true, confirmed: true));
    }

    [Fact]
    public void AnUnconfirmedFullScreenClaimIsNotEnough()
    {
        // The whole reason for the second signal. ABN_FULLSCREENAPP reports an opening
        // and a closing, not what is in front, so a claim nobody will confirm must not
        // be allowed to freeze the bar.
        Assert.False(StandDown.ShouldStandDown(
            windowManagerSuspended: false, fullScreenApp: true, confirmed: false));
    }

    // ---- the confirmation --------------------------------------------------

    [Theory]
    [InlineData(UserActivityKind.FullScreenGame)]
    [InlineData(UserActivityKind.FullScreenApp)]
    [InlineData(UserActivityKind.Presenting)]
    public void ActivitiesThatCoverTheScreenKeepTheBarDown(UserActivityKind activity)
    {
        Assert.True(StandDown.StillCovered(activity));
    }

    [Theory]
    [InlineData(UserActivityKind.Ordinary)]
    [InlineData(UserActivityKind.Unknown)]
    public void AnythingElseBringsTheBarBack(UserActivityKind activity)
    {
        // Unknown included, and deliberately. Standing back up on an answer nobody can
        // give costs the polling this exists to avoid; staying down costs a bar that
        // has stopped for a reason the user cannot see.
        Assert.False(StandDown.StillCovered(activity));
    }

    // ---- what it does to the sources ---------------------------------------

    [Fact]
    public void AStoodDownIntervalSourceStopsProducing()
    {
        int produced = 0;
        var source = new IntervalSource(
            "counter",
            TimeSpan.FromMilliseconds(50),
            () => Interlocked.Increment(ref produced).ToString(CultureInfo.InvariantCulture));

        source.Start();
        source.StandDown();

        int atStandDown = Volatile.Read(ref produced);
        Thread.Sleep(250);

        // Several intervals have passed and nothing ran. The first reading, taken by
        // Start, is the only one.
        Assert.Equal(atStandDown, Volatile.Read(ref produced));
    }

    [Fact]
    public void StandingUpTakesAReadingImmediately()
    {
        // The subtle half. A clock restarted on its ordinary schedule shows the time
        // it stopped at until its interval next elapses, so a bar returning from a
        // long game would come back showing a stale clock. The due-time on the way up
        // is zero for exactly this.
        //
        // Waited on rather than spun for. SpinWait.SpinUntil burns a core, and the
        // thing it is waiting for is a timer callback that needs a thread-pool thread
        // to run on - so on an agent with few cores the spin starves the work it is
        // waiting for and the test fails for reasons that have nothing to do with the
        // code. That is exactly how this behaved on CI while passing locally.
        var produced = new ManualResetEventSlim(false);

        var source = new IntervalSource(
            "counter", TimeSpan.FromSeconds(30), () => DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));

        source.Start();
        source.StandDown();

        // Subscribed after the stand-down, so only a reading taken on the way back up
        // can set it.
        source.Changed += _ => produced.Set();

        source.StandUp();

        Assert.True(
            produced.Wait(TimeSpan.FromSeconds(10)),
            "standing up left the source waiting out its interval, so the bar would show a stale value");
    }

    [Fact]
    public void APushSourceIsUnaffected()
    {
        // It must be. The window manager's own state arrives this way, and it is the
        // signal that ends a stand-down - a push source that stopped would leave the
        // bar unable to hear that it should start again.
        var source = new PushSource("status");
        source.Start();
        source.StandDown();

        source.Set("suspended");

        Assert.Equal("suspended", source.Value);
    }

    [Fact]
    public void StandingDownTwiceIsHarmless()
    {
        // The loop says it on a transition, but a transition is computed from two
        // signals arriving on two other threads, so saying it twice must cost nothing.
        var source = new IntervalSource("counter", TimeSpan.FromMilliseconds(50), () => "x");

        source.Start();
        source.StandDown();
        source.StandDown();
        source.StandUp();
        source.StandUp();
    }

    [Fact]
    public void ASourceNeverStartedCanStillBeToldEitherThing()
    {
        // Sources are created before the bar decides anything, and a stand-down can
        // arrive during startup.
        var source = new IntervalSource("counter", TimeSpan.FromMilliseconds(50), () => "x");

        source.StandDown();
        source.StandUp();
    }
}
