using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;

namespace Taj.Core.Widgets;

/// <summary>
/// The active layout as a picture of itself: the workspace drawn small, with a pane
/// for each window the layout would place.
/// </summary>
/// <remarks>
/// <para>
/// The glyph filter - <c>{{ layout | icon }}</c> - names each layout with a
/// box-drawing character that needs no font installed, and that is all it can do:
/// <c>┤</c> for a spiral means nothing until it has been learnt, and three spirals
/// and four master layouts are more than a character each can tell apart at a glance.
/// A thumbnail shows the arrangement: the big pane on the left and the rest dwindling
/// into a corner is the spiral, a column of equal strips beside it is the master
/// layout, four squares are the grid. Nothing has to be learnt.
/// </para>
/// <para>
/// Four panes by default, because that is the fewest that tell every layout apart -
/// a spiral of three is exactly a master layout of three - and a schematic that is
/// always the same picture for a layout is one the eye comes to recognise. With
/// <c>panes="windows"</c> the picture follows the workspace instead, growing a pane as
/// each window opens - but never below <see cref="MinPanes"/>, four unless said, and
/// the panes the workspace does not yet have are drawn faint. Without the floor, one
/// window was one square in every layout and the indicator had stopped indicating;
/// without the fading, the floor would have been a lie about the count. With both,
/// one window in a spiral is the large pane solid and the three it would dwindle into
/// faint, which says the layout and the count at once - the one thing a fixed four
/// cannot.
/// </para>
/// <para>
/// Hidden while the window manager has not said what the layout is, and when it names
/// one the bar does not know, as a text widget with nothing to say is hidden.
/// </para>
/// </remarks>
public sealed class LayoutWidget : IWidget
{
    /// <summary>The value this widget reads unless told otherwise.</summary>
    public const string DefaultSource = "layout";

    /// <summary>The value that says how many windows the active workspace holds.</summary>
    public const string WindowsKey = "windows";

    /// <summary>The fewest panes that tell every layout apart; the default for <see cref="Panes"/> and <see cref="MinPanes"/>.</summary>
    public const int DistinguishingPanes = 4;

    private IReadOnlyList<WidgetCondition> _conditions = [];
    private int? _panes = DistinguishingPanes;
    private int _minPanes = DistinguishingPanes;
    private string _source = DefaultSource;

    /// <param name="id">The widget's id, for styling and hit testing.</param>
    /// <param name="width">The thumbnail's width in device-independent pixels.</param>
    /// <param name="height">The thumbnail's height in device-independent pixels.</param>
    /// <param name="style">Pane colour as the foreground; a pill behind the picture as the background.</param>
    /// <param name="box">Padding around the picture, inside the pill.</param>
    public LayoutWidget(string id, int width, int height, VisualStyle style, BoxStyle box = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Id = id;
        Width = width;
        Height = height;
        Style = style;
        Box = box;
        Dependencies = [DefaultSource];
    }

    public string Id { get; }

