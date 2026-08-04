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
    /// <summary>The pump's frame interval, which the floor is defined in terms of.</summary>
    private const double FrameMs = 7;

    [Fact]
    public void AFrameIsNotDueBeforeTheIntervalHasPassed()
    {
        // The regression, stated as a number: the loop woke roughly every 0.8 ms and
        // committed a frame every time it woke.
        Assert.False(WmDaemon.IsFrameDue(0.81));
    }

    [Fact]
    public void AFrameIsDueOnceTheIntervalHasPassed()
    {
        Assert.True(WmDaemon.IsFrameDue(FrameMs));
        Assert.True(WmDaemon.IsFrameDue(FrameMs + 0.01));
    }

    [Fact]
    public void TheBoundaryIsInclusive()
    {
        // Exclusive would push every frame to the next wake. The pump asks for exactly
        // this interval, so the tick that arrives on time is the common case, not an
        // edge case - rejecting it would halve the frame rate.
        Assert.True(WmDaemon.IsFrameDue(FrameMs));
        Assert.False(WmDaemon.IsFrameDue(FrameMs - 0.01));
    }

    [Fact]
    public void ALateFrameIsStillDue()
    {
        // The floor must not become a schedule. A frame that missed its slot is
        // committed at the next opportunity rather than waiting for a multiple of the
        // interval to come round.
        Assert.True(WmDaemon.IsFrameDue(FrameMs * 3.5));
    }

    [Fact]
    public void TheFloorAndTheClampAgreeOnTheInterval()
    {
        // Both are defined in terms of the pump's frame interval and neither can see
        // it directly. If one is changed without the other, animations either stutter
        // or run at the wrong speed, and the two failures look nothing alike.
        Assert.Equal(FrameMs * 2, WmDaemon.ClampAnimationStep(1000));
        Assert.True(WmDaemon.IsFrameDue(FrameMs));
        Assert.False(WmDaemon.IsFrameDue(FrameMs / 2));
    }
}
