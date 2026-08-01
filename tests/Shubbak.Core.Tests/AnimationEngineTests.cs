using Shubbak.Core.Animation;
using Shubbak.Core.Geometry;

namespace Shubbak.Core.Tests;

/// <summary>Tests for the animation engine.</summary>
/// <remarks>
/// The engine is deliberately time-free in the sense that the caller supplies the
/// delta, so these run deterministically with no sleeping and no wall-clock
/// dependence.
/// </remarks>
public sealed class AnimationEngineTests
{
    private static readonly Rect Start = new(0, 0, 100, 100);
    private static readonly Rect End = new(400, 300, 200, 150);

    private static AnimationEngine Create(AnimationOptions? options = null) =>
        new(options ?? AnimationOptions.Default with
        {
            WindowMove = new AnimationProfile(TimeSpan.FromMilliseconds(100), Easing.Linear),
        });

    private static Rect RunToCompletion(AnimationEngine engine, long handle, double step = 16.0)
    {
        Span<AnimationFrame> frames = stackalloc AnimationFrame[8];
        Rect last = default;

        for (int i = 0; i < 100 && engine.IsAnimating; i++)
        {
            int count = engine.Tick(step, frames);
            for (int j = 0; j < count; j++)
                if (frames[j].Handle == handle) last = frames[j].Rect;
        }

        return last;
    }

    [Fact]
    public void AnimationReachesTheTargetExactly()
    {
        // Rounding must never leave a window a pixel short of where the layout said
        // it belongs; the error would be visible against a neighbouring tile.
        AnimationEngine engine = Create();

        Assert.True(engine.Retarget(1, Start, End, AnimationKind.WindowMove));
        Assert.Equal(End, RunToCompletion(engine, 1));
        Assert.False(engine.IsAnimating);
    }

    [Fact]
    public void InterpolationIsMonotonic()
    {
        AnimationEngine engine = Create();
        engine.Retarget(1, Start, End, AnimationKind.WindowMove);

        Span<AnimationFrame> frames = stackalloc AnimationFrame[4];
        int previousX = Start.X;

        while (engine.IsAnimating)
        {
            int count = engine.Tick(10, frames);
            if (count == 0) break;

            Assert.True(frames[0].Rect.X >= previousX, "window moved backwards mid-animation");
            previousX = frames[0].Rect.X;
        }
    }

    [Fact]
    public void RetargetingBlendsFromTheCurrentPositionNotTheOriginal()
    {
        // Layout changes arrive faster than animations complete - opening three
        // windows retargets every tile twice. Restarting from the old origin would
        // make windows visibly jump backwards.
        AnimationEngine engine = Create();
        engine.Retarget(1, Start, End, AnimationKind.WindowMove);

        Span<AnimationFrame> frames = stackalloc AnimationFrame[4];
        engine.Tick(50, frames);

        Rect midway = frames[0].Rect;
        Assert.True(midway.X > Start.X && midway.X < End.X);

        var newTarget = new Rect(800, 600, 300, 200);
        engine.Retarget(1, midway, newTarget, AnimationKind.WindowMove);

        engine.Tick(1, frames);

        // The next frame continues from where the window actually is.
        Assert.True(frames[0].Rect.X >= midway.X,
            $"jumped backwards from {midway.X} to {frames[0].Rect.X}");

        Assert.Equal(newTarget, RunToCompletion(engine, 1));
    }

    [Fact]
    public void NegligibleMovementsAreAppliedInstantly()
    {
        // Animating a three-pixel nudge costs a dozen frames and reads as lag.
        AnimationEngine engine = Create();

        bool animated = engine.Retarget(
            1, new Rect(0, 0, 100, 100), new Rect(3, 2, 100, 100), AnimationKind.WindowMove);

        Assert.False(animated);
        Assert.False(engine.IsAnimating);
    }

    [Fact]
    public void MovingToTheSamePlaceDoesNothing()
    {
        AnimationEngine engine = Create();
        Assert.False(engine.Retarget(1, Start, Start, AnimationKind.WindowMove));
    }

    [Fact]
    public void DisablingAnimationAppliesEverythingInstantly()
    {
        var engine = new AnimationEngine(AnimationOptions.Disabled);

        Assert.False(engine.Retarget(1, Start, End, AnimationKind.WindowMove));
        Assert.False(engine.IsAnimating);
    }

    [Fact]
    public void ManyWindowsAnimateIndependently()
    {
        AnimationEngine engine = Create();

        for (long handle = 1; handle <= 20; handle++)
            engine.Retarget(handle, Start, new Rect((int)handle * 50, 0, 100, 100), AnimationKind.WindowMove);

        Assert.Equal(20, engine.ActiveCount);

        Span<AnimationFrame> frames = stackalloc AnimationFrame[32];
        int count = engine.Tick(16, frames);

        Assert.Equal(20, count);

        // Every handle appears exactly once per frame.
        Assert.Equal(20, frames[..count].ToArray().Select(f => f.Handle).Distinct().Count());
    }

