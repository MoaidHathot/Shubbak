using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;

namespace Taj.Core.Widgets;

/// <summary>What a <see cref="MeterWidget"/> looks like.</summary>
public enum MeterShape
{
    /// <summary>A track with a fill along it: a progress bar, a level.</summary>
    Bar,

    /// <summary>A ring with an arc around it: a gauge.</summary>
    Ring,
}

/// <summary>
/// A level: how much of a range a value is, drawn as a bar or a ring.
/// </summary>
/// <remarks>
/// <para>
/// A number is read to be compared, and a bar is for being glanced at; a battery at
/// <c>23%</c> is a figure to read where a bar a quarter full is a shape to see. The
/// value is the first number in the source, placed between <see cref="Min"/> and
/// <see cref="Max"/> - nought to a hundred unless told otherwise, since most of what
/// a bar meters is a percentage - and clamped, so a reading past the end fills the
/// meter rather than spilling out of it.
/// </para>
/// <para>
/// The bar is built from the primitives the layout already had: a container coloured
/// as the track, with one child coloured as the fill and sized to the fraction. The
/// ring is two arcs on one shape node, the dim track under the bright value,
/// clockwise from the top unless <see cref="Start"/> and <see cref="Sweep"/> make it
/// a dial. A <c>when</c> block recolours the fill by the value - red below ten,
/// green when charging - which is the one thing a meter most wants to say.
/// </para>
/// <para>
/// Hidden while the source has no number in it, as a text widget with nothing to say
/// is: a battery that has not reported is not at nought.
/// </para>
/// </remarks>
public sealed class MeterWidget : IWidget
{
    private IReadOnlyList<WidgetCondition> _conditions = [];

    /// <param name="id">The widget's id, for styling and hit testing.</param>
    /// <param name="source">The source whose first number is the level.</param>
    /// <param name="style">Fill colour as the foreground, track colour as the background, corners as the radius.</param>
    public MeterWidget(string id, string source, VisualStyle style)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(source);

