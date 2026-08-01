using Shubbak.Config;
using Shubbak.Config.Kdl;
using Shubbak.Core.Diagnostics;
using Taj.Core.Layout;
using Taj.Core.Sources;
using Taj.Core.Widgets;

namespace Taj.Core;

/// <summary>Everything Taj needs to run.</summary>
/// <param name="Profiles">Bar profiles by name.</param>
/// <param name="Rules">Which profile to use when.</param>
/// <param name="Default">Profile used when no rule matches.</param>
/// <param name="Sources">Sources to create.</param>
public sealed record TajConfig(
    IReadOnlyDictionary<string, BarProfile> Profiles,
    IReadOnlyList<BarRule> Rules,
    BarProfile Default,
    IReadOnlyList<SourceSpec> Sources);

/// <summary>A source declared in config.</summary>
/// <param name="Name">Name templates refer to.</param>
/// <param name="Kind">time, command, or wm.</param>
/// <param name="Argument">Format string or command line.</param>
/// <param name="Interval">How often to poll, for pull sources.</param>
/// <param name="TimeZone">Timezone id for a clock, or null for local time.</param>
public sealed record SourceSpec(
    string Name, string Kind, string Argument, TimeSpan Interval, string? TimeZone = null);

/// <summary>
/// Reads Taj's section of the Shubbak config.
/// </summary>
/// <remarks>
/// Same file and same parser as the window manager, so there is one config to learn,
/// one place to look, and one set of diagnostics. Splitting the bar into its own file
/// and format is exactly what makes Zebar feel like a separate product bolted on to
/// GlazeWM.
/// </remarks>
public static class TajConfigLoader
{
    /// <summary>Loads Taj's config, falling back to a sensible default bar.</summary>
    public static (TajConfig Config, IReadOnlyList<Diagnostic> Diagnostics) Load(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<Diagnostic> diagnostics = [];
        KdlParseResult parsed = KdlParser.Parse(source);
        diagnostics.AddRange(parsed.Diagnostics);

        KdlNode? bar = parsed.Document.Node("bar");

        if (parsed.HasErrors || bar is null) return (CreateDefault(), diagnostics);

        Dictionary<string, BarProfile> profiles = new(StringComparer.OrdinalIgnoreCase);
        List<SourceSpec> sources = [.. DefaultSources()];

        foreach (KdlNode node in bar.ChildrenNamed("source"))
            if (ParseSource(node, diagnostics) is { } spec) sources.Add(spec);

        foreach (KdlNode node in bar.ChildrenNamed("profile"))
        {
            BarProfile? profile = ParseProfile(node, profiles, diagnostics);
            if (profile is not null) profiles[profile.Name] = profile;
        }

        if (profiles.Count == 0)
        {
            TajConfig fallback = CreateDefault();
            profiles = new Dictionary<string, BarProfile>(fallback.Profiles, StringComparer.OrdinalIgnoreCase);
        }

        List<BarRule> rules = [];

        foreach (KdlNode node in bar.ChildrenNamed("rule"))
        {
            string? profileName = SettingText(node, "use") ?? node.Argument(0)?.AsString();

            if (profileName is null)
            {
                diagnostics.Add(Diagnostic.Error(
                    "TAJ0001", "A bar rule must name a profile.", node.Span,
                    "Write rule use=\"presentation\" workspace=\"\\\\\"."));
                continue;
            }

            if (!profiles.ContainsKey(profileName))
            {
                diagnostics.Add(Diagnostic.Error(
                    "TAJ0002",
                    $"Bar rule references profile '{profileName}', which is not defined.",
                    node.Span));
                continue;
            }

            rules.Add(new BarRule(
                profileName,
                SettingText(node, "workspace"),
                SettingInt(node, "monitor")));
        }

        BarProfile fallbackProfile =
            profiles.TryGetValue("default", out BarProfile? named) ? named : profiles.Values.First();

        return (new TajConfig(profiles, rules, fallbackProfile, sources), diagnostics);
    }

    private static SourceSpec? ParseSource(KdlNode node, List<Diagnostic> diagnostics)
    {
        string? name = node.Argument(0)?.AsString();

        if (name is null)
        {
            diagnostics.Add(Diagnostic.Error("TAJ0003", "A source must be named.", node.Span));
            return null;
        }

        string kind = SettingText(node, "kind") ?? "time";
        string argument = SettingText(node, "format") ?? SettingText(node, "command") ?? string.Empty;

        int intervalMs = SettingInt(node, "interval") ?? 1000;

        return new SourceSpec(
            name,
            kind,
            argument,
            TimeSpan.FromMilliseconds(intervalMs),
            SettingText(node, "timezone"));
    }