    [Fact]
    public void FinishedTracksAreDroppedWithoutDisturbingTheRest()
    {
        var engine = new AnimationEngine(new AnimationOptions
        {
            WindowMove = new AnimationProfile(TimeSpan.FromMilliseconds(100), Easing.Linear),
            WindowOpen = new AnimationProfile(TimeSpan.FromMilliseconds(400), Easing.Linear),
        });

        engine.Retarget(1, Start, End, AnimationKind.WindowMove);   // finishes first
        engine.Retarget(2, Start, End, AnimationKind.WindowOpen);   // still running

        Span<AnimationFrame> frames = stackalloc AnimationFrame[8];

        for (int i = 0; i < 10; i++) engine.Tick(16, frames);

        Assert.Equal(1, engine.ActiveCount);
        Assert.True(engine.TryGetCurrent(2, out _));
        Assert.False(engine.TryGetCurrent(1, out _));
    }

    [Fact]
    public void RemovingAWindowStopsItsAnimation()
    {
        AnimationEngine engine = Create();
        engine.Retarget(1, Start, End, AnimationKind.WindowMove);
        engine.Retarget(2, Start, End, AnimationKind.WindowMove);

        engine.Remove(1);

        Assert.Equal(1, engine.ActiveCount);
        Assert.True(engine.TryGetCurrent(2, out _));
    }

    [Fact]
    public void TheFinalFrameIsFlagged()
    {
        // The committer uses this to drop the window from its driving set, which is
        // what re-enables feedback suppression cleanly.
        AnimationEngine engine = Create();
        engine.Retarget(1, Start, End, AnimationKind.WindowMove);

        Span<AnimationFrame> frames = stackalloc AnimationFrame[4];
        bool sawFinal = false;

        while (engine.IsAnimating)
        {
            int count = engine.Tick(16, frames);
            for (int i = 0; i < count; i++)
                if (frames[i].IsFinal) sawFinal = true;
        }

        Assert.True(sawFinal);
    }

    [Fact]
    public void WidthAndHeightNeverGoNegative()
    {
        // An overshooting curve can drive an interpolated dimension below zero,
        // which Win32 handles badly.
        var engine = new AnimationEngine(new AnimationOptions
        {
            WindowMove = new AnimationProfile(TimeSpan.FromMilliseconds(100), Easing.EaseOutBack),
        });

        engine.Retarget(1, new Rect(0, 0, 500, 400), new Rect(0, 0, 10, 10), AnimationKind.WindowMove);

        Span<AnimationFrame> frames = stackalloc AnimationFrame[4];

        while (engine.IsAnimating)
        {
            int count = engine.Tick(8, frames);
            for (int i = 0; i < count; i++)
            {
                Assert.True(frames[i].Rect.Width >= 0);
                Assert.True(frames[i].Rect.Height >= 0);
            }
        }
    }

    // ---- easing ------------------------------------------------------------

    [Theory]
    [InlineData("linear")]
    [InlineData("ease-out")]
    [InlineData("ease-in-out")]
    [InlineData("ease-out-back")]
    [InlineData("ease-out-expo")]
    public void EveryNamedCurveIsAnchoredAtBothEnds(string name)
    {
        // A curve that does not start at 0 and end at 1 makes windows teleport at
        // the start or stop short at the end.
        Assert.True(Easing.TryParse(name, out Easing easing));

        Assert.Equal(0, easing.Evaluate(0), 1e-6);
        Assert.Equal(1, easing.Evaluate(1), 1e-6);
    }

    [Fact]
    public void LinearIsTheIdentity()
    {
        Assert.Equal(0.25, Easing.Linear.Evaluate(0.25), 1e-6);
        Assert.Equal(0.5, Easing.Linear.Evaluate(0.5), 1e-6);
    }

    [Fact]
    public void EaseOutCoversMostOfTheDistanceEarly()
    {
        // This is why ease-out reads as responsive: half the time has passed but
        // well over half the journey is done.
        Assert.True(Easing.EaseOut.Evaluate(0.5) > 0.6);
    }

    [Fact]
    public void EaseOutBackOvershootsBeforeSettling()
    {
        bool overshot = false;

        for (double t = 0.5; t < 1.0; t += 0.01)
            if (Easing.EaseOutBack.Evaluate(t) > 1.0) overshot = true;

        Assert.True(overshot, "ease-out-back should exceed 1 before settling");
        Assert.Equal(1, Easing.EaseOutBack.Evaluate(1), 1e-6);
    }

    [Fact]
    public void UnknownCurveNamesFallBackToEaseOut()
    {
        Assert.False(Easing.TryParse("nonsense", out Easing easing));
        Assert.Equal(Easing.EaseOut, easing);
    }
}
