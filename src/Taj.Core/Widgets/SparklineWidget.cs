using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;

namespace Taj.Core.Widgets;

/// <summary>
/// A small graph of a list of numbers: CPU over the last minute, the battery over
/// the afternoon, whatever a script prints as a run of readings.
/// </summary>
/// <remarks>
/// <para>
/// The value it reads is a list - <c>12 15 50 50 48</c> - and it draws every number in
/// it, oldest at the left. Where the list comes from is not its business: a source
/// with <c>history=</c> publishes one as <c>&lt;name&gt;.history</c>, and a script
/// that already keeps the readings prints the same shape itself. Keeping the history
/// here instead would have meant a graph that stopped moving when the value held
/// steady; see <see cref="Sources.HistorySource"/>.
/// </para>
/// <para>
/// The scale is the data's own unless <see cref="Min"/> and <see cref="Max"/> pin it,
/// which a percentage should: a CPU graph scaled to its own range makes a quiet
/// minute look like a storm. With <see cref="Points"/> set the newest reading sits at
/// the right edge and the line grows in from there as readings arrive, so time runs
/// left to right from the first tick rather than the first two readings being
/// stretched across the whole width.
/// </para>
/// <para>
/// A <c>when</c> block tests the newest reading, so a graph can turn red while the
/// value is high and go back when it is not, which is the glance a bar is for.
/// </para>
/// </remarks>
public sealed class SparklineWidget : IWidget
{
    /// <param name="id">The widget's id, for styling and hit testing.</param>
    /// <param name="source">The source whose value is the list of readings.</param>
    /// <param name="width">The graph's width in device-independent pixels.</param>
    /// <param name="height">The graph's height in device-independent pixels.</param>
    /// <param name="style">Line colour as the foreground, and a pill behind the graph as the background.</param>
    /// <param name="box">Padding around the graph, inside the pill.</param>
    public SparklineWidget(string id, string source, int width, int height, VisualStyle style, BoxStyle box = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Id = id;
        Source = source;
        Width = width;
        Height = height;
        Style = style;
        Box = box;
        Dependencies = [source];
    }

    public string Id { get; }

    /// <summary>The source read: a list of numbers, oldest first.</summary>
    public string Source { get; }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<string> Dependencies { get; private set; }

    /// <summary>Line colour as the foreground; a pill behind the graph as the background.</summary>
    public VisualStyle Style { get; set; }

    public BoxStyle Box { get; set; }

    /// <summary>The line's width in device-independent pixels.</summary>
    public int Thickness { get; set; } = 1;

    /// <summary>The colour of the area under the line; transparent for none.</summary>
    public Colour Fill { get; set; } = Colour.Transparent;

    /// <summary>The value at the bottom edge, or null to use the smallest reading.</summary>
    public double? Min { get; set; }

    /// <summary>The value at the top edge, or null to use the largest reading.</summary>
    public double? Max { get; set; }

    /// <summary>
    /// How many readings the width holds, or null to spread whatever there are across
    /// it. With a count, the newest reading is at the right edge and the graph fills
    /// in from there.
    /// </summary>
    public int? Points { get; set; }

    /// <summary>What the pointer does here: a command per gesture, or nothing.</summary>
    public PointerActions Actions { get; set; } = PointerActions.None;

    /// <summary>Colours while the pointer is over it, when it is clickable and they were written.</summary>
    public VisualStyle? HoverStyle { get; set; }