    /// <summary>
    /// Reads a setting written either as a child node or as a property.
    /// </summary>
    /// <remarks>
    /// <c>height 34</c> and <c>height=34</c> are both natural to write, and the rest
    /// of the config uses the child-node form for block settings - <c>general</c> and
    /// <c>gaps</c> both do. Accepting only one silently ignored the other, which is
    /// how an entire profile's appearance came to be discarded while the config
    /// validated cleanly.
    /// </remarks>
    private static KdlValue? Setting(KdlNode node, string name) =>
        node.Child(name)?.Argument(0) ?? node.Property(name);

    private static string? SettingText(KdlNode node, string name) =>
        Setting(node, name)?.AsString();

    private static int? SettingInt(KdlNode node, string name) =>
        Setting(node, name) is { } value && value.TryAsInt(out int result) ? result : null;

    private static double? SettingDouble(KdlNode node, string name) =>
        Setting(node, name) is { } value && value.TryAsDouble(out double result) ? result : null;

    private static bool? SettingBool(KdlNode node, string name) =>
        Setting(node, name) is { } value && value.TryAsBool(out bool result) ? result : null;

    private static BarProfile? ParseProfile(
        KdlNode node,
        Dictionary<string, BarProfile> existing,
        List<Diagnostic> diagnostics)
    {
        string? name = node.Argument(0)?.AsString();

        if (name is null)
        {
            diagnostics.Add(Diagnostic.Error("TAJ0004", "A bar profile must be named.", node.Span));
            return null;
        }

        // `extends` lets a profile change one thing about another, which is what
        // makes a per-workspace variant a few lines rather than a duplicate.
        BarProfile? parent = SettingText(node, "extends") is { } parentName &&
                             existing.TryGetValue(parentName, out BarProfile? found)
            ? found
            : null;

        BarEdge edge = string.Equals(SettingText(node, "edge"), "bottom", StringComparison.OrdinalIgnoreCase)
            ? BarEdge.Bottom
            : parent?.Edge ?? BarEdge.Top;

        int height = SettingInt(node, "height") ?? parent?.Height ?? 26;

        Colour background = ParseColour(SettingText(node, "background"))
            ?? parent?.Background
            ?? new Colour(0x1E, 0x1E, 0x2E);

        Colour foreground = ParseColour(SettingText(node, "foreground"))
            ?? new Colour(0xCD, 0xD6, 0xF4);

        var font = new FontStyle(
            SettingText(node, "font") ?? "Segoe UI",
            SettingInt(node, "font-size") ?? 12);

        List<BarZone> zones = [];

        foreach (KdlNode zoneNode in node.ChildrenNamed("zone"))
        {
            BarZone? zone = ParseZone(zoneNode, foreground, font, diagnostics);
            if (zone is not null) zones.Add(zone);
        }

        if (zones.Count == 0 && parent is not null) zones = [.. parent.Zones];

        int padding = SettingInt(node, "padding") ?? 6;

        return new BarProfile(name, edge, height, background, Edges.Symmetric(padding, 0), zones);
    }

    private static BarZone? ParseZone(
        KdlNode node, Colour foreground, FontStyle font, List<Diagnostic> diagnostics)
    {
        string id = node.Argument(0)?.AsString() ?? "zone";

        JustifyContent justify = (SettingText(node, "justify") ?? "start").ToLowerInvariant() switch
        {
            "center" or "centre" => JustifyContent.Center,
            "end" => JustifyContent.End,
            "space-between" => JustifyContent.SpaceBetween,
            "space-around" => JustifyContent.SpaceAround,
            _ => JustifyContent.Start,
        };

        double grow = SettingDouble(node, "grow") ?? 0;
        int gap = SettingInt(node, "gap") ?? 6;

        List<IWidget> widgets = [];

        foreach (KdlNode widgetNode in node.Children)
        {
            IWidget? widget = ParseWidget(widgetNode, foreground, font, diagnostics);
            if (widget is not null) widgets.Add(widget);
        }

        return new BarZone(id, justify, grow, gap, widgets);
    }