    /// <summary>The value read for the layout's name; <c>layout</c> unless config says otherwise.</summary>
    public string Source
    {
        get => _source;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            _source = value;
            RecomputeDependencies();
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The space between panes, in device-independent pixels.</summary>
    public int Gap { get; set; } = 1;

    /// <summary>
    /// How many windows to draw, or null to draw as many as the workspace holds -
    /// never fewer than <see cref="MinPanes"/>, at most <see cref="LayoutThumbnail.MaxPanes"/>.
    /// </summary>
    public int? Panes
    {
        get => _panes;
        set
        {
            _panes = value is { } count ? Math.Clamp(count, 1, LayoutThumbnail.MaxPanes) : null;
            RecomputeDependencies();
        }
    }

    /// <summary>
    /// The fewest panes drawn while <see cref="Panes"/> follows the workspace; the
    /// panes beyond the workspace's own windows are drawn <see cref="Faint"/>.
    /// </summary>
    /// <remarks>
    /// Four unless said, for the reason four is the fixed default: below it the
    /// layouts cannot be told apart, and a picture that cannot say which layout it
    /// shows has stopped being a layout indicator. One draws the workspace's own count,
    /// faint when it is empty. Clamped to 1..<see cref="LayoutThumbnail.MaxPanes"/>, and
    /// nothing to a fixed count, which is both the floor and the ceiling already.
    /// </remarks>
    public int MinPanes
    {
        get => _minPanes;
        set => _minPanes = Math.Clamp(value, 1, LayoutThumbnail.MaxPanes);
    }

    public IReadOnlyList<string> Dependencies { get; private set; }

    /// <summary>Pane colour as the foreground; a pill behind the picture as the background.</summary>
    public VisualStyle Style { get; set; }

    public BoxStyle Box { get; set; }

    /// <summary>
    /// The colour of the first window's pane - the main one in a master layout, the
    /// large one in a spiral - or null to draw it like the rest.
    /// </summary>
    public Colour? MainColour { get; set; }

    /// <summary>What the pointer does here: a command per gesture, or nothing.</summary>
    public PointerActions Actions { get; set; } = PointerActions.None;

    /// <summary>Colours while the pointer is over it, when it is clickable and they were written.</summary>
    public VisualStyle? HoverStyle { get; set; }

    /// <summary>Styles whose foreground replaces the pane colour when the layout's name matches; first match wins.</summary>
    public IReadOnlyList<WidgetCondition> Conditions
    {
        get => _conditions;
        set
        {
            _conditions = value ?? [];
            RecomputeDependencies();
        }
    }

    public VisualNode Build(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        string? layout = values.GetValueOrDefault(Source);

        // How many panes to draw, and how many of them the workspace actually has. A
        // fixed count is both. Following the workspace, the count is its windows but
        // never fewer than the floor, and the panes past its windows are the faint ones.
        int shown;
        int real;

        if (Panes is { } fixedCount)
        {
            shown = real = fixedCount;
        }
        else
        {
            real = Numbers.TryParseFirst(values.GetValueOrDefault(WindowsKey), out double windows)
                ? (int)Math.Clamp(windows, 0, LayoutThumbnail.MaxPanes)
                : 0;

            shown = Math.Max(real, MinPanes);
        }

        VisualStyle style = StyleFor(layout, values);
        IReadOnlyList<Shape>? shapes = Shapes(layout, shown, real, style.Foreground);

        var node = new VisualNode
        {
            Id = Id,
            Kind = VisualKind.Shape,
            Shapes = shapes ?? [],
            Style = style,
            Box = Box with
            {
                Width = Width + Box.Padding.Horizontal,
                Height = Height + Box.Padding.Vertical,
            },
            Visible = shapes is not null,
            HoverStyle = Actions.Any ? Hovered(style) : null,
        };

        Actions.ApplyTo(node);
        return node;
    }

    /// <summary>
    /// A pane the workspace does not yet have: the colour at a third of its own
    /// opacity, so a dim colour fades further and a bright one to a tint.
    /// </summary>
    public static Colour Faint(Colour colour) => colour.WithAlpha((byte)Math.Round(colour.A / 3.0));

    private string? _lastLayout;
    private int _lastShown;
    private int _lastReal;
    private Colour _lastColour;
    private Colour? _lastMain;
    private IReadOnlyList<Shape>? _lastShapes;

    /// <summary>
    /// The panes as shapes, worked out once per distinct layout, counts and colour.
    /// </summary>
    /// <remarks>
    /// The tree is rebuilt whenever any value on the bar changes - a seconds clock,
    /// once a second - and the layout changes when the user changes it. Arranging a
    /// container of placeholders is cheap, but it is work and a handful of allocations
    /// that would otherwise be done sixty times a minute for the same picture.
    /// </remarks>
    private IReadOnlyList<Shape>? Shapes(string? layout, int shown, int real, Colour colour)
    {
        if (string.Equals(layout, _lastLayout, StringComparison.Ordinal) &&
            shown == _lastShown && real == _lastReal && colour == _lastColour && MainColour == _lastMain)
        {
            return _lastShapes;
        }

        UnitRect[]? rects = LayoutThumbnail.Panes(layout, shown, Width, Height, Gap);
        Shape[]? shapes = null;

        if (rects is not null)
        {
            shapes = new Shape[rects.Length];

            for (int i = 0; i < rects.Length; i++)
            {
                Colour pane = i == 0 && MainColour is { } main ? main : colour;
                if (i >= real) pane = Faint(pane);

                shapes[i] = new RectShape(rects[i], pane);
            }
        }

        _lastLayout = layout;
        _lastShown = shown;
        _lastReal = real;
        _lastColour = colour;
        _lastMain = MainColour;
        _lastShapes = shapes;

        return shapes;
    }

    private VisualStyle StyleFor(string? layout, IReadOnlyDictionary<string, string?> values)
    {
        IReadOnlyList<WidgetCondition> conditions = _conditions;

        // Indexed rather than foreach, so the list's enumerator is not boxed per build.
        for (int i = 0; i < conditions.Count; i++)
        {
            WidgetCondition condition = conditions[i];

            string subject = condition.Source is null || string.Equals(condition.Source, Source, StringComparison.OrdinalIgnoreCase)
                ? layout ?? string.Empty
                : values.GetValueOrDefault(condition.Source) ?? string.Empty;

            // Only the panes change colour: a condition says what the picture is drawn
            // in, and the pill behind it stays what the widget said.
            if (condition.Holds(subject)) return Style with { Foreground = condition.Style.Foreground };
        }

        return Style;
    }

    private void RecomputeDependencies()
    {
        List<string> keys = [Source];

        if (Panes is null) keys.Add(WindowsKey);

        foreach (WidgetCondition condition in _conditions)
        {
            if (condition.Source is { } source && !keys.Contains(source, StringComparer.OrdinalIgnoreCase))
                keys.Add(source);
        }

        Dependencies = keys;
    }

    /// <summary>The same hover an icon gets: a lighter pill, or the faint one when there is none.</summary>
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
