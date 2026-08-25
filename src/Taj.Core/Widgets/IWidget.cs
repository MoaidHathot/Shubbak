using Shubbak.Core.Geometry;
using Shubbak.Ui.Layout;
using Taj.Core.Sources;

namespace Taj.Core.Widgets;

/// <summary>
/// Produces part of the bar's visual tree, from one or more sources.
/// </summary>
/// <remarks>
/// The extension point for anything the declarative widgets cannot express - a
/// sparkline, a graph, a custom indicator. Most widgets never need this: a template
/// bound to a source covers the overwhelming majority, which is the point.
/// </remarks>
public interface IWidget
{
    string Id { get; }

    /// <summary>Source names this widget re-renders for.</summary>
    IReadOnlyList<string> Dependencies { get; }

    /// <summary>Builds the widget's subtree from the current source values.</summary>
    VisualNode Build(IReadOnlyDictionary<string, string?> values);
}

/// <summary>
/// A style to use when a widget's value matches, or fails to match.
/// </summary>
/// <param name="Value">The text to compare against, case-insensitively.</param>
/// <param name="Style">The style to use instead when the condition holds.</param>
/// <param name="Negate">
/// True to apply the style when the value is <i>anything but</i> <paramref name="Value"/>.
/// </param>
/// <param name="Source">
/// The source to test instead of the rendered text, when they differ.
/// </param>
/// <remarks>
/// <para>
/// The bar's job is to be read at a glance, and a value that matters is one that
/// should look different rather than one the user has to actually read. A keyboard
/// showing a language you did not mean to be in, a battery below ten percent, a
/// microphone that is live: all the same shape of problem, and none of them worth a
/// widget type of their own.
/// </para>
/// <para>
/// Testing a source rather than the rendered text matters wherever a filter has
/// already transformed the value. The layout widget renders its name as a glyph, so
/// matching the text means writing box-drawing characters into the config and keeping
/// them in step with the glyph the filter happens to choose; matching the source means
/// writing the layout's name.
/// </para>
/// </remarks>
public readonly record struct WidgetCondition(
    string Value,
    VisualStyle Style,
    bool Negate = false,
    string? Source = null);

/// <summary>
/// A widget that renders a template into a single text node.
/// </summary>
/// <remarks>
/// Covers nearly every widget anyone actually writes: clock, window title, CPU,
/// battery, network, and anything driven by an external script. Declaring one takes
/// a few lines of config and no code.
/// </remarks>
public sealed class TemplateWidget : IWidget
{
    private readonly string _template;
    private readonly IReadOnlyList<string> _templateDependencies;
    private IReadOnlyList<WidgetCondition> _conditions = [];

    public TemplateWidget(string id, string template, VisualStyle style, BoxStyle box = default)
    {
        Id = id;
        _template = template ?? throw new ArgumentNullException(nameof(template));
        Style = style;
        Box = box;
        _templateDependencies = Template.Dependencies(template);
        Dependencies = _templateDependencies;
    }

    public string Id { get; }

    /// <summary>
    /// The sources this widget reads.
    /// </summary>
    /// <remarks>
    /// Includes anything a condition tests, not only what the template renders. The
    /// bar rebuilds a widget when one of its dependencies changes, so a condition
    /// watching a source the template never mentions would otherwise be evaluated
    /// once and then never again.
    /// </remarks>
    public IReadOnlyList<string> Dependencies { get; private set; }

    public VisualStyle Style { get; set; }

    public BoxStyle Box { get; set; }

    /// <summary>Command sent to the window manager when clicked.</summary>
    public string? OnClick { get; set; }

    /// <summary>
    /// Whether an empty result hides the widget entirely.
    /// </summary>
    /// <remarks>
    /// On by default. A widget whose value is momentarily unavailable should leave no
    /// trace rather than an empty box with padding and a background, which looks like
    /// a rendering fault.
    /// </remarks>
    public bool HideWhenEmpty { get; set; } = true;

