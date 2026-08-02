using Shubbak.Core.Geometry;
using Taj.Core.Layout;
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

    public TemplateWidget(string id, string template, VisualStyle style, BoxStyle box = default)
    {
        Id = id;
        _template = template ?? throw new ArgumentNullException(nameof(template));
        Style = style;
        Box = box;
        Dependencies = Template.Dependencies(template);
    }

    public string Id { get; }

    public IReadOnlyList<string> Dependencies { get; }

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

    public VisualNode Build(IReadOnlyDictionary<string, string?> values)
    {
        string text = Template.Render(_template, values);

        return new VisualNode
        {
            Id = Id,
            Kind = VisualKind.Text,
            Text = text,
            Style = Style,
            Box = Box,
            Visible = !HideWhenEmpty || text.Length > 0,
            OnClick = OnClick,
        };
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
