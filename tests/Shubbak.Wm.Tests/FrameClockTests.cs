using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// The floor on how often an animation frame may be committed.
/// </summary>
/// <remarks>
/// <para>
/// The pump waits with <c>MsgWaitForMultipleObjectsEx</c> and <c>QS_ALLINPUT</c>, so
/// it returns on any queue activity and the interval it asks for is an upper bound.
/// Nothing enforced a lower one, and for a long time nothing had to: the commit call
/// sent to each target window's thread and blocked until that thread answered, so the
/// windows being moved were setting the pace.
/// </para>
/// <para>
/// Making that call asynchronous took 19x off the commit cost and removed the pace at
/// the same time. Frames went out at a p50 of 0.81 ms - about 1230 Hz against the
/// 143 Hz asked for. Each one carries <c>SWP_NOCOPYBITS</c> and so asks the target to
/// discard its client area and repaint, which no application can do a thousand times
/// a second: the window's geometry kept up and its content did not, leaving bare grey
/// where the content should have been.
/// </para>
/// </remarks>
public sealed class FrameClockTests
{
    /// <summary>
    /// A frame interval to state the rules in terms of - roughly 144 Hz.
    /// </summary>
    /// <remarks>
    /// Not the default any more. The rate is configuration now, so the floor takes it
    /// as an argument and these assertions name the rate they are about.
    /// </remarks>
    private const double FrameMs = 7;

    [Fact]
    public void TheFloorFollowsWhateverRateItIsGiven()
    {
        // The point of making the rate configurable: at 60 fps a gap of 10 ms is not
        // yet a frame, where at 144 it is two. A floor that had kept the old fixed
        // number would have gone on emitting frames at 143 Hz however the file was
        // configured, which is the flood this exists to prevent.
        const double SixtyFps = 1000.0 / 60;

        Assert.False(WmDaemon.IsFrameDue(10, SixtyFps));
        Assert.True(WmDaemon.IsFrameDue(10, FrameMs));
    }

    [Fact]
    public void AFrameIsNotDueBeforeTheIntervalHasPassed()
    {
        // The regression, stated as a number: the loop woke roughly every 0.8 ms and
        // committed a frame every time it woke.
        Assert.False(WmDaemon.IsFrameDue(0.81, FrameMs));
    }

    [Fact]
    public void AFrameIsDueOnceTheIntervalHasPassed()
    {
        Assert.True(WmDaemon.IsFrameDue(FrameMs, FrameMs));
        Assert.True(WmDaemon.IsFrameDue(FrameMs + 0.01, FrameMs));
    }

    [Fact]
    public void AWakeThatLandsSlightlyEarlyStillCounts()
    {
        // The pump expresses its timeout in whole milliseconds and a frame interval
        // usually is not one: 60 fps is 16.6666 ms, which truncated to 16 meant the
        // pump woke reliably just short of a frame being due, the frame was refused,
        // and the next came a whole cycle later. Measured, that was 30.52 ms between
        // frames against the 16.67 asked for - half the rate, from a third of a
        // millisecond.
        //
        // The pump now rounds its timeout up, but events wake it too, so the floor
        // tolerates a wake up to a millisecond early rather than spending a whole
        // interval on it.
        const double SixtyFps = 1000.0 / 60;

        Assert.True(WmDaemon.IsFrameDue(16, SixtyFps));
    }

    [Fact]
    public void AWakeThatIsProperlyEarlyDoesNotCount()
    {
        // The slack is a millisecond, not a licence. Half an interval early is the
        // flood this exists to prevent, not a rounding artefact.
        const double SixtyFps = 1000.0 / 60;

        Assert.False(WmDaemon.IsFrameDue(SixtyFps / 2, SixtyFps));
        Assert.False(WmDaemon.IsFrameDue(FrameMs - 2, FrameMs));
    }

    [Fact]
    public void ALateFrameIsStillDue()
    {
        // The floor must not become a schedule. A frame that missed its slot is
        // committed at the next opportunity rather than waiting for a multiple of the
        // interval to come round.
        Assert.True(WmDaemon.IsFrameDue(FrameMs * 3.5, FrameMs));
    }

    [Fact]
    public void TheFloorAndTheClampAgreeOnTheInterval()
    {
        // Both are defined in terms of the pump's frame interval and neither can see
        // it directly. If one is changed without the other, animations either stutter
        // or run at the wrong speed, and the two failures look nothing alike.
        Assert.Equal(FrameMs * 2, WmDaemon.ClampAnimationStep(1000, FrameMs));
        Assert.True(WmDaemon.IsFrameDue(FrameMs, FrameMs));
        Assert.False(WmDaemon.IsFrameDue(FrameMs / 2, FrameMs));
    }

    [Fact]
    public void AFreshFrameWaitsTheWholeInterval()
    {
        Assert.Equal(FrameMs, WmDaemon.RemainingUntilFrame(0, FrameMs).TotalMilliseconds, 6);
    }

    [Fact]
    public void AnInterruptedFrameWaitsOnlyWhatIsLeftOfIt()
    {
        // The bug this exists to fix. The pump is woken by keyboard and window events
        // as well as by its own timeout, and asking for a fresh interval after each of
        // those pushed the frame out by up to a whole one every time an event arrived.
        // During a workspace switch they arrive constantly.
        Assert.Equal(2, WmDaemon.RemainingUntilFrame(5, FrameMs).TotalMilliseconds, 6);
    }

    [Fact]
    public void AnOverdueFrameAsksForNoWaitRatherThanANegativeOne()
    {
        // A negative TimeSpan reaches the pump as an enormous unsigned timeout, which
        // would park the loop for weeks rather than running the frame it is late for.
        Assert.Equal(TimeSpan.Zero, WmDaemon.RemainingUntilFrame(FrameMs * 3, FrameMs));
        Assert.Equal(TimeSpan.Zero, WmDaemon.RemainingUntilFrame(FrameMs, FrameMs));
    }

    [Fact]
    public void AZeroWaitAlwaysMeansTheFrameIsDue()
    {
        // The two halves of the clock have to agree, and this is the direction that
        // matters: if the wait could reach zero while the floor still refused the
        // frame, the loop would ask for no wait, decline to emit, ask for no wait
        // again, and spin a core for the length of the animation.
        //
        // Swept rather than spot-checked, and across rates, because the floor carries
        // a millisecond of slack and the wait does not - so they agree by inequality
        // rather than by sharing a number, which is exactly the kind of agreement that
        // breaks silently when one of them is edited.
        foreach (double frameMs in new[] { 1000.0 / 240, 1000.0 / 144, 1000.0 / 90, 1000.0 / 60, 1000.0 / 15 })
        {
            for (double since = 0; since <= frameMs * 2; since += frameMs / 64)
            {
                bool noWait = WmDaemon.RemainingUntilFrame(since, frameMs) == TimeSpan.Zero;

                if (noWait)
                {
                    Assert.True(
                        WmDaemon.IsFrameDue(since, frameMs),
                        $"at {frameMs:F3} ms a frame, {since:F3} ms in: no wait but not due - the loop would spin");
                }
            }
        }
    }
}