    /// <summary>Styles that replace the default one when a value matches.</summary>
    /// <remarks>
    /// Checked in order, first match wins, so the config reads top to bottom the way
    /// it is written.
    /// </remarks>
    public IReadOnlyList<WidgetCondition> Conditions
    {
        get => _conditions;

        set
        {
            _conditions = value ?? [];

            string[] extra = [.. _conditions
                .Select(c => c.Source)
                .OfType<string>()
                .Where(s => !_templateDependencies.Contains(s, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)];

            Dependencies = extra.Length == 0 ? _templateDependencies : [.. _templateDependencies, .. extra];
        }
    }

    public VisualNode Build(IReadOnlyDictionary<string, string?> values)
    {
        string text = Template.Render(_template, values);

        return new VisualNode
        {
            Id = Id,
            Kind = VisualKind.Text,
            Text = text,
            Style = StyleFor(text, values),
            Box = Box,
            Visible = !HideWhenEmpty || text.Length > 0,
            OnClick = OnClick,
        };
    }

    private VisualStyle StyleFor(string text, IReadOnlyDictionary<string, string?> values)
    {
        foreach (WidgetCondition condition in Conditions)
        {
            // The rendered text unless the condition names a source. A filter may have
            // transformed the value out of recognition - the layout widget renders a
            // name as a glyph - and a condition should be able to test what the value
            // is rather than what it ended up looking like.
            string subject = condition.Source is null
                ? text
                : values.GetValueOrDefault(condition.Source) ?? string.Empty;

            bool matches = string.Equals(condition.Value, subject, StringComparison.OrdinalIgnoreCase);

            if (matches != condition.Negate) return condition.Style;
        }

        return Style;
    }
}

/// <summary>
/// The workspace indicator.
/// </summary>
/// <remarks>
/// The one widget that genuinely needs code rather than a template, because it
/// produces a variable number of children with per-child state and per-child click
/// commands. Clicking a workspace sends the same <c>focus --workspace</c> command a
/// keybinding would, so the two cannot diverge.
/// </remarks>
public sealed class WorkspacesWidget : IWidget
{
    public WorkspacesWidget(string id) => Id = id;

    public string Id { get; }

    /// <summary>Rebuilt whenever the workspace list changes.</summary>
    public IReadOnlyList<string> Dependencies => ["workspaces"];

    /// <summary>Style for the workspace showing on this monitor.</summary>
    public VisualStyle ActiveStyle { get; set; } = VisualStyle.Default;

    /// <summary>
    /// Style for the workspace holding input focus, when it differs from
    /// <see cref="ActiveStyle"/>.
    /// </summary>
    /// <remarks>
    /// Null falls back to <see cref="ActiveStyle"/>, which is right for a single
    /// monitor - there the focused workspace and the displayed one are always the
    /// same. With several, marking every monitor's displayed workspace identically
    /// makes it impossible to see which one the keyboard is talking to.
    /// </remarks>
    public VisualStyle? FocusedStyle { get; set; }

    /// <summary>Style for workspaces with windows on them.</summary>
    public VisualStyle OccupiedStyle { get; set; } = VisualStyle.Default;

    /// <summary>Style for empty, inactive workspaces.</summary>
    public VisualStyle EmptyStyle { get; set; } = VisualStyle.Default;

    /// <summary>
    /// Style while the pointer is over a workspace.
    /// </summary>
    /// <remarks>
    /// Workspaces are clickable, and nothing about them says so. A hover response is
    /// how a pointer interface admits that - without it the bar reads as a readout
    /// rather than a control.
    /// </remarks>
    public VisualStyle? HoverStyle { get; set; }

    /// <summary>
    /// Whether to hide empty workspaces that are not active.
    /// </summary>
    /// <remarks>
    /// Off by default. With nineteen declared workspaces, hiding the empty ones makes
    /// the indicator jump about as windows open and close, and positional muscle
    /// memory stops working.
    /// </remarks>
    public bool HideEmpty { get; set; }

    public BoxStyle ItemBox { get; set; } = new(Padding: Edges.Symmetric(8, 2));

