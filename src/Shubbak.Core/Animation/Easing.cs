using System.Globalization;

namespace Shubbak.Core.Animation;

/// <summary>
/// A named easing curve, or a custom cubic bezier.
/// </summary>
/// <remarks>
/// <para>
/// The curve matters more than the duration for how motion feels. An ease-out
/// curve - fast at the start, settling at the end - reads as responsive because
/// most of the distance is covered immediately; a linear curve of the same duration
/// feels sluggish even though it takes exactly as long.
/// </para>
/// <para>
/// Cubic bezier control points use the CSS convention, so curves can be copied
/// straight from any easing reference or design tool.
/// </para>
/// </remarks>
public readonly record struct Easing
{
    private readonly double _x1, _y1, _x2, _y2;

    private Easing(double x1, double y1, double x2, double y2)
    {
        _x1 = x1;
        _y1 = y1;
        _x2 = x2;
        _y2 = y2;
    }

    /// <summary>Constant speed. Reads as mechanical; rarely the right choice.</summary>
    public static Easing Linear { get; } = new(0, 0, 1, 1);

    public static Easing EaseInOut { get; } = new(0.42, 0, 0.58, 1);

    /// <summary>Fast start, gentle settle. The best default for window motion.</summary>
    public static Easing EaseOut { get; } = new(0, 0, 0.58, 1);

    public static Easing EaseIn { get; } = new(0.42, 0, 1, 1);

    /// <summary>Overshoots slightly then settles, giving motion a sense of weight.</summary>
    public static Easing EaseOutBack { get; } = new(0.34, 1.56, 0.64, 1);

    /// <summary>A sharp, decisive curve; good for very short durations.</summary>
    public static Easing EaseOutExpo { get; } = new(0.16, 1, 0.3, 1);

    /// <summary>A custom curve, in CSS <c>cubic-bezier</c> terms.</summary>
    public static Easing CubicBezier(double x1, double y1, double x2, double y2) =>
        new(Math.Clamp(x1, 0, 1), y1, Math.Clamp(x2, 0, 1), y2);

    /// <summary>
    /// Resolves a curve written in config: a named one, or <c>cubic-bezier(...)</c>.
    /// </summary>
    /// <remarks>
    /// The custom form is the reason the type stores CSS control points at all, and
    /// for a long time there was no way to write one - <see cref="CubicBezier"/> had
    /// no callers anywhere, so the documented reason for the design was unreachable by
    /// the people it was made for.
    /// </remarks>
    public static bool TryParse(string name, out Easing easing)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case "linear": easing = Linear; return true;
            case "ease-in": easing = EaseIn; return true;
            case "ease-out": easing = EaseOut; return true;
            case "ease-in-out": easing = EaseInOut; return true;
            case "ease-out-back": easing = EaseOutBack; return true;
            case "ease-out-expo": easing = EaseOutExpo; return true;
            default: return TryParseCubicBezier(name, out easing);
        }
    }

    /// <summary>Parses the CSS <c>cubic-bezier(x1, y1, x2, y2)</c> form.</summary>
    /// <remarks>
    /// The x control points are clamped to [0, 1] because a bezier used for easing
    /// must be a function of time, and one that runs backwards has no meaning here.
    /// The y points are left alone on purpose: outside that range is exactly how a
    /// curve overshoots, which is what ease-out-back is.
    /// </remarks>
    private static bool TryParseCubicBezier(string? text, out Easing easing)
    {
        easing = EaseOut;

        if (string.IsNullOrWhiteSpace(text)) return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        const string Prefix = "cubic-bezier";

        if (!span.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        span = span[Prefix.Length..].TrimStart();

        if (span.Length < 2 || span[0] != '(' || span[^1] != ')') return false;

        span = span[1..^1];

        // Five, so a fifth argument is detected rather than silently ignored.
        Span<Range> parts = stackalloc Range[5];
        if (span.Split(parts, ',') != 4) return false;

        Span<double> points = stackalloc double[4];

        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(
                span[parts[i]].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out points[i]))
            {
                return false;
            }
        }

        easing = CubicBezier(points[0], points[1], points[2], points[3]);
        return true;
    }

    /// <summary>
    /// Evaluates the curve at <paramref name="t"/> in [0, 1].
    /// </summary>
    /// <remarks>
    /// A cubic bezier used for easing is parametric in both axes, so finding y for a
    /// given x needs a solve. Newton-Raphson converges in three or four iterations
    /// here, and the whole thing is branch-light and allocation-free because it runs
    /// once per window per frame inside the tick loop
    /// (docs/adr/0001-language-choice.md, constraint 2).
    /// </remarks>
    public double Evaluate(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        // Linear is by far the most common curve after ease-out and needs no solve.
        if (_x1 == _y1 && _x2 == _y2) return t;

        double u = SolveForX(t);
        return Bezier(u, _y1, _y2);
    }

    private double SolveForX(double x)
    {
        double u = x;

        for (int i = 0; i < 4; i++)
        {
            double error = Bezier(u, _x1, _x2) - x;
            if (Math.Abs(error) < 1e-6) return u;

            double slope = BezierDerivative(u, _x1, _x2);
            if (Math.Abs(slope) < 1e-9) break;

            u -= error / slope;
        }

        // Fall back to bisection if Newton stalls, which can happen on curves with
        // a near-flat segment.
        double low = 0, high = 1;
        u = x;

        for (int i = 0; i < 12; i++)
        {
            double value = Bezier(u, _x1, _x2);
            if (Math.Abs(value - x) < 1e-6) break;

            if (value > x) high = u; else low = u;
            u = (low + high) / 2;
        }

        return u;
    }

    private static double Bezier(double t, double a, double b)
    {
        double inverse = 1 - t;
        return (3 * inverse * inverse * t * a) + (3 * inverse * t * t * b) + (t * t * t);
    }

    private static double BezierDerivative(double t, double a, double b)
    {
        double inverse = 1 - t;
        return (3 * inverse * inverse * a) +
               (6 * inverse * t * (b - a)) +
               (3 * t * t * (1 - b));
    }

    public override string ToString() =>
        $"cubic-bezier({_x1:0.##}, {_y1:0.##}, {_x2:0.##}, {_y2:0.##})";
}