    private static IWidget? ParseWidget(
        KdlNode node, Colour foreground, FontStyle font, List<Diagnostic> diagnostics)
    {
        string id = SettingText(node, "id") ?? node.Name;

        var style = VisualStyle.Default with
        {
            Foreground = ParseColour(SettingText(node, "colour") ?? SettingText(node, "color")) ?? foreground,
            Background = ParseColour(SettingText(node, "background")) ?? Colour.Transparent,
            Font = font,
            CornerRadius = SettingInt(node, "radius") ?? 0,
        };

        var box = new BoxStyle(Padding: Edges.Symmetric(6, 0));

        switch (node.Name)
        {
            case "workspaces":
                return new WorkspacesWidget(id)
                {
                    ActiveStyle = style with
                    {
                        Background = ParseColour(SettingText(node, "active-background"))
                            ?? new Colour(0x8D, 0xBC, 0xFF),
                        Foreground = ParseColour(SettingText(node, "active-colour")
                            ?? SettingText(node, "active-color"))
                            ?? new Colour(0x1E, 0x1E, 0x2E),
                        CornerRadius = 4,
                    },
                    OccupiedStyle = style,
                    EmptyStyle = style with { Foreground = style.Foreground.WithAlpha(110) },
                    HideEmpty = SettingBool(node, "hide-empty") ?? false,
                };

            case "spacer":
                return new SpacerWidget(
                    id,
                    SettingInt(node, "width"),
                    SettingDouble(node, "grow") ?? 1);

            case "text":
            {
                string? template = SettingText(node, "template") ?? node.Argument(0)?.AsString();

                if (template is null)
                {
                    diagnostics.Add(Diagnostic.Error(
                        "TAJ0005", "A text widget needs a template.", node.Span,
                        "Write text template=\"{{ clock }}\"."));
                    return null;
                }

                return new TemplateWidget(id, template, style, box)
                {
                    OnClick = SettingText(node, "on-click"),
                };
            }

            default:
                // Unknown nodes are ignored rather than fatal, so a config written
                // for a newer Taj still produces a working bar.
                return null;
        }
    }

    private static Colour? ParseColour(string? text) =>
        Colour.TryParse(text, out Colour colour) ? colour : null;

    /// <summary>Sources always available, without being declared.</summary>
    private static IEnumerable<SourceSpec> DefaultSources() =>
    [
        // Polled faster than it displays: Publish suppresses unchanged values, so a
        // clock showing minutes still only redraws once a minute.
        new SourceSpec("clock", "time", "HH:mm", TimeSpan.FromMilliseconds(500)),
        new SourceSpec("date", "time", "ddd d MMM", TimeSpan.FromSeconds(30)),
    ];

    /// <summary>
    /// A usable bar for someone who has configured nothing.
    /// </summary>
    /// <remarks>
    /// Workspaces on the left, window title in the middle, clock on the right - the
    /// arrangement nearly everyone builds anyway.
    /// </remarks>
    public static TajConfig CreateDefault()
    {
        var background = new Colour(0x1E, 0x1E, 0x2E);
        var foreground = new Colour(0xCD, 0xD6, 0xF4);
        var accent = new Colour(0x8D, 0xBC, 0xFF);

        var font = new FontStyle("Segoe UI", 12);
        var style = VisualStyle.Default with { Foreground = foreground, Font = font };
        var padded = new BoxStyle(Padding: Edges.Symmetric(6, 0));

        var profile = new BarProfile(
            "default",
            BarEdge.Top,
            26,
            background,
            Edges.Symmetric(6, 0),
            [
                new BarZone("left", JustifyContent.Start, 0, 4,
                [
                    new WorkspacesWidget("workspaces")
                    {
                        ActiveStyle = style with
                        {
                            Background = accent,
                            Foreground = background,
                            CornerRadius = 4,
                        },
                        OccupiedStyle = style,
                        EmptyStyle = style with { Foreground = foreground.WithAlpha(110) },
                    },
                ]),

                new BarZone("centre", JustifyContent.Center, 1, 6,
                [
                    new TemplateWidget(
                        "title", "{{ window.title | truncate:80 }}", style, padded),
                ]),

                new BarZone("right", JustifyContent.End, 0, 10,
                [
                    new TemplateWidget("mode", "{{ binding_mode }}",
                        style with { Foreground = new Colour(0xF9, 0xE2, 0xAF) }, padded),
                    new TemplateWidget("layout", "{{ layout }}",
                        style with { Foreground = foreground.WithAlpha(160) }, padded),
                    new TemplateWidget("date", "{{ date }}", style, padded),
                    new TemplateWidget("clock", "{{ clock }}",
                        style with { Font = font with { Bold = true } }, padded),
                ]),
            ]);

        return new TajConfig(
            new Dictionary<string, BarProfile>(StringComparer.OrdinalIgnoreCase) { ["default"] = profile },
            [],
            profile,
            [.. DefaultSources()]);
    }

    /// <summary>Creates the sources described by config.</summary>
    public static IEnumerable<ISource> CreateSources(IReadOnlyList<SourceSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);

        foreach (SourceSpec spec in specs)
        {
            switch (spec.Kind.ToLowerInvariant())
            {
                case "time":
                    yield return new ClockSource(
                        spec.Name, spec.Argument, spec.Interval, spec.TimeZone);
                    break;

                case "command":
                    if (spec.Argument.Length > 0)
                        yield return new ProcessSource(spec.Name, spec.Argument);
                    break;

                default:
                    Log.Warn(LogCategory.Config, $"unknown source kind '{spec.Kind}' for '{spec.Name}'");
                    break;
            }
        }
    }
}
