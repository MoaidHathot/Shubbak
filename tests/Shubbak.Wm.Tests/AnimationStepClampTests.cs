using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// How much elapsed time a single tick may hand to the animation engine.
/// </summary>
/// <remarks>
/// <para>
/// The tick measures the real gap since the previous pass and adds it to every
/// in-flight animation. Nothing bounded it, and the pump waits a quarter of a second
/// when idle, so the sequence within one tick was: wait up to 250 ms, run the layout
/// pass, which creates an animation track with zero elapsed time, then add up to
/// 250 ms to it.
/// </para>
/// <para>
/// A window move is 140 ms by default. It therefore finished on its first frame and
/// the window teleported - but only when the tick that started it followed a long
/// wait, which is to say on the first action after the desktop had been idle. That
/// is what made it read as "the animations are flaky" rather than as a bug with a
/// cause.
/// </para>
/// </remarks>
public sealed class AnimationStepClampTests
{
    /// <summary>The pump's frame interval, which the clamp is defined in terms of.</summary>
    private const double FrameMs = 7;

    [Fact]
    public void AQuarterSecondIdleWaitCannotCollapseAnAnimation()
    {
        // The bug, stated as a number: 250 ms handed to a 140 ms animation on the tick
        // that created it finished it instantly.
        double step = WmDaemon.ClampAnimationStep(250);

        Assert.True(step < 140, $"a single step of {step} ms would complete a default window move");
        Assert.Equal(FrameMs * 2, step);
    }

    [Fact]
    public void AnOrdinaryFrameIsPassedThroughUntouched()
    {
        // The loop runs at roughly the frame interval when it has work, and that must
        // reach the engine unchanged or every animation runs slow.
        Assert.Equal(7, WmDaemon.ClampAnimationStep(7));
        Assert.Equal(3.5, WmDaemon.ClampAnimationStep(3.5));
        Assert.Equal(0, WmDaemon.ClampAnimationStep(0));
    }

    [Fact]
    public void OneMissedFrameIsStillCaughtUp()
    {
        // Two frames of slack, so a single dropped pass is absorbed rather than
        // stretching the animation. Beyond that it stretches, which is invisible.
        Assert.Equal(FrameMs * 2, WmDaemon.ClampAnimationStep(FrameMs * 2));
        Assert.Equal(FrameMs * 2, WmDaemon.ClampAnimationStep(FrameMs * 3));
    }

    [Fact]
    public void ALongStallStretchesTheAnimationRatherThanEndingIt()
    {
        // A 140 ms animation advanced in clamped steps still takes at least ten of
        // them, however badly the loop was starved. Slower is a blemish; instant is a
        // window appearing somewhere it never travelled to.
        const double DefaultWindowMoveMs = 140;

        double step = WmDaemon.ClampAnimationStep(5_000);
        int frames = (int)Math.Ceiling(DefaultWindowMoveMs / step);

        Assert.True(frames >= 10, $"only {frames} frames for a default window move");
    }
}