    public int Gap { get; set; } = 2;

    public VisualNode Build(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var container = new VisualNode
        {
            Id = Id,
            Kind = VisualKind.Container,
            Direction = FlexDirection.Row,
            Align = AlignItems.Stretch,
            Gap = Gap,
        };

        values.TryGetValue("workspaces", out string? encoded);

        foreach (WorkspaceEntry entry in Decode(encoded))
        {
            if (HideEmpty && !entry.Active && !entry.HasWindows) continue;

            // Four states, most specific first. Focused is a refinement of active,
            // so it only wins when a style has been given for it.
            VisualStyle style = entry switch
            {
                { Focused: true } when FocusedStyle is { } focused => focused,
                { Active: true } => ActiveStyle,
                { HasWindows: true } => OccupiedStyle,
                _ => EmptyStyle,
            };

            container.Add(new VisualNode
            {
                Id = $"{Id}.{entry.Name}",
                Kind = VisualKind.Text,
                Text = entry.Label,
                Style = style,
                Box = ItemBox,

                // Keeps whatever the state gave it and changes only what hover says,
                // so hovering an active workspace does not blank its accent.
                HoverStyle = HoverStyle is { } hover
                    ? style with
                    {
                        Foreground = hover.Foreground,
                        Background = hover.Background.IsTransparent ? style.Background : hover.Background,
                    }
                    : null,

                // Quoted, because workspace names include characters the command
                // tokeniser would otherwise treat as syntax - the author's config
                // has workspaces named `-`, `\` and `'`.
                OnClick = $"focus --workspace \"{entry.Name}\"",
            });
        }

        return container;
    }

    /// <summary>One workspace as encoded by the bar host.</summary>
    /// <param name="Name">Identifier used by commands.</param>
    /// <param name="Label">What to display.</param>
    /// <param name="Active">Whether it is showing on its monitor.</param>
    /// <param name="HasWindows">Whether it holds any windows.</param>
    /// <param name="Focused">
    /// Whether it holds input focus. Only one workspace does, whereas one per monitor
    /// is <paramref name="Active"/>.
    /// </param>
    public readonly record struct WorkspaceEntry(
        string Name, string Label, bool Active, bool HasWindows, bool Focused = false);

    /// <summary>
    /// Decodes the compact workspace description.
    /// </summary>
    /// <remarks>
    /// A tab-and-pipe separated string rather than JSON. Sources carry strings by
    /// design - that uniformity is what lets a script-backed source and a built-in one
    /// be interchangeable - so this widget encodes its structure into one.
    /// </remarks>
    public static IEnumerable<WorkspaceEntry> Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) yield break;

        foreach (string record in encoded.Split('\t', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = record.Split('|');
            if (fields.Length < 4) continue;

            yield return new WorkspaceEntry(
                fields[0],
                fields[1],
                fields[2] == "1",
                fields[3] == "1",

                // Optional, so a host that predates the field still decodes cleanly.
                fields.Length > 4 && fields[4] == "1");
        }
    }

    /// <summary>Encodes workspaces for the source.</summary>
    public static string Encode(IEnumerable<WorkspaceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return string.Join('\t', entries.Select(e =>
            $"{e.Name}|{e.Label}|{(e.Active ? '1' : '0')}|" +
            $"{(e.HasWindows ? '1' : '0')}|{(e.Focused ? '1' : '0')}"));
    }
}

/// <summary>A fixed or flexible gap.</summary>
public sealed class SpacerWidget : IWidget
{
    public SpacerWidget(string id, int? width = null, double grow = 1)
    {
        Id = id;
        Width = width;
        Grow = grow;
    }

    public string Id { get; }

    public int? Width { get; }

    public double Grow { get; }

    public IReadOnlyList<string> Dependencies => [];

    public VisualNode Build(IReadOnlyDictionary<string, string?> values) => new()
    {
        Id = Id,
        Kind = VisualKind.Spacer,
        Box = new BoxStyle(Width: Width, Grow: Width is null ? Grow : 0),
    };
}
