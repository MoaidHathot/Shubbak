using Shubbak.Core.Animation;
using Shubbak.Core.Geometry;

namespace Shubbak.Core.Tests;

public sealed class AnimationRetargetTests
{
    private static readonly Rect From = new(0, 0, 100, 100);
    private static readonly Rect To = new(0, 0, 1100, 100);

    private static AnimationEngine Engine() => new(new AnimationOptions
    {
        WindowMove = new AnimationProfile(TimeSpan.FromMilliseconds(100), Easing.Linear),
        WindowOpen = new AnimationProfile(TimeSpan.FromMilliseconds(180), Easing.Linear),
    });

    [Fact]
    public void RepeatedLayoutsCannotExtendTheOriginalDeadline()
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);
        engine.Tick(0, frames, 20);

        for (int i = 0; i < 4; i++)
        {
            Assert.True(engine.TryGetCurrent(1, out Rect current));
            Assert.True(engine.Retarget(1, current, To, AnimationKind.WindowMove));
            engine.Tick(20, frames, 20);
        }

        Assert.False(engine.IsAnimating);
        Assert.Equal(new AnimationFrame(1, To, true, false), frames[0]);
    }

    [Theory]
    [InlineData("ease-out")]
    [InlineData("ease-out-back")]
    public void ARedundantLayoutPreservesTheCurveAsWellAsTheDeadline(string curve)
    {
        Assert.True(Easing.TryParse(curve, out Easing easing));
        AnimationEngine engine = Engine();
        engine.Options = engine.Options with { WindowMove = new AnimationProfile(TimeSpan.FromMilliseconds(100), easing) };
        var control = new AnimationEngine(engine.Options);
        var actual = new AnimationFrame[1];
        var expected = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);
        control.Retarget(1, From, To, AnimationKind.WindowMove);

        for (int i = 0; i < 10; i++)
        {
            int count = engine.Tick(10, actual, 10);
            Assert.Equal(control.Tick(10, expected, 10), count);
            if (count != 0) Assert.Equal(expected[0], actual[0]);

            if (engine.TryGetCurrent(1, out Rect current))
                Assert.True(engine.Retarget(1, current, To, AnimationKind.WindowMove));
        }

        Assert.False(engine.IsAnimating);
    }

    [Fact]
    public void AnUnchangedDestinationDoesNotReplaceAnOpeningProfileWithAMove()
    {
        AnimationEngine engine = Engine();
        var control = new AnimationEngine(engine.Options);
        var actual = new AnimationFrame[1];
        var expected = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowOpen);
        control.Retarget(1, From, To, AnimationKind.WindowOpen);
        engine.Tick(60, actual);
        control.Tick(60, expected);

        engine.Retarget(1, actual[0].Rect, To, AnimationKind.WindowMove);
        engine.Tick(30, actual);
        control.Tick(30, expected);
        Assert.Equal(expected[0], actual[0]);
    }

    [Fact]
    public void AChangedConfigurationStillUpdatesTheActiveProfile()
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowOpen);
        engine.Tick(60, frames);
        Rect current = frames[0].Rect;

        engine.Options = engine.Options with { WindowOpen = new AnimationProfile(TimeSpan.FromMilliseconds(40), Easing.Linear) };
        engine.Retarget(1, current, To, AnimationKind.WindowMove);
        engine.Tick(40, frames);

        Assert.False(engine.IsAnimating);
        Assert.Equal(To, frames[0].Rect);
    }

    [Theory]
    [InlineData(99.6)]
    [InlineData(99.99)]
    public void RoundingAndTheMinimumDistanceDoNotDiscardTheFinalFrame(double elapsed)
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);
        engine.Tick(elapsed, frames);

        // The application may still be applying an earlier frame. A final frame is
        // owed even if the engine's rounded rectangle is already at its destination.
        Assert.True(engine.Retarget(1, frames[0].Rect, To, AnimationKind.WindowMove));
        Assert.Equal(1, engine.Tick(100 - elapsed, frames));
        Assert.Equal(new AnimationFrame(1, To, true, false), frames[0]);
        Assert.False(engine.IsAnimating);
    }

    [Fact]
    public void AChangedCurveTakesEffectEvenWhenTheDurationIsUnchanged()
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);
        engine.Tick(20, frames);
        Rect current = frames[0].Rect;

        engine.Options = engine.Options with { WindowMove = new AnimationProfile(TimeSpan.FromMilliseconds(100), Easing.EaseOut) };
        var control = new AnimationEngine(engine.Options);
        var expected = new AnimationFrame[1];
        control.Retarget(1, current, To, AnimationKind.WindowMove);
        engine.Retarget(1, current, To, AnimationKind.WindowMove);

        control.Tick(30, expected);
        engine.Tick(30, frames);
        Assert.Equal(expected[0], frames[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledAnimationOrAnExplicitInstantRequestStillCancels(bool instant)
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);
        engine.Tick(20, frames);
        if (!instant) engine.Options = engine.Options with { Enabled = false };

        Assert.False(engine.Retarget(1, frames[0].Rect, To, instant ? AnimationKind.Resize : AnimationKind.WindowMove));
        Assert.False(engine.IsAnimating);
    }

    [Fact]
    public void SettingTheOriginalProfileToInstantCancelsItsMotion()
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowOpen);
        engine.Tick(20, frames);
        engine.Options = engine.Options with { WindowOpen = AnimationProfile.Instant };

        Assert.False(engine.Retarget(1, frames[0].Rect, To, AnimationKind.WindowMove));
        Assert.False(engine.IsAnimating);
    }
}
