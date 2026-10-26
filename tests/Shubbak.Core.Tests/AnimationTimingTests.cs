using Shubbak.Core.Animation;
using Shubbak.Core.Geometry;

namespace Shubbak.Core.Tests;

public sealed class AnimationTimingTests
{
    private static readonly Rect From = new(0, 0, 100, 100);
    private static readonly Rect To = new(0, 0, 1500, 100);
    private static AnimationEngine Engine() => new(new AnimationOptions
    {
        WindowMove = new AnimationProfile(TimeSpan.FromMilliseconds(140), Easing.Linear),
    });

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void StartingAfterIdleMovesOnTheFirstFrameWithoutTeleporting(int fps)
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);

        Assert.Equal(1, engine.Tick(5000, frames, 1000.0 / fps));
        Assert.True(frames[0].Rect.Width > From.Width);
        Assert.True(frames[0].Rect.Width < To.Width);
        Assert.False(frames[0].IsFinal);
    }

    [Fact]
    public void AnInFlightMotionFinishesWhenItsDurationHasElapsed()
    {
        // The old daemon clamped 100 ms to two frames. After steps totaling 140 ms
        // the engine had advanced only 72 ms and was still barely halfway there.
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);
        engine.Tick(0, frames, 16);
        engine.Tick(100, frames, 16);
        engine.Tick(24, frames, 16);

        Assert.False(engine.IsAnimating);
        Assert.True(frames[0].IsFinal);
        Assert.Equal(To, frames[0].Rect);
    }

    [Fact]
    public void ALongStallFinishesOldTracksButNotOneStartedOnTheSamePass()
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[2];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);
        engine.Tick(0, frames, 16);
        engine.Retarget(2, From, To, AnimationKind.WindowMove);

        Assert.Equal(2, engine.Tick(5000, frames, 16));
        Assert.Equal(new AnimationFrame(1, To, true, false), frames[0]);
        Assert.Equal(2, frames[1].Handle);
        Assert.False(frames[1].IsFinal);
        Assert.True(frames[1].Rect.Width > From.Width);
        Assert.Equal(1, engine.ActiveCount);
    }

    [Fact]
    public void RetargetingOneTrackDoesNotResetAnotherTracksClock()
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[2];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);
        engine.Retarget(2, From, To, AnimationKind.WindowMove);
        engine.Tick(0, frames, 16);
        engine.Tick(40, frames, 16);

        Assert.True(engine.TryGetCurrent(2, out Rect current));
        var further = new Rect(0, 0, 2900, 100);
        engine.Retarget(2, current, further, AnimationKind.WindowMove);
        engine.Tick(100, frames, 16);

        Assert.True(frames[0].IsFinal);
        Assert.Equal(To, frames[0].Rect);
        Assert.False(frames[1].IsFinal);
        Assert.InRange(frames[1].Rect.Width, current.Width + 1, further.Width - 1);

        engine.Tick(124, frames, 16);
        Assert.False(engine.IsAnimating);
        Assert.Equal(further, frames[0].Rect);
    }

    [Fact]
    public void AZeroInitialStepDoesNotKeepATrackUnstartedForever()
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);
        Assert.Equal(0, engine.Tick(5000, frames, 0));
        engine.Tick(140, frames, 0);
        Assert.False(engine.IsAnimating);
        Assert.Equal(To, frames[0].Rect);
    }

    [Fact]
    public void ANewMotionAfterThePreviousOneEndedHasItsOwnFirstFrame()
    {
        AnimationEngine engine = Engine();
        var frames = new AnimationFrame[1];
        engine.Retarget(1, From, To, AnimationKind.WindowMove);
        engine.Tick(0, frames, 16);
        engine.Tick(140, frames, 16);
        engine.Retarget(1, To, From, AnimationKind.WindowMove);
        engine.Tick(60_000, frames, 16);

        Assert.True(engine.IsAnimating);
        Assert.InRange(frames[0].Rect.Width, From.Width + 1, To.Width - 1);
    }
}