        Id = id;
        Source = source;
        Style = style;
        Dependencies = [source];
    }

    public string Id { get; }

    /// <summary>The source read.</summary>
    public string Source { get; }

    public IReadOnlyList<string> Dependencies { get; private set; }

    /// <summary>Fill colour as the foreground, track colour as the background.</summary>
    public VisualStyle Style { get; set; }

    /// <summary>Spacing around the meter; a minimum or maximum width.</summary>
    public BoxStyle Box { get; set; }

    public MeterShape Shape { get; set; } = MeterShape.Bar;

    /// <summary>A bar's length along the value, in device-independent pixels.</summary>
    public int Width { get; set; } = 60;

    /// <summary>A bar's breadth, in device-independent pixels.</summary>
    public int Height { get; set; } = 6;

    /// <summary>Whether a bar fills upward rather than rightward.</summary>
    public bool Vertical { get; set; }

    /// <summary>A ring's diameter, in device-independent pixels.</summary>
    public int Size { get; set; } = 16;

    /// <summary>A ring's stroke, in device-independent pixels.</summary>
    public int Thickness { get; set; } = 3;

    /// <summary>Where a ring begins, in degrees clockwise from the top.</summary>
    public double Start { get; set; }

    /// <summary>How far a full ring runs, in degrees clockwise; 270 from 225 is a dial.</summary>
    public double Sweep { get; set; } = 360;

    /// <summary>The value at the empty end.</summary>
    public double Min { get; set; }

    /// <summary>The value at the full end.</summary>
    public double Max { get; set; } = 100;

    /// <summary>What the pointer does here: a command per gesture, or nothing.</summary>
    public PointerActions Actions { get; set; } = PointerActions.None;

    /// <summary>Colours while the pointer is over it, when it is clickable and they were written.</summary>
    public VisualStyle? HoverStyle { get; set; }

    /// <summary>Styles whose foreground replaces the fill when the value matches; first match wins.</summary>
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

    /// <summary>The value's place in the range, 0 to 1, or null when the source holds no number.</summary>
    public double? Fraction(string? value)
    {
        if (!Numbers.TryParseFirst(value, out double number)) return null;
        if (Max <= Min) return 0;

        return Math.Clamp((number - Min) / (Max - Min), 0, 1);
    }

    public VisualNode Build(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        string? value = values.GetValueOrDefault(Source);
        double? fraction = Fraction(value);
        VisualStyle style = StyleFor(value, values);

        VisualNode node = Shape == MeterShape.Ring
            ? Ring(fraction ?? 0, style)
            : Bar(fraction ?? 0, style);

        node.Visible = fraction is not null;

        // From the node's own style rather than the widget's: a bar's hover lightens
        // its track, and a ring - whose track is an arc, not the node - gains the
        // faint pill an icon does.
        node.HoverStyle = Actions.Any ? Hovered(node.Style) : null;

        Actions.ApplyTo(node);
        return node;
    }

    /// <summary>The track as a container, the fill as its one child, sized to the fraction.</summary>
    private VisualNode Bar(double fraction, VisualStyle style)
    {
        var track = new VisualNode
        {
            Id = Id,
            Kind = VisualKind.Container,
            Direction = Vertical ? FlexDirection.Column : FlexDirection.Row,

            // A vertical bar fills from the bottom, which is how a level is read.
            Justify = Vertical ? JustifyContent.End : JustifyContent.Start,
            Align = AlignItems.Stretch,
            Style = style,
            Box = Box with { Width = Width + Box.Padding.Horizontal, Height = Height + Box.Padding.Vertical },
        };

        int length = (int)Math.Round(fraction * (Vertical ? Height : Width), MidpointRounding.AwayFromZero);

        if (length > 0)
        {
            track.Add(new VisualNode
            {
                Id = $"{Id}.fill",
                Kind = VisualKind.Container,
                Style = VisualStyle.Default with { Background = style.Foreground, CornerRadius = style.CornerRadius },
                Box = Vertical ? new BoxStyle(Height: length) : new BoxStyle(Width: length),
            });
        }

        return track;
    }

    /// <summary>Two arcs on one node: the whole sweep dimly, and the fraction of it brightly over that.</summary>
    private VisualNode Ring(double fraction, VisualStyle style)
    {
        List<Shape> shapes = [];

        if (!style.Background.IsTransparent)
            shapes.Add(new ArcShape(Start, Sweep, style.Background, Thickness));

        if (fraction > 0 && !style.Foreground.IsTransparent)
            shapes.Add(new ArcShape(Start, Sweep * fraction, style.Foreground, Thickness));

        return new VisualNode
        {
            Id = Id,
            Kind = VisualKind.Shape,
            Shapes = shapes,

            // The ring's own colours are the arcs; the node behind them is bare, so a
            // `background` means the track and not a square behind a circle.
            Style = style with { Background = Colour.Transparent, Foreground = Colour.Transparent },
            Box = Box with { Width = Size + Box.Padding.Horizontal, Height = Size + Box.Padding.Vertical },
        };
    }

    private VisualStyle StyleFor(string? value, IReadOnlyDictionary<string, string?> values)
    {
        IReadOnlyList<WidgetCondition> conditions = _conditions;

        // Indexed rather than foreach, so the list's enumerator is not boxed per build.
        for (int i = 0; i < conditions.Count; i++)
        {
            WidgetCondition condition = conditions[i];

            string subject = condition.Source is null || string.Equals(condition.Source, Source, StringComparison.OrdinalIgnoreCase)
                ? value ?? string.Empty
                : values.GetValueOrDefault(condition.Source) ?? string.Empty;

            // Only the fill changes: a condition says what colour the level is, and
            // the track and corners stay what the widget said.
            if (condition.Holds(subject)) return Style with { Foreground = condition.Style.Foreground };
        }

        return Style;
    }

    /// <summary>The track lightened, as a text widget's pill is.</summary>
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
