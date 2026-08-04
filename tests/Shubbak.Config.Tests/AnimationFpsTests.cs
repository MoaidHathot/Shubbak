using Shubbak.Core.Animation;

namespace Shubbak.Config.Tests;

/// <summary>
/// How many frames a second the daemon aims to commit while anything is moving.
/// </summary>
/// <remarks>
/// <para>
/// This was a fixed 7 ms in the daemon, described as "roughly 144 Hz, the rate ADR
/// 0001 gates the animation path on" - which conflated the rate the design was proved
/// sound at with the rate it should ask for.
/// </para>
/// <para>
/// A window manager paints nothing. It repositions windows, and each application
/// repaints itself on its own thread at whatever rate it can manage. Past the point
/// where applications keep up, more frames do not buy smoother motion; they ask every
/// window being moved to discard and redraw its contents more often, and the ones that
/// cannot fall behind and show bare background where their content should be.
/// </para>
/// <para>
/// The first measurement of the shipping binary found it delivering 13 to 16 frames in
/// a 140 ms motion - about 100 Hz - whatever it asked for. Sixty therefore costs far
/// less in practice than the numbers suggest, and nearly halves the repaint load.
/// komorebi defaults to the same sixty.
/// </para>
/// </remarks>
public sealed class AnimationFpsTests
{
    [Fact]
    public void SixtyIsTheDefault()
    {
        ConfigLoadResult result = ConfigLoader.Load("animation { }");

        Assert.Equal(60, result.Config.Animation.FramesPerSecond);
    }

    [Fact]
    public void TheDefaultSurvivesTheSectionBeingAbsent()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { }");

        Assert.Equal(60, result.Config.Animation.FramesPerSecond);
    }

    [Fact]
    public void ItCanBeSet()
    {
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
        // so 1000/60 lands on 16.6666 rather than 16.66667. That is seven ten-millionths
        // of a millisecond per frame, or about a microsecond across a whole animation.
        var options = new AnimationOptions { FramesPerSecond = 60 };

        Assert.Equal(1000.0 / 60, options.FramePeriod.TotalMilliseconds, 3);
    }

    [Fact]
    public void TheRateAndThePeriodAgree()
    {
        // Stated as the round trip rather than as a constant, so it holds at every rate
        // rather than at the one that happened to be the default when it was written.
        foreach (int fps in new[] { 15, 30, 60, 120, 144, 240 })
        {
            var options = new AnimationOptions { FramesPerSecond = fps };

            Assert.Equal(fps, 1000.0 / options.FramePeriod.TotalMilliseconds, 2);
        }
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
        ConfigLoadResult result = ConfigLoader.Load("animation { fps 60 }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SHB0428");
    }
}