    /// <summary>Styles that replace the default when the newest reading matches; first match wins.</summary>
    public IReadOnlyList<WidgetCondition> Conditions
    {
        get => _conditions;
        set
        {
            _conditions = value ?? [];

            string[] extra = [.. _conditions
                .Select(c => c.Source)
                .OfType<string>()
                .Where(s => !string.Equals(s, Source, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)];

            Dependencies = extra.Length == 0 ? [Source] : [Source, .. extra];
        }
    }

    private IReadOnlyList<WidgetCondition> _conditions = [];

    public VisualNode Build(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        string? value = values.GetValueOrDefault(Source);
        (IReadOnlyList<double> readings, UnitPoint[] points) = Decode(value);

        VisualStyle style = StyleFor(readings, values);

        var node = new VisualNode
        {
            Id = Id,
            Kind = VisualKind.Shape,
            Style = style,
            Box = Box with
            {
                Width = Width + Box.Padding.Horizontal,
                Height = Height + Box.Padding.Vertical,
            },

            // Hidden while the source has said nothing, as a text widget with nothing
            // to say is. Shown as soon as it has, even with one reading and no line
            // yet, so the box does not jump into the bar on the second tick.
            Visible = !string.IsNullOrEmpty(value),
            HoverStyle = Actions.Any ? Hovered(style) : null,
        };

        if (readings.Count >= 2)
            node.Shapes = [new PolylineShape(points, style.Foreground, Thickness, Fill)];

        Actions.ApplyTo(node);
        return node;
    }

    private string? _lastValue;
    private IReadOnlyList<double> _lastReadings = [];
    private UnitPoint[] _lastPoints = [];

    /// <summary>
    /// The readings in a value and where they plot, worked out once per distinct value.
    /// </summary>
    /// <remarks>
    /// The tree is rebuilt whenever any value on the bar changes - a seconds clock
    /// rebuilds it every second - and the history string is the same reference as last
    /// time whenever its own source has not ticked. Parsing sixty numbers and plotting
    /// them again for a value that had not changed was most of what a sparkline cost;
    /// the same bargain the icon widget makes with its bitmap. The points depend only
    /// on the readings and on settings fixed at load, so they are cached with them.
    /// </remarks>
    private (IReadOnlyList<double> Readings, UnitPoint[] Points) Decode(string? value)
    {
        if (ReferenceEquals(value, _lastValue) || string.Equals(value, _lastValue, StringComparison.Ordinal))
            return (_lastReadings, _lastPoints);

        IReadOnlyList<double> readings = Numbers.ParseAll(value);

        _lastValue = value;
        _lastReadings = readings;
        _lastPoints = Plot(readings);

        return (_lastReadings, _lastPoints);
    }

    /// <summary>The readings as points in the unit square, oldest at the left.</summary>
    internal UnitPoint[] Plot(IReadOnlyList<double> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        int count = readings.Count;
        var points = new UnitPoint[count];
        if (count == 0) return points;

        double low = Min ?? readings.Min();
        double high = Max ?? readings.Max();

        // Anchored at the right when a count is given and the list is short of it;
        // spread across the width otherwise. A single reading has nowhere to go but
        // the right edge, or the middle when nothing says.
        int slots = Points is { } wanted && wanted > count ? wanted : count;
        int offset = slots - count;

        for (int i = 0; i < count; i++)
        {
            double x = slots <= 1 ? 0.5 : (double)(offset + i) / (slots - 1);

            // A flat list sits in the middle rather than along an edge, where half the
            // stroke would be lost and a steady value would read as an empty graph.
            double y = high > low ? 1 - Math.Clamp((readings[i] - low) / (high - low), 0, 1) : 0.5;

            points[i] = new UnitPoint(x, y);
        }

        return points;
    }

    private VisualStyle StyleFor(IReadOnlyList<double> readings, IReadOnlyDictionary<string, string?> values)
    {
        IReadOnlyList<WidgetCondition> conditions = Conditions;
        if (conditions.Count == 0) return Style;

        // Indexed rather than foreach: enumerating the interface boxes the list's
        // enumerator, and this runs on every rebuild.
        for (int i = 0; i < conditions.Count; i++)
        {
            WidgetCondition condition = conditions[i];

            bool own = condition.Source is null || string.Equals(condition.Source, Source, StringComparison.OrdinalIgnoreCase);

            if (own)
            {
                if (readings.Count == 0)
                {
                    if (condition.Holds(string.Empty)) return condition.Style;
                    continue;
                }

                // The number as a number, where the condition is one; written out only
                // for a textual match against the newest reading, which is rare.
                bool holds = condition.IsNumeric
                    ? condition.HoldsNumber(readings[^1])
                    : condition.Holds(Numbers.Format(readings[^1]));

                if (holds) return condition.Style;
                continue;
            }

            if (condition.Holds(values.GetValueOrDefault(condition.Source!) ?? string.Empty)) return condition.Style;
        }

        return Style;
    }

    /// <summary>The same hover a text widget gets: a lighter pill, or the faint one when there is none.</summary>
    private VisualStyle Hovered(VisualStyle style) => style with
    {
        Background = HoverStyle is { } written && !written.Background.IsTransparent
            ? written.Background
            : style.Background.IsTransparent
                ? new Colour(0xFF, 0xFF, 0xFF, 0x1A)
                : style.Background.Lerp(Colour.White, 0.2),
        CornerRadius = Math.Max(style.CornerRadius, 4),
    };
}
