using Shubbak.Core.Geometry;

namespace Shubbak.Ui.Rendering;

/// <summary>
/// Works out how much of each pixel a line, a polygon or an arc covers, for a
/// renderer that owns its pixels and blends them itself.
/// </summary>
/// <remarks>
/// <para>
/// Pure arithmetic over a span of coverages, one per pixel of a clip rectangle, so
/// it can be tested without a surface and used by any renderer that composites in
/// software. The composited GDI renderer is the one that does; a renderer on an API
/// with anti-aliasing of its own has no use for this.
/// </para>
/// <para>
/// Strokes use the same one-pixel ramp on distance the rounded corners use: a pixel
/// whose centre is well inside the stroke is covered, one well outside is not, and
/// the pixel the edge passes through is covered by how far in it lies. It is the
/// usual approximation and at bar sizes it is indistinguishable from the real area.
/// </para>
/// </remarks>
public static class ShapeRasteriser
{
    /// <summary>Sub-rows per pixel row when filling; see <see cref="Polygon"/>.</summary>
    private const int SubRows = 4;

    /// <summary>What one sub-row contributes to a pixel it crosses in full.</summary>
    private const double SubRowWeight = 1.0 / SubRows;

    /// <summary>
    /// A plain clamp. <see cref="Math.Clamp(double, double, double)"/> and its
    /// relatives order NaN and the two zeros as the standard says, which costs branches
    /// a pixel loop does not want and coverage never needs.
    /// </summary>
    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    /// <summary>The pixels a set of points can reach, grown by a margin and cut to the surface.</summary>
    public static Rect Bounds(ReadOnlySpan<PointD> points, double margin, Rect surface)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        foreach (PointD p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY)) return Rect.Empty;

        return Rect.FromEdges(
            (int)Math.Floor(minX - margin), (int)Math.Floor(minY - margin),
            (int)Math.Ceiling(maxX + margin), (int)Math.Ceiling(maxY + margin)).Intersect(surface);
    }

    /// <summary>The pixels within a reach of a centre, cut to the surface.</summary>
    public static Rect Bounds(PointD centre, double reach, Rect surface) => Rect.FromEdges(
        (int)Math.Floor(centre.X - reach), (int)Math.Floor(centre.Y - reach),
        (int)Math.Ceiling(centre.X + reach), (int)Math.Ceiling(centre.Y + reach)).Intersect(surface);

    /// <summary>
    /// Adds a stroked line through the points to the coverage, with round joins and
    /// ends.
    /// </summary>
    /// <remarks>
    /// Each pixel takes its coverage from its distance to the nearest segment, and the
    /// segments combine by the larger coverage rather than by adding, so a joint where
    /// two meet is drawn once and is no darker than the line either side of it. The
    /// distance to a segment includes its ends, which is what makes joins and caps
    /// round without any work.
    /// </remarks>
    /// <param name="points">The vertices, in pixels.</param>
    /// <param name="thickness">The stroke's width in pixels.</param>
    /// <param name="clip">The pixels the coverage describes, row by row.</param>
    /// <param name="coverage"><c>clip.Width * clip.Height</c> coverages, 0 to 1, raised where the stroke lies.</param>
    public static void Polyline(ReadOnlySpan<PointD> points, double thickness, Rect clip, Span<double> coverage)
    {
        Check(clip, coverage);
        if (points.Length < 2 || thickness <= 0 || clip.IsEmpty) return;

        double half = thickness / 2.0;

        // The ramp runs from half - 0.5 (fully covered) to half + 0.5 (not at all);
        // outside it the answer is known from the squared distance alone, and the
        // square root is paid only for the pixels the edge actually passes through.
        double outer = half + 0.5;
        double inner = Math.Max(0, half - 0.5);
        double outerSquared = outer * outer;
        double innerSquared = inner * inner;

        for (int i = 1; i < points.Length; i++)
        {
            PointD a = points[i - 1];
            PointD b = points[i];

            double abx = b.X - a.X;
            double aby = b.Y - a.Y;
            double lengthSquared = (abx * abx) + (aby * aby);

            // Only the pixels this segment can reach.
            int left = Math.Max(clip.Left, (int)Math.Floor(Math.Min(a.X, b.X) - outer));
            int right = Math.Min(clip.Right, (int)Math.Ceiling(Math.Max(a.X, b.X) + outer));
            int top = Math.Max(clip.Top, (int)Math.Floor(Math.Min(a.Y, b.Y) - outer));
            int bottom = Math.Min(clip.Bottom, (int)Math.Ceiling(Math.Max(a.Y, b.Y) + outer));

            for (int y = top; y < bottom; y++)
            {
                int row = (y - clip.Top) * clip.Width;
                double py = y + 0.5;

                for (int x = left; x < right; x++)
                {
                    double px = x + 0.5;

                    // The nearest point of the segment, ends included.
                    double t = lengthSquared <= 0 ? 0 : Clamp01((((px - a.X) * abx) + ((py - a.Y) * aby)) / lengthSquared);
                    double dx = px - (a.X + (t * abx));
                    double dy = py - (a.Y + (t * aby));
                    double distanceSquared = (dx * dx) + (dy * dy);

                    if (distanceSquared >= outerSquared) continue;

                    double c = distanceSquared <= innerSquared ? 1.0 : Clamp01(outer - Math.Sqrt(distanceSquared));

                    ref double cell = ref coverage[row + (x - clip.Left)];
                    if (c > cell) cell = c;
                }
            }
        }
    }

    /// <summary>
    /// Adds a filled polygon to the coverage, by the even-odd rule.
    /// </summary>
    /// <remarks>
    /// Scanlines, four to a pixel row, each crossing the polygon's edges to find the
    /// spans inside it; a span's end pixels take the fraction of their width the span
    /// covers and the pixels between take the whole of theirs. Four sub-rows is coarse
    /// along a nearly horizontal edge, but the one filled polygon a bar draws is the
    /// area under a sparkline, and its top edge is the line drawn over it.
    /// </remarks>
    /// <param name="points">The vertices, in pixels; the polygon is closed from the last back to the first.</param>
    /// <param name="clip">The pixels the coverage describes, row by row.</param>
    /// <param name="coverage"><c>clip.Width * clip.Height</c> coverages, 0 to 1, raised where the polygon lies.</param>
    public static void Polygon(ReadOnlySpan<PointD> points, Rect clip, Span<double> coverage)
    {
        Check(clip, coverage);
        if (points.Length < 3 || clip.IsEmpty) return;

        Span<double> crossings = points.Length <= 64 ? stackalloc double[points.Length] : new double[points.Length];

        for (int y = clip.Top; y < clip.Bottom; y++)
        {
            int row = (y - clip.Top) * clip.Width;

            for (int sub = 0; sub < SubRows; sub++)
            {
                double sy = y + ((sub + 0.5) / SubRows);
                int count = 0;

                // The edge from each point to the next, and from the last back to the
                // first - tracked by hand rather than with a modulo, which was most of
                // the cost of this loop.
                PointD a = points[^1];

                for (int i = 0; i < points.Length; i++)
                {
                    PointD b = points[i];

                    if (a.Y != b.Y)
                    {
                        // Half-open, so a vertex two edges share is crossed once, not twice.
                        (double lower, double upper) = a.Y < b.Y ? (a.Y, b.Y) : (b.Y, a.Y);

                        if (sy >= lower && sy < upper)
                            crossings[count++] = a.X + ((sy - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                    }

                    a = b;
                }

                if (count < 2) continue;

                crossings[..count].Sort();

                for (int i = 0; i + 1 < count; i += 2)
                {
                    double from = crossings[i] > clip.Left ? crossings[i] : clip.Left;
                    double to = crossings[i + 1] < clip.Right ? crossings[i + 1] : clip.Right;
                    if (to <= from) continue;

                    int first = (int)Math.Floor(from);
                    int last = (int)Math.Ceiling(to) - 1;
                    if (last > clip.Right - 1) last = clip.Right - 1;

                    int offset = row - clip.Left;

                    if (first == last)
                    {
                        // The span starts and ends inside one pixel.
                        Add(ref coverage[offset + first], (to - from) * SubRowWeight);
                        continue;
                    }

                    // The two end pixels take the fraction of their width the span
                    // covers; every pixel between them is covered whole, and the fill
                    // is nearly all of those - which is why they get the one add and
                    // nothing else. Working the fraction out per pixel was the whole
                    // cost of the fill.
                    Add(ref coverage[offset + first], (first + 1 - from) * SubRowWeight);
                    for (int x = first + 1; x < last; x++) Add(ref coverage[offset + x], SubRowWeight);
                    Add(ref coverage[offset + last], (to - last) * SubRowWeight);
                }
            }
        }

        static void Add(ref double cell, double amount)
        {
            double sum = cell + amount;
            cell = sum > 1.0 ? 1.0 : sum;
        }
    }

    /// <summary>
    /// Adds a stroked arc to the coverage, clockwise from twelve o'clock, with round
    /// ends.
    /// </summary>
    /// <remarks>
    /// A pixel within the arc's angle takes its coverage from how far it is from the
    /// circle; one outside takes it from how far it is from the nearer end of the arc,
    /// which rounds the ends and - because the cap is exactly the stroke's own half
    /// width - meets the body without a seam. A sweep of a whole turn or more is a
    /// ring and skips the angles altogether. A negative sweep is the same arc spoken
    /// from its other end.
    /// </remarks>
    /// <param name="centre">The circle's centre, in pixels.</param>
    /// <param name="radius">The circle's radius, to the middle of the stroke.</param>
    /// <param name="startDegrees">Where the arc begins; 0 is the top, 90 the right.</param>
    /// <param name="sweepDegrees">How far it runs clockwise.</param>
    /// <param name="thickness">The stroke's width in pixels.</param>
    /// <param name="clip">The pixels the coverage describes, row by row.</param>
    /// <param name="coverage"><c>clip.Width * clip.Height</c> coverages, 0 to 1, raised where the arc lies.</param>
    public static void Arc(
        PointD centre, double radius, double startDegrees, double sweepDegrees, double thickness,
        Rect clip, Span<double> coverage)
    {
        Check(clip, coverage);
        if (thickness <= 0 || radius <= 0 || sweepDegrees == 0 || clip.IsEmpty) return;

        if (sweepDegrees < 0)
        {
            startDegrees += sweepDegrees;
            sweepDegrees = -sweepDegrees;
        }

        bool ring = sweepDegrees >= 360;
        double start = ((startDegrees % 360) + 360) % 360;
        double half = thickness / 2.0;

        PointD from = OnCircle(centre, radius, start);
        PointD to = OnCircle(centre, radius, start + sweepDegrees);

        // The directions of the two ends, for the wedge test below.
        double fromX = from.X - centre.X, fromY = from.Y - centre.Y;
        double toX = to.X - centre.X, toY = to.Y - centre.Y;
        bool reflex = sweepDegrees > 180;

        for (int y = clip.Top; y < clip.Bottom; y++)
        {
            int row = (y - clip.Top) * clip.Width;

            for (int x = clip.Left; x < clip.Right; x++)
            {
                double px = x + 0.5;
                double py = y + 0.5;
                double dx = px - centre.X;
                double dy = py - centre.Y;

                double distance = ring || WithinSweep(dx, dy, fromX, fromY, toX, toY, reflex)
                    ? Math.Abs(Math.Sqrt((dx * dx) + (dy * dy)) - radius)
                    : Math.Min(Distance(px, py, from), Distance(px, py, to));

                double c = Clamp01(half + 0.5 - distance);
                if (c <= 0) continue;

                ref double cell = ref coverage[row + (x - clip.Left)];
                if (c > cell) cell = c;
            }
        }
    }

    /// <summary>The point on a circle at an angle clockwise from twelve o'clock.</summary>
    public static PointD OnCircle(PointD centre, double radius, double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        return new PointD(centre.X + (radius * Math.Sin(radians)), centre.Y - (radius * Math.Cos(radians)));
    }

    /// <summary>
    /// Whether a direction from the centre lies within the arc's angle, by which side
    /// of each end's ray it falls on.
    /// </summary>
    /// <remarks>
    /// Two cross products rather than an arctangent per pixel. For a sweep up to a
    /// half turn, inside is clockwise of the start ray and anticlockwise of the end
    /// ray; past a half turn the wedge is reflex, and inside is everything but the
    /// complement - anticlockwise of the start and clockwise of the end both at once.
    /// Measured at a tenth of the arctangent's cost, which for a gauge that is
    /// mostly its ring was most of the arc.
    /// </remarks>
    private static bool WithinSweep(double dx, double dy, double fromX, double fromY, double toX, double toY, bool reflex)
    {
        // In screen coordinates y grows downward, so a positive cross product here is
        // a clockwise turn from the first vector to the second.
        bool clockwiseOfStart = ((fromX * dy) - (fromY * dx)) >= 0;
        bool clockwiseOfEnd = ((toX * dy) - (toY * dx)) >= 0;

        return reflex
            ? clockwiseOfStart || !clockwiseOfEnd
            : clockwiseOfStart && !clockwiseOfEnd;
    }

    private static double Distance(double x, double y, PointD p)
    {
        double dx = x - p.X;
        double dy = y - p.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static void Check(Rect clip, Span<double> coverage)
    {
        if (coverage.Length < clip.Width * clip.Height)
            throw new ArgumentException("The coverage span is smaller than the clip.", nameof(coverage));
    }
}
