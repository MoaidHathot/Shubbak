using Shubbak.Core.Animation;

namespace Shubbak.Core.Tests;

/// <summary>
/// How accurately the easing curves actually solve.
/// </summary>
/// <remarks>
/// <para>
/// A cubic bezier used for easing is parametric in both axes, so finding y for a given
/// x needs a numerical solve. <c>Evaluate</c> says Newton-Raphson "converges in three
/// or four iterations here" - a claim made once, in a comment, and never checked.
/// </para>
/// <para>
/// It runs once per window per frame: at 90 fps with three windows moving that is
/// about 270 solves a second. Every named curve goes through it except Linear, which
/// is the only one the short-circuit catches, so this is not a rarely-taken path.
/// </para>
/// <para>
/// The reference bezier below is written out independently rather than reusing the
/// implementation under test. Checking a solver against its own arithmetic proves the
/// two agree, which they will, whether or not either is right.
/// </para>
/// </remarks>
public sealed class EasingAccuracyTests
{
    /// <summary>
    /// Every curve the configuration can name, plus a hand-written one, each with the
    /// accuracy it is held to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per curve rather than one shared bound, because a single number loose enough
    /// for the pathological case would leave the shipped curves an order of magnitude
    /// of slack to regress into unnoticed.
    /// </para>
    /// <para>
    /// The tolerances are on y, not on the solved u, and dy/du exceeds one on the
    /// steep curves - which is why ease-out-expo is allowed more than ease-out despite
    /// the solver working equally hard on both.
    /// </para>
    /// </remarks>
    public static TheoryData<string, double, double, double, double, double> Curves => new()
    {
        { "linear", 0, 0, 1, 1, 1e-9 },
        { "ease-in-out", 0.42, 0, 0.58, 1, 5e-6 },
        { "ease-out", 0, 0, 0.58, 1, 5e-6 },
        { "ease-in", 0.42, 0, 1, 1, 5e-6 },
        { "ease-out-back", 0.34, 1.56, 0.64, 1, 5e-6 },
        { "ease-out-expo", 0.16, 1, 0.3, 1, 1e-5 },

        // A deliberately awkward one: both control points bunched near zero makes
        // x(u) nearly flat over most of its start, which is where Newton stalls and
        // the bisection fallback has to carry the whole solve. Nothing ships with a
        // curve like this, but cubic-bezier() in the config will accept one.
        { "flat-start", 0.02, 0.9, 0.05, 1, 5e-5 },
    };

    /// <summary>
    /// The cubic bezier for control points (0, a, b, 1), written from the definition.
    /// </summary>
    private static double Bezier(double t, double a, double b)
    {
        double inverse = 1 - t;

        return (3 * inverse * inverse * t * a)
             + (3 * inverse * t * t * b)
             + (t * t * t);
    }

    private static Easing Curve(double x1, double y1, double x2, double y2) =>
        Easing.CubicBezier(x1, y1, x2, y2);

    [Theory]
    [MemberData(nameof(Curves))]
    public void TheSolveLandsWhereItClaimsTo(
        string name, double x1, double y1, double x2, double y2, double tolerance)
    {
        // Walk the curve by its parameter, which gives an exact (x, y) pair with no
        // solving involved, then ask Evaluate for y at that x. Any difference is the
        // solver's error, isolated from the curve's own shape.
        Easing easing = Curve(x1, y1, x2, y2);

        double worst = 0;
        double worstAt = 0;

        for (int i = 1; i < 1000; i++)
        {
            double u = i / 1000.0;
            double x = Bezier(u, x1, x2);
            double expected = Bezier(u, y1, y2);

            double error = Math.Abs(easing.Evaluate(x) - expected);

            if (error > worst)
            {
                worst = error;
                worstAt = x;
            }
        }

        Assert.True(
            worst < tolerance,
            $"{name}: worst error {worst:E3} at x={worstAt:F4}, allowed {tolerance:E0}");
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void TheCurveDoesNotJumpBetweenNeighbouringFrames(
        string name, double x1, double y1, double x2, double y2, double tolerance)
    {
        _ = tolerance;

        // What a solver error actually looks like on screen. A wrong-but-consistent
        // answer shifts a window a fraction of a pixel and nobody sees it; an answer
        // that is wrong by a different amount each frame is jitter, and jitter is the
        // whole reason any of this is being measured.
        //
        // Sampled far finer than a real frame, so the bound is on the solver rather
        // than on the curve: over a thousandth of the animation no easing should move
        // more than a few percent of its range.
        Easing easing = Curve(x1, y1, x2, y2);

        double worst = 0;
        double worstAt = 0;
        double previous = easing.Evaluate(0);

        for (int i = 1; i <= 1000; i++)
        {
            double t = i / 1000.0;
            double current = easing.Evaluate(t);
            double step = Math.Abs(current - previous);

            if (step > worst)
            {
                worst = step;
                worstAt = t;
            }

            previous = current;
        }

        Assert.True(
            worst < 0.05,
            $"{name}: jumped {worst:F4} between t={worstAt - 0.001:F3} and t={worstAt:F3}");
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void TheEndsAreExact(
        string name, double x1, double y1, double x2, double y2, double tolerance)
    {
        _ = tolerance;

        // Not solved - short-circuited - but worth pinning, because a window that ends
        // a hair short of its target is left permanently misplaced by exactly that
        // hair, and the next layout pass has no reason to correct it.
        Easing easing = Curve(x1, y1, x2, y2);

        Assert.Equal(0, easing.Evaluate(0));
        Assert.Equal(1, easing.Evaluate(1));
        Assert.Equal(0, easing.Evaluate(-1));
        Assert.Equal(1, easing.Evaluate(2));

        Assert.False(double.IsNaN(easing.Evaluate(0.5)), $"{name} produced NaN mid-curve");
    }
}
