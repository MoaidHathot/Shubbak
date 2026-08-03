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

        // Declared sources are collected first, then the built-ins fill in whatever
        // was not named. The other order silently discards the user's version: the
        // model registers by name and keeps the first, so declaring `clock` with a
        // date in it changed nothing and gave no reason why.
        List<SourceSpec> sources = [];
        HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);

        foreach (KdlNode node in bar.ChildrenNamed("source"))
        {
            if (ParseSource(node, diagnostics) is not { } spec) continue;

            if (!declared.Add(spec.Name))
            {
                diagnostics.Add(Diagnostic.Warning(
                    "TAJ0006",
                    $"Source '{spec.Name}' is declared more than once.",
                    node.Span,
                    "The first declaration is used. Remove the duplicate, or rename it."));

                continue;
            }

            sources.Add(spec);
        }

        foreach (SourceSpec builtin in DefaultSources())
            if (!declared.Contains(builtin.Name)) sources.Add(builtin);

        // Typography is baked into each widget at parse time, so it is not on
        // BarProfile to be read back. Carried alongside instead, or a profile that
        // extends another would silently fall back to the built-in defaults - which
        // is what made a variant render in a smaller font than the one it inherited
        // from, for no reason visible in the config.
        Dictionary<string, ProfileText> text = new(StringComparer.OrdinalIgnoreCase);

        foreach (KdlNode node in bar.ChildrenNamed("profile"))
        {
            BarProfile? profile = ParseProfile(node, profiles, text, diagnostics);
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

    /// <summary>Profile settings that widgets absorb and BarProfile does not keep.</summary>
    private readonly record struct ProfileText(Colour Foreground, FontStyle Font, int Padding);

    private static BarProfile? ParseProfile(
        KdlNode node,
        Dictionary<string, BarProfile> existing,
        Dictionary<string, ProfileText> inheritedText,
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

        // Everything below inherits from the profile being extended. Falling back to
        // the built-in defaults instead makes a variant differ from its parent in
        // ways the config never mentions - a slimmer bar that also, inexplicably,
        // used a smaller font and a different text colour.
        ProfileText inherited =
            SettingText(node, "extends") is { } from && inheritedText.TryGetValue(from, out ProfileText inheritedFound)
                ? inheritedFound
                : new ProfileText(
                    new Colour(0xCD, 0xD6, 0xF4),
                    new FontStyle("Segoe UI", 12),
                    6);

        Colour foreground = ParseColour(SettingText(node, "foreground")) ?? inherited.Foreground;

        var font = new FontStyle(
            SettingText(node, "font") ?? inherited.Font.Family,
            SettingInt(node, "font-size") ?? inherited.Font.Size);

        // Zones are merged with the parent's by id, not substituted for them.
        //
        // Replacing wholesale contradicts what `extends` is for. A variant that
        // redefined only "left" and "right" lost the inherited "centre" - and with it
        // the only zone that grows, so the remaining zones packed against the left
        // edge and the clock appeared on the wrong side of the bar. The failure was
        // in the layout rather than the zone that went missing, which made it look
        // like an alignment bug.
        //
        // To empty a zone rather than inherit it, redeclare it with no widgets.
        List<BarZone> zones = parent is not null ? [.. parent.Zones] : [];

        foreach (KdlNode zoneNode in node.ChildrenNamed("zone"))
        {
            BarZone? zone = ParseZone(zoneNode, foreground, font, diagnostics);
            if (zone is null) continue;

            int existingIndex = zones.FindIndex(z =>
                string.Equals(z.Id, zone.Id, StringComparison.OrdinalIgnoreCase));

            // Overridden in place, so the inherited order survives: a redefined
            // "right" stays on the right rather than moving to wherever it was
            // written in the file.
            if (existingIndex >= 0) zones[existingIndex] = zone;
            else zones.Add(zone);
        }

        int padding = SettingInt(node, "padding") ?? inherited.Padding;

        // Recorded so a profile extending this one inherits what was resolved here,
        // rather than what was written here - inheritance should chain.
        inheritedText[name] = new ProfileText(foreground, font, padding);

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

    /// <summary>
    /// Reads the <c>when value="…"</c> children of a text widget.
    /// </summary>
    /// <remarks>
    /// Each one restates only what differs, inheriting everything else from the
    /// widget, so marking a value usually costs a colour and nothing more.
    /// </remarks>
    private static List<WidgetCondition> ParseConditions(
        KdlNode node, VisualStyle baseStyle, FontStyle baseFont)
    {
        List<WidgetCondition> conditions = [];

        foreach (KdlNode child in node.ChildrenNamed("when"))
        {
            string? value = SettingText(child, "value") ?? child.Argument(0)?.AsString();
            if (value is null) continue;

            var font = baseFont with
            {
                Size = SettingInt(child, "font-size") ?? baseFont.Size,
                Bold = SettingBool(child, "bold") ?? baseFont.Bold,
                Italic = SettingBool(child, "italic") ?? baseFont.Italic,
            };

            var style = baseStyle with
            {
                Foreground =
                    ParseColour(SettingText(child, "colour") ?? SettingText(child, "color"))
                    ?? baseStyle.Foreground,
                Background =
                    ParseColour(SettingText(child, "background")) ?? baseStyle.Background,
                Font = font,
            };

            conditions.Add(new WidgetCondition(value, style));
        }

        return conditions;
    }

    private static IWidget? ParseWidget(
        KdlNode node, Colour foreground, FontStyle font, List<Diagnostic> diagnostics)
    {
        string id = SettingText(node, "id") ?? node.Name;

        // Typography per widget, not only per profile. The model and the renderer
        // have always supported size, weight and slant - the built-in default profile
        // bolds its own clock - but no config key reached them, so a user's config
        // could not reproduce what Taj shipped with.
        var widgetFont = font with
        {
            Size = SettingInt(node, "font-size") ?? font.Size,
            Bold = SettingBool(node, "bold") ?? font.Bold,
            Italic = SettingBool(node, "italic") ?? font.Italic,
        };

        var style = VisualStyle.Default with
        {
            Foreground = ParseColour(SettingText(node, "colour") ?? SettingText(node, "color")) ?? foreground,
            Background = ParseColour(SettingText(node, "background")) ?? Colour.Transparent,
            Font = widgetFont,
            CornerRadius = SettingInt(node, "radius") ?? 0,
        };

        var box = new BoxStyle(Padding: Edges.Symmetric(6, 0));

        switch (node.Name)
        {
            case "workspaces":
            {
                VisualStyle activeStyle = style with
                {
                    Background = ParseColour(SettingText(node, "active-background"))
                        ?? new Colour(0x8D, 0xBC, 0xFF),
                    Foreground = ParseColour(SettingText(node, "active-colour")
                        ?? SettingText(node, "active-color"))
                        ?? new Colour(0x1E, 0x1E, 0x2E),
                    CornerRadius = SettingInt(node, "radius") ?? 4,
                };

                // Only built when asked for. Falling back to the active style is
                // right on a single monitor, where the focused workspace and the
                // displayed one are never different.
                Colour? focusedColour = ParseColour(
                    SettingText(node, "focused-colour") ?? SettingText(node, "focused-color"));

                Colour? focusedBackground = ParseColour(SettingText(node, "focused-background"));

                VisualStyle? focusedStyle = focusedColour is null && focusedBackground is null
                    ? null
                    : activeStyle with
                    {
                        Foreground = focusedColour ?? activeStyle.Foreground,
                        Background = focusedBackground ?? activeStyle.Background,
                    };

                return new WorkspacesWidget(id)
                {
                    ActiveStyle = activeStyle,
                    FocusedStyle = focusedStyle,
                    OccupiedStyle = style,
                    EmptyStyle = style with
                    {
                        Foreground = ParseColour(SettingText(node, "empty-colour")
                            ?? SettingText(node, "empty-color"))
                            ?? style.Foreground.WithAlpha(110),
                    },
                    HoverStyle = style with
                    {
                        Foreground = ParseColour(SettingText(node, "hover-colour")
                            ?? SettingText(node, "hover-color"))
                            ?? style.Foreground,
                        Background = ParseColour(SettingText(node, "hover-background"))
                            ?? new Colour(0xFF, 0xFF, 0xFF, 0x1A),
                        CornerRadius = SettingInt(node, "radius") ?? 4,
                    },
                    HideEmpty = SettingBool(node, "hide-empty") ?? false,
                };
            }

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
                    Conditions = ParseConditions(node, style, widgetFont),
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
    /// <param name="specs">The declared sources.</param>
    /// <param name="keyboardLanguage">
    /// Reads the current input language, supplied by the host.
    /// </param>
    /// <remarks>
    /// The keyboard language cannot be read without Win32, and this assembly is
    /// deliberately free of it - it is the part that can be tested without a desktop.
    /// The host passes the reader in, so this stays the single place that knows what
    /// kinds of source exist.
    /// </remarks>
    public static IEnumerable<ISource> CreateSources(
        IReadOnlyList<SourceSpec> specs, Func<string>? keyboardLanguage = null)
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

                case "keyboard":
                    if (keyboardLanguage is null)
                    {
                        Log.Warn(LogCategory.Config,
                            $"source '{spec.Name}': the keyboard language is not available on this host");
                        break;
                    }

                    yield return new IntervalSource(spec.Name, spec.Interval, keyboardLanguage);
                    break;

                default:
                    Log.Warn(LogCategory.Config, $"unknown source kind '{spec.Kind}' for '{spec.Name}'");
                    break;
            }
        }
    }
}
