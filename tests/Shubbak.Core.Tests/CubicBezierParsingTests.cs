using Shubbak.Core.Animation;

namespace Shubbak.Core.Tests;

/// <summary>
/// Writing a custom easing curve in a config.
/// </summary>
/// <remarks>
/// Easing stores CSS control points, and its own doc says why: "so curves can be
/// copied straight from any easing reference or design tool". The function that
/// builds one had no callers anywhere in the program, and TryParse - which is the
/// only route from a config file to a curve - recognised six names and nothing else.
/// The documented reason for the design was unreachable by the people it was made
/// for.
/// </remarks>
public sealed class CubicBezierParsingTests
{
    [Theory]
    [InlineData("cubic-bezier(0.34, 1.56, 0.64, 1)")]
    [InlineData("cubic-bezier(0.34,1.56,0.64,1)")]
    [InlineData("  cubic-bezier( 0.34 , 1.56 , 0.64 , 1 )  ")]
    [InlineData("CUBIC-BEZIER(0.34, 1.56, 0.64, 1)")]
    public void TheCssFormIsAccepted(string text)
    {
        Assert.True(Easing.TryParse(text, out Easing easing));

        // Those are ease-out-back's own control points, so the parsed curve should be
        // indistinguishable from the named one.
        Assert.Equal(Easing.EaseOutBack, easing);
    }

    [Fact]
    public void ANamedCurveStillWins()
    {
        Assert.True(Easing.TryParse("ease-out", out Easing named));
        Assert.Equal(Easing.EaseOut, named);

        Assert.True(Easing.TryParse("  Ease-Out  ", out Easing padded));
        Assert.Equal(Easing.EaseOut, padded);
    }

    [Fact]
    public void OvershootIsPreservedButTimeCannotRunBackwards()
    {
        // The y points are what make a curve overshoot, so they are left alone. The x
        // points are clamped, because a bezier used for easing has to be a function of
        // time and one that runs backwards has no meaning.
        Assert.True(Easing.TryParse("cubic-bezier(-1, -2, 2, 3)", out Easing easing));

        Assert.Equal(0, easing.Evaluate(0));
        Assert.Equal(1, easing.Evaluate(1));

        // Clamping x to [0,1] gives linear-looking control points in x but the y
        // overshoot survives, which is the whole point of allowing it.
        Assert.Equal(Easing.CubicBezier(0, -2, 1, 3), easing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cubic-bezier")]
    [InlineData("cubic-bezier(")]
    [InlineData("cubic-bezier()")]
    [InlineData("cubic-bezier(0.1, 0.2, 0.3)")]
    [InlineData("cubic-bezier(0.1, 0.2, 0.3, 0.4, 0.5)")]
    [InlineData("cubic-bezier(a, b, c, d)")]
    [InlineData("cubic-bezier[0.1, 0.2, 0.3, 0.4]")]
    [InlineData("bouncy")]
    public void AnythingElseIsRefusedSoTheLoaderCanSaySo(string? text)
    {
        // Refused rather than silently accepted: the config loader turns false into
        // SHB0421, which is the diagnostic that stops a curve nobody can parse from
        // looking like one that simply had no effect.
        Assert.False(Easing.TryParse(text!, out Easing easing));

        // And the fallback is still usable, so a bad curve degrades rather than throws.
        Assert.Equal(Easing.EaseOut, easing);
    }

    [Fact]
    public void AParsedCurveIsAnchoredAtBothEnds()
    {
        Assert.True(Easing.TryParse("cubic-bezier(0.2, 0.8, 0.4, 0.9)", out Easing easing));

        Assert.Equal(0, easing.Evaluate(0));
        Assert.Equal(1, easing.Evaluate(1));

        // And monotonic through the middle, which is what makes it usable for motion.
        double previous = 0;

        for (double t = 0.1; t <= 1.0; t += 0.1)
        {
            double value = easing.Evaluate(t);
            Assert.True(value >= previous, $"went backwards at t={t}");
            previous = value;
        }
    }

    [Fact]
    public void ItRoundTripsThroughItsOwnText()
    {
        // ToString already emits the cubic-bezier form, so it was printing something
        // the parser could not read back.
        Assert.True(Easing.TryParse(Easing.EaseOutExpo.ToString(), out Easing parsed));

        Assert.Equal(Easing.EaseOutExpo, parsed);
    }
}
