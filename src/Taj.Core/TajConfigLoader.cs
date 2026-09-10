using Shubbak.Config;
using Shubbak.Config.Kdl;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
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
    IReadOnlyList<SourceSpec> Sources)
{
    /// <summary>How long the bar waits for a window manager it has lost, by default.</summary>
    public static readonly TimeSpan DefaultWindowManagerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to keep waiting for the window manager after losing it, or null to
    /// wait for ever.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever counted once the bar has connected at least one time. A bar that has
    /// never reached a window manager waits indefinitely, because it is usually
    /// launched by the window manager's own startup command and can win the race.
    /// </para>
    /// <para>
    /// If the window manager comes back inside the window the bar reconnects and the
    /// clock resets, so restarting the daemon does not cost you the bar.
    /// </para>
    /// </remarks>
    public TimeSpan? WindowManagerTimeout { get; init; } = DefaultWindowManagerTimeout;
}

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

        WarnAboutUnknown(bar, KnownBarKeys, "setting in 'bar'", "TAJ0013", diagnostics);

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

        // The names the other half of the file declares, so a rule that matches on a
        // context nobody declared can be said to be one - the same check the window
        // manager's loader makes for a `context` command, from the same document. Read
        // as names only: the bar has no use for what a context does, and parsing that
        // here would mean two loaders disagreeing about one section.
        List<string> declaredContexts = DeclaredContexts(parsed.Document);

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

            WarnAboutUnknown(node, KnownBarRuleKeys, "setting on a bar rule", "TAJ0019", diagnostics);

            // A number is a position in the window manager's monitor list; anything
            // else is a name the window manager's configuration gives a display, and
            // the same word means the same screen in both halves of the file. A quoted
            // number is still a position - the loader for the other half makes the same
            // call, for the same reason: the value type is not a reliable signal of
            // what was meant.
            int? monitorIndex = null;
            string? monitorName = null;

            if (Setting(node, "monitor") is { } monitor)
            {
                if (monitor.TryAsInt(out int index) ||
                    int.TryParse(monitor.AsString(), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out index))
                {
                    monitorIndex = index;
                }
                else
                {
                    monitorName = monitor.AsString();
                }
            }

            string? context = SettingText(node, "context");

            // A warning, not an error, and the rule is kept: the rule is correct as
            // written and can never match, which is worth saying but not worth losing
            // the bar over. The window manager's loader gives an undeclared name in a
            // `context` command the same treatment.
            if (context is not null &&
                !declaredContexts.Contains(context, StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Add(Diagnostic.Warning(
                    "TAJ0020",
                    $"Bar rule matches on context '{context}', which the contexts section does not declare; " +
                    "it will never apply.",
                    Setting(node, "context")?.Span ?? node.Span,
                    declaredContexts.Count == 0
                        ? "No contexts are declared. Add a contexts { } section with a context of that name."
                        : Suggestion.Closest(context, declaredContexts) is { } guess
                            ? $"Did you mean '{guess}'?"
                            : $"Declared: {string.Join(", ", declaredContexts)}."));
            }

            rules.Add(new BarRule(
                profileName,
                SettingText(node, "workspace"),
                monitorIndex,
                monitorName,
                context));
        }

        WarnAboutUndeclaredContextSources(bar, declaredContexts, diagnostics);

        BarProfile fallbackProfile =
            profiles.TryGetValue("default", out BarProfile? named) ? named : profiles.Values.First();

        var config = new TajConfig(profiles, rules, fallbackProfile, sources)
        {
            WindowManagerTimeout = ParseWindowManagerTimeout(bar, diagnostics),
        };

        return (config, diagnostics);
    }

    /// <summary>
    /// Reads <c>window-manager-timeout</c>, in seconds, where zero waits for ever.
    /// </summary>
    private static TimeSpan? ParseWindowManagerTimeout(KdlNode bar, List<Diagnostic> diagnostics)
    {
        if (bar.Child("window-manager-timeout") is not { } node) return TajConfig.DefaultWindowManagerTimeout;

        if (node.Argument(0) is not { } value || !value.TryAsInt(out int seconds))
        {
            diagnostics.Add(Diagnostic.Warning(
                "TAJ0011",
                "'window-manager-timeout' must be a whole number of seconds.",
                node.Span,
                "Write window-manager-timeout 30, or 0 to keep the bar running for ever."));

            return TajConfig.DefaultWindowManagerTimeout;
        }

        if (seconds < 0)
        {
            diagnostics.Add(Diagnostic.Warning(
                "TAJ0012",
                $"'window-manager-timeout' cannot be negative ({seconds}).",
                node.Span,
                "Write 0 to keep the bar running for ever."));

            return TajConfig.DefaultWindowManagerTimeout;
        }

        // Zero is "no timeout" rather than "give up at once". A bar that vanished the
        // instant the window manager hiccupped would be worse than one that lingers.
        return seconds == 0 ? null : TimeSpan.FromSeconds(seconds);
    }

    private static SourceSpec? ParseSource(KdlNode node, List<Diagnostic> diagnostics)
    {
        string? name = node.Argument(0)?.AsString();

        if (name is null)
        {
            diagnostics.Add(Diagnostic.Error("TAJ0003", "A source must be named.", node.Span));
            return null;
        }

        WarnAboutUnknown(node, KnownSourceKeys, "setting on a source", "TAJ0018", diagnostics);

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

    /// <summary>Children of <c>bar</c> the loader understands.</summary>
    private static readonly string[] KnownBarKeys =
        ["source", "profile", "rule", "window-manager-timeout"];

    /// <summary>Settings a <c>profile</c> understands, as a child or a property.</summary>
    private static readonly string[] KnownProfileKeys =
        ["extends", "edge", "height", "background", "foreground", "font", "font-size", "padding", "zone"];

    /// <summary>What may appear inside a <c>zone</c>: its settings, and the widgets.</summary>
    private static readonly string[] KnownZoneKeys =
        ["justify", "grow", "gap", "workspaces", "spacer", "text"];

    /// <summary>Styling every widget accepts, whatever kind it is.</summary>
    /// <remarks>
    /// Both spellings of colour throughout, because the config is read by people who
    /// write one or the other and being told off for either would be absurd.
    /// </remarks>
    private static readonly string[] CommonWidgetKeys =
        ["id", "font", "font-size", "bold", "italic", "colour", "color", "background", "radius"];

    private static readonly string[] KnownWorkspacesKeys =
    [
        .. CommonWidgetKeys,
        "active-background", "active-colour", "active-color",
        "focused-background", "focused-colour", "focused-color",
        "empty-colour", "empty-color",
        "hover-background", "hover-colour", "hover-color",
        "hide-empty",
    ];

    private static readonly string[] KnownSpacerKeys = [.. CommonWidgetKeys, "width", "grow"];

    private static readonly string[] KnownTextKeys =
        [.. CommonWidgetKeys, "template", "on-click", "when", "hover-background", "hover-colour", "hover-color"];

    /// <summary>What a <c>when</c> block accepts: what it matches, and what it restates.</summary>
    private static readonly string[] KnownConditionKeys =
        ["value", "not", "of", "font", "font-size", "bold", "italic", "colour", "color", "background"];

    private static readonly string[] KnownSourceKeys =
        ["kind", "format", "command", "interval", "timezone"];

    private static readonly string[] KnownBarRuleKeys = ["use", "workspace", "monitor", "context"];

    /// <summary>
    /// Reports a <c>{{ context.x }}</c> or <c>when of="context.x"</c> that names a context
    /// the <c>contexts</c> section does not declare.
    /// </summary>
    /// <remarks>
    /// The same check a rule's <c>context=</c> gets, for the same reason: the widget is
    /// correct as written and will never show anything, and the symptom - an icon that
    /// never appears - looks exactly like a widget that was never written. A warning,
    /// because the bar still builds. Walked over the KDL rather than the built widgets
    /// so it needs nothing the widgets do not already say.
    /// </remarks>
    private static void WarnAboutUndeclaredContextSources(
        KdlNode bar, List<string> declaredContexts, List<Diagnostic> diagnostics)
    {
        foreach (KdlNode profile in bar.ChildrenNamed("profile"))
        {
            foreach (KdlNode zone in profile.ChildrenNamed("zone"))
            {
                foreach (KdlNode widget in zone.ChildrenNamed("text"))
                {
                    if ((SettingText(widget, "template") ?? widget.Argument(0)?.AsString()) is { } template)
                    {
                        foreach (string source in Template.Dependencies(template))
                            Check(source, Setting(widget, "template")?.Span ?? widget.Span);
                    }

                    foreach (KdlNode condition in widget.ChildrenNamed("when"))
                    {
                        if (Setting(condition, "of") is { } of) Check(of.AsString(), of.Span);
                    }
                }
            }
        }

        void Check(string source, TextSpan span)
        {
            if (!source.StartsWith(ActiveContexts.KeyPrefix, StringComparison.Ordinal)) return;

            string name = source[ActiveContexts.KeyPrefix.Length..];
            if (declaredContexts.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

            diagnostics.Add(Diagnostic.Warning(
                "TAJ0021",
                $"'{source}' reads context '{name}', which the contexts section does not declare; it will always be empty.",
                span,
                declaredContexts.Count == 0
                    ? "No contexts are declared. Add a contexts { } section with a context of that name."
                    : Suggestion.Closest(name, declaredContexts) is { } guess
                        ? $"Did you mean 'context.{guess}'?"
                        : $"Declared: {string.Join(", ", declaredContexts)}."));
        }
    }

    /// <summary>
    /// The names the <c>contexts</c> section declares, in order, from the same document
    /// the bar was read from.
    /// </summary>
    /// <remarks>
    /// Names only, and nothing is reported about the section: whether a context is
    /// well-formed is the window manager's loader's business, and it says so with its
    /// own codes. Reporting it here as well would show every mistake twice.
    /// </remarks>
    private static List<string> DeclaredContexts(KdlDocument document)
    {
        if (document.Node("contexts") is not { } section) return [];

        List<string> names = [];

        foreach (KdlNode context in section.ChildrenNamed("context"))
            if (context.Argument(0)?.AsString() is { Length: > 0 } name)
                names.Add(name);

        return names;
    }

    /// <summary>The settings a widget of the given kind accepts, or null if it is not a widget.</summary>
    /// <remarks>
    /// Null for an unrecognised kind, so nothing is said about its properties. The
    /// zone that contains it has already reported the kind itself, and adding a
    /// complaint about every property on a node the user has been told is wrong
    /// buries the one that matters.
    /// </remarks>
    private static string[]? KnownKeysFor(string widget) => widget switch
    {
        "workspaces" => KnownWorkspacesKeys,
        "spacer" => KnownSpacerKeys,
        "text" => KnownTextKeys,
        _ => null,
    };

    /// <summary>
    /// Reports anything the loader does not recognise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Taj used to drop unknown nodes without a word, on the grounds that a config
    /// written for a newer Taj should still produce a working bar. That is a good
    /// reason to keep loading, and no reason at all to keep quiet: the overwhelmingly
    /// more common case is not a config from the future but a typo, and the symptom is
    /// a setting that appears to have no effect.
    /// </para>
    /// <para>
    /// So they stay warnings rather than errors - the bar still builds - and they say
    /// what was probably meant. The same treatment the window manager's own loader
    /// gives its sections and settings, which this file had never had.
    /// </para>
    /// <para>
    /// Both children and properties, because a setting can be written either way.
    /// </para>
    /// </remarks>
    private static void WarnAboutUnknown(
        KdlNode node, string[] known, string what, string code, List<Diagnostic> diagnostics)
    {
        foreach (KdlNode child in node.Children) Warn(child.Name, child.Span);

        foreach ((string name, KdlValue value) in node.Properties) Warn(name, value.Span);

        void Warn(string name, TextSpan span)
        {
            if (known.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

            string? guess = Suggestion.Closest(name, known);

            diagnostics.Add(Diagnostic.Warning(
                code,
                $"Unknown {what} '{name}'; it will be ignored.",
                span,
                guess is null ? null : $"Did you mean '{guess}'?"));
        }
    }

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

        WarnAboutUnknown(node, KnownProfileKeys, "setting in a profile", "TAJ0014", diagnostics);

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

        WarnAboutUnknown(node, KnownZoneKeys, "widget or setting in a zone", "TAJ0015", diagnostics);

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
        KdlNode node, VisualStyle baseStyle, FontStyle baseFont, List<Diagnostic> diagnostics)
    {
        List<WidgetCondition> conditions = [];

        foreach (KdlNode child in node.ChildrenNamed("when"))
        {
            WarnAboutUnknown(child, KnownConditionKeys, "setting in a 'when' block", "TAJ0017", diagnostics);

            // `value` matches, `not` matches everything else. One or the other, and
            // the bare argument form spells `value`.
            string? negated = SettingText(child, "not");
            string? value = negated ?? SettingText(child, "value") ?? child.Argument(0)?.AsString();

            if (value is null) continue;

            var font = baseFont with
            {
                Family = SettingText(child, "font") ?? baseFont.Family,
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

            conditions.Add(new WidgetCondition(
                value,
                style,
                Negate: negated is not null,
                Source: SettingText(child, "of")));
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
        // The family too, per widget: an icon font on one widget - Segoe Fluent
        // Icons for a camera glyph - beside text in the profile's face on the rest.
        var widgetFont = font with
        {
            Family = SettingText(node, "font") ?? font.Family,
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

        if (KnownKeysFor(node.Name) is { } widgetKeys)
            WarnAboutUnknown(node, widgetKeys, $"setting on a '{node.Name}' widget", "TAJ0016", diagnostics);

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

                string? onClick = SettingText(node, "on-click");
                Colour? hoverBackground = ParseColour(SettingText(node, "hover-background"));
                Colour? hoverForeground = ParseColour(SettingText(node, "hover-colour") ?? SettingText(node, "hover-color"));

                // A hover on something that cannot be clicked would claim it can be.
                if (onClick is null && (hoverBackground is not null || hoverForeground is not null))
                {
                    diagnostics.Add(Diagnostic.Warning(
                        "TAJ0022",
                        $"'{id}' has a hover colour but no on-click; only a clickable widget reacts to the pointer.",
                        node.Span,
                        "Add on-click=\"...\" to make it a control, or drop the hover settings."));
                }

                return new TemplateWidget(id, template, style, box)
                {
                    OnClick = onClick,
                    Conditions = ParseConditions(node, style, widgetFont, diagnostics),
                    HoverStyle = hoverBackground is null && hoverForeground is null
                        ? null
                        : VisualStyle.Default with
                        {
                            Background = hoverBackground ?? Colour.Transparent,
                            Foreground = hoverForeground ?? Colour.Transparent,
                        },
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
                    // Says "suspended" or "paused" and is otherwise absent, because an
                    // empty template hides its widget. Both states change what Shubbak
                    // does without changing anything on screen - a suspended window
                    // manager looks exactly like a crashed one until you press a key -
                    // so this is the only warning the user gets.
                    //
                    // First in the zone and in red, deliberately. It is the one thing
                    // here that means something is not working.
                    new TemplateWidget("status", "{{ status }}",
                        style with { Foreground = new Colour(0xF3, 0x8B, 0xA8) }, padded),
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
