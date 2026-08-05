using Shubbak.Core.Animation;

namespace Shubbak.Config.Tests;

/// <summary>
/// How many frames a second the daemon aims to commit while anything is moving.
/// </summary>
/// <remarks>
/// <para>
/// This was a fixed 7 ms in the daemon, described as "roughly 144 Hz, the rate ADR
/// 0001 gates the animation path on" - which conflated the rate the design was proved
/// sound at with the rate it should ask for. Making it configurable moved the guess
/// from the program to the user without making it better informed.
/// </para>
/// <para>
/// Neither could know what the panel does. Asking is one call, and the answer is the
/// only one that is neither a guess nor an ambition: on a sixty hertz panel, asking
/// for ninety means half as many frames again as the display can present, discarded by
/// the compositor after every application has already been told to repaint.
/// </para>
/// </remarks>
public sealed class AnimationFpsTests
{
    [Fact]
    public void FollowingTheDisplayIsTheDefault()
    {
        // Null means "ask the display", which the daemon resolves. A number here would
        // be a guess about hardware nobody has looked at.
        ConfigLoadResult result = ConfigLoader.Load("animation { }");

        Assert.Null(result.Config.Animation.FramesPerSecond);
    }

    [Fact]
    public void TheDefaultSurvivesTheSectionBeingAbsent()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { }");

        Assert.Null(result.Config.Animation.FramesPerSecond);
    }

    [Fact]
    public void AutomaticCanBeAskedForExplicitly()
    {
        ConfigLoadResult result = ConfigLoader.Load("animation { fps \"auto\" }");

        Assert.False(result.HasErrors);
        Assert.Null(result.Config.Animation.FramesPerSecond);
    }

    [Fact]
    public void ANumberOverridesTheDisplay()
    {
        // Worth having on a very fast panel, where the applications rather than the
        // display are the limit.
        ConfigLoadResult result = ConfigLoader.Load("animation { fps 144 }");

        Assert.False(result.HasErrors);
        Assert.Equal(144, result.Config.Animation.FramesPerSecond);
    }

    [Fact]
    public void ThePeriodIsDerivedFromTheRate()
    {
        // What the frame clock actually consumes. Getting this inverted would run every
        // animation at the square of the intended rate, which is the kind of mistake
        // that is obvious in a test and baffling on a desktop.
        //
        // Three decimal places, not more: TimeSpan counts in hundred-nanosecond ticks,
        // so 1000/60 lands on 16.6666 rather than 16.66667.
        Assert.Equal(1000.0 / 60, AnimationOptions.PeriodFor(60).TotalMilliseconds, 3);
    }

    [Fact]
    public void TheRateAndThePeriodAgree()
    {
        // Stated as the round trip rather than as a constant, so it holds at every rate
        // rather than at the one that happened to be the default when it was written.
        foreach (int fps in new[] { 15, 30, 60, 120, 144, 240 })
            Assert.Equal(fps, 1000.0 / AnimationOptions.PeriodFor(fps).TotalMilliseconds, 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(5)]
    public void ARateBelowTheMinimumIsClampedAndReported(int fps)
    {
        // Zero would divide by zero in the period, and anything under about fifteen
        // reads as a series of jumps rather than movement.
        ConfigLoadResult result = ConfigLoader.Load($"animation {{ fps {fps} }}");

        Assert.Equal(AnimationOptions.MinimumFps, result.Config.Animation.FramesPerSecond);
        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0435");
    }

    [Fact]
    public void ARateAboveTheMaximumIsClampedAndReported()
    {
        ConfigLoadResult result = ConfigLoader.Load("animation { fps 1000 }");

        Assert.Equal(AnimationOptions.MaximumFps, result.Config.Animation.FramesPerSecond);
        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0435");
    }

    [Fact]
    public void ClampingIsAWarningRatherThanAnError()
    {
        // A frame rate is a preference. A configuration that is merely ambitious should
        // still start, and the warning says which rate was used so an ignored setting
        // does not look like an honoured one.
        ConfigLoadResult result = ConfigLoader.Load("animation { fps 1000 }");

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void SomethingThatIsNeitherANumberNorAutomaticIsAnError()
    {
        // Falling back silently would leave someone who wrote "display" or "native"
        // believing they had asked for something.
        ConfigLoadResult result = ConfigLoader.Load("animation { fps \"display\" }");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0437");
    }

    [Fact]
    public void ARateInsideTheRangeIsNotReported()
    {
        ConfigLoadResult result = ConfigLoader.Load("animation { fps 120 }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SHB0435");
    }

    [Fact]
    public void FpsIsAKnownSetting()
    {
        // The unknown-setting warning is on by default, so a setting added to the
        // loader without being added to the known list is reported to every user who
        // adopts it.
        ConfigLoadResult result = ConfigLoader.Load("animation { fps \"auto\" }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SHB0428");
    }
}
