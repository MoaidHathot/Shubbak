using Shubbak.Config;
using Shubbak.Config.Kdl;
using Shubbak.Core.Commands;
using Shubbak.Core.Rendering;

namespace Dalil.Core;

/// <summary>
/// Reads the <c>dalil</c> section of the shared configuration.
/// </summary>
/// <remarks>
/// <para>
/// The same file the window manager and the bar read, because a user who has to
/// remember which of three files a setting lives in has been given a filing system
/// rather than a configuration.
/// </para>
/// <para>
/// Every setting is optional and every default is a working palette. A feature that
/// does nothing until it has been configured is a feature nobody tries.
/// </para>
/// <para>
/// Two entry points, deliberately. <see cref="Load(string)"/> is what the palette
/// itself calls and never complains: a broken setting degrades to a working default
/// rather than to no palette. <see cref="Validate"/> is what <c>shubbak check-config</c>
/// calls and reports everything, with a line, a column and a caret. Until it existed
/// the <c>dalil</c> section was checked by nothing at all - the section name was on the
/// allow-list and its contents were not, so a misspelt setting produced no diagnostic
/// here and no diagnostic there, and the only symptom was a setting that appeared to do
/// nothing. That is precisely the failure this project exists to not have.
/// </para>
/// </remarks>
public static class DalilConfigLoader
{
    /// <summary>Settings a <c>dalil</c> block understands.</summary>
    /// <remarks>
    /// Listed so a misspelt key can be reported rather than silently ignored -
    /// the same reason every other section in this configuration keeps one.
    /// </remarks>
    public static IReadOnlyList<string> KnownKeys { get; } =
    [
        "open-on-signal", "width", "row-height", "visible-rows", "close-on-blur",
        "show-unmanaged", "confirm-destructive", "action-guard", "show-icons",
        "shrink-to-fit", "placement", "background", "foreground", "match",
        "secondary", "selection-background", "border", "danger", "font", "font-size",
    ];

    /// <summary>Blocks a <c>dalil</c> section can contain, as opposed to settings.</summary>
    /// <remarks>
    /// Kept apart from <see cref="KnownKeys"/> because these take children rather than
    /// a value, and a validator checking "is this a known setting" would otherwise
    /// report every one of them as a misspelling.
    /// </remarks>
    public static IReadOnlyList<string> KnownBlocks { get; } = ["prefixes", "action"];

    /// <summary>Reads the section from a configuration file.</summary>
    public static DalilConfig LoadFile(string path) => Load(File.ReadAllText(path));

    /// <summary>Reads the section from configuration text, reporting nothing.</summary>
    public static DalilConfig Load(string source) => Read(source, diagnostics: null);

    /// <summary>
    /// Reads the section and says everything that is wrong with it.
    /// </summary>
    /// <remarks>
    /// The KDL parser's own diagnostics are deliberately not repeated here. The window
    /// manager's loader reads the same text and reports them, and
    /// <c>shubbak check-config</c> runs both - so including them would print every
    /// syntax error twice.
    /// </remarks>
    public static (DalilConfig Config, IReadOnlyList<Diagnostic> Diagnostics) Validate(string source)
    {
        List<Diagnostic> diagnostics = [];

        return (Read(source, diagnostics), diagnostics);
    }

    private static DalilConfig Read(string source, List<Diagnostic>? diagnostics)
    {
        KdlParseResult parsed = KdlParser.Parse(source);

        // A file that does not parse is the window manager's problem to report, with
        // carets and hints. Falling back to defaults here means a broken config
        // degrades to a working palette rather than to no palette, and the user is
        // already being told what is wrong by the thing that owns the file.
        if (parsed.HasErrors) return new DalilConfig();

        return parsed.Document.Node("dalil") is { } node
            ? Read(node, diagnostics)
            : new DalilConfig();
    }

    private static DalilConfig Read(KdlNode node, List<Diagnostic>? diagnostics)
    {
        var defaults = new DalilConfig();

        WarnAboutUnknown(node, diagnostics);

        return new DalilConfig
        {
            OpenOnSignal = Text(node, "open-on-signal") ?? defaults.OpenOnSignal,

            // Clamped rather than trusted. A palette one pixel wide is not a
            // configuration anybody meant, and it is indistinguishable on screen from
            // the process having failed to start. Said out loud, too: silently using a
            // number nobody wrote is the same class of problem as ignoring one.
            Width = Clamp(node, "width", 240, 2400, defaults.Width, diagnostics),
            RowHeight = Clamp(node, "row-height", 16, 120, defaults.RowHeight, diagnostics),
            VisibleRows = Clamp(node, "visible-rows", 1, 40, defaults.VisibleRows, diagnostics),
            FontSize = Clamp(node, "font-size", 6, 48, defaults.FontSize, diagnostics),

            CloseOnBlur = Boolean(node, "close-on-blur") ?? defaults.CloseOnBlur,
            ShowUnmanaged = Boolean(node, "show-unmanaged") ?? defaults.ShowUnmanaged,
            ShowIcons = Boolean(node, "show-icons") ?? defaults.ShowIcons,
            ShrinkToFit = Boolean(node, "shrink-to-fit") ?? defaults.ShrinkToFit,

            // The old name is still read, and still means what somebody who wrote it
            // meant: ask before doing something irreversible. It no longer disables
            // the eight reversible chords it used to take down with it, which is the
            // whole of the change and is not something anybody configured on purpose.
            ConfirmDestructive =
                Boolean(node, "confirm-destructive") ??
                Boolean(node, "action-guard") ??
                defaults.ConfirmDestructive,

            Placement = ParsePlacement(node, diagnostics) ?? defaults.Placement,

            Prefixes = ReadPrefixes(node, diagnostics),
            Macros = ReadMacros(node, diagnostics),

            Background = Colour(node, "background", diagnostics) ?? defaults.Background,
            Foreground = Colour(node, "foreground", diagnostics) ?? defaults.Foreground,
            Match = Colour(node, "match", diagnostics) ?? defaults.Match,
            Secondary = Colour(node, "secondary", diagnostics) ?? defaults.Secondary,
            SelectionBackground = Colour(node, "selection-background", diagnostics) ?? defaults.SelectionBackground,
            Border = Colour(node, "border", diagnostics) ?? defaults.Border,
            Danger = Colour(node, "danger", diagnostics) ?? defaults.Danger,

            FontFamily = Text(node, "font") ?? defaults.FontFamily,
        };
    }

    /// <summary>
    /// Reports settings the palette will never look at.
    /// </summary>
    /// <remarks>
    /// Both shapes are accepted everywhere in this configuration - a setting can be
    /// written as a child node or as a property - so both have to be checked, or half
    /// the misspellings would go unreported depending on which style the user prefers.
    /// </remarks>
    private static void WarnAboutUnknown(KdlNode node, List<Diagnostic>? diagnostics)
    {
        if (diagnostics is null) return;

        string[] everything = [.. KnownKeys, .. KnownBlocks];

        foreach (KdlNode child in node.Children)
        {
            if (everything.Contains(child.Name, StringComparer.OrdinalIgnoreCase)) continue;

            diagnostics.Add(Diagnostic.Warning(
                "DAL0001",
                $"Unknown setting '{child.Name}' in 'dalil'; it will be ignored.",
                child.NameSpan,
                Suggestion.Closest(child.Name, everything) is { } guess
                    ? $"Did you mean '{guess}'?"
                    : null));
        }

        foreach ((string name, KdlValue value) in node.Properties)
        {
            if (everything.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            diagnostics.Add(Diagnostic.Warning(
                "DAL0001",
                $"Unknown setting '{name}' in 'dalil'; it will be ignored.",
                value.Span,
                Suggestion.Closest(name, everything) is { } guess
                    ? $"Did you mean '{guess}'?"
                    : null));
        }
    }

    /// <summary>
    /// Reads a <c>prefixes</c> block: which character selects which mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed by the mode's own name, which is the name the hint bar shows and the name
    /// <c>signal "palette" "layouts"</c> already uses, so there is nothing new to
    /// learn:
    /// </para>
    /// <code>
    /// prefixes {
    ///     layouts "l"
    ///     monitors "m"
    /// }
    /// </code>
    /// <para>
    /// An empty string removes a prefix rather than being an error. A mode with no
    /// prefix is still reachable by Tab and by its jump key, so this is a way of
    /// freeing a character up rather than a way of losing a mode.
    /// </para>
    /// </remarks>
    private static Dictionary<PaletteMode, char> ReadPrefixes(
        KdlNode node, List<Diagnostic>? diagnostics)
    {
        Dictionary<PaletteMode, char> table = [];

        if (node.Child("prefixes") is not { } block) return table;

        // Where each explicit assignment was written, so a clash can be reported at the
        // line that caused it rather than at the block.
        Dictionary<PaletteMode, TextSpan> where = [];

        foreach (KdlNode child in block.Children)
        {
            if (PaletteModel.ModeNamed(child.Name) is not { } mode)
            {
                diagnostics?.Add(Diagnostic.Warning(
                    "DAL0002",
                    $"'{child.Name}' is not a palette mode; this prefix will be ignored.",
                    child.NameSpan,
                    $"Modes: {string.Join(", ", ModeNames())}."));

                continue;
            }

            KdlValue? spelled = child.Argument(0);
            string spelling = spelled?.AsString() ?? string.Empty;
            TextSpan span = spelled?.Span ?? child.Span;

            // Exactly one character, or none. A two-character prefix is not something
            // this can honour - the mode is decided by looking at the first character
            // of the query - so accepting it would mean silently using the first
            // letter of whatever was written.
            if (spelling.Length > 1)
            {
                diagnostics?.Add(Diagnostic.Warning(
                    "DAL0003",
                    $"The prefix for '{child.Name}' must be a single character, not \"{spelling}\".",
                    span,
                    "The mode is chosen from the first character typed, so a longer " +
                    "prefix could never match. Write \"\" to give the prefix up entirely."));

                table[mode] = '\0';
                where[mode] = span;
                continue;
            }

            table[mode] = spelling.Length == 1 ? spelling[0] : '\0';
            where[mode] = span;
        }

        ReportDisplacedPrefixes(table, where, diagnostics);

        return table;
    }

    /// <summary>
    /// Reports the modes that quietly lost their prefix to somebody else's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved through <see cref="PalettePrefixes.With"/> rather than worked out again
    /// here, so the warning cannot disagree with what the palette will actually do. An
    /// approximation was tried first and was wrong in the obvious case: reading the
    /// block a line at a time, swapping two prefixes over looks like a clash at the
    /// first line, because the mode that is about to give the character up has not got
    /// there yet.
    /// </para>
    /// <para>
    /// Only modes the user did not mention are worth reporting. Somebody who wrote
    /// <c>monitors ""</c> asked for that mode to have no prefix and does not need
    /// telling.
    /// </para>
    /// </remarks>
    private static void ReportDisplacedPrefixes(
        Dictionary<PaletteMode, char> explicitly,
        Dictionary<PaletteMode, TextSpan> where,
        List<Diagnostic>? diagnostics)
    {
        if (diagnostics is null || explicitly.Count == 0) return;

        PalettePrefixes resolved = PalettePrefixes.With(explicitly);

        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
        {
            if (explicitly.ContainsKey(mode)) continue;

            char had = PalettePrefixes.Default.PrefixFor(mode);
            if (had == '\0' || resolved.PrefixFor(mode) != '\0') continue;

            // Whoever took it. There is exactly one, because the resolver refuses to
            // let two modes hold one character.
            PaletteMode thief = explicitly.First(pair => pair.Value == had).Key;

            diagnostics.Add(Diagnostic.Warning(
                "DAL0004",
                $"'{had}' is the prefix for '{PaletteModel.NameOf(mode)}', which will now have none.",
                where[thief],
                $"Two modes cannot share a character. Give '{PaletteModel.NameOf(mode)}' " +
                "another one in this block, or reach it with Tab or its Ctrl+digit."));
        }
    }

    private static IEnumerable<string> ModeNames() =>
        Enum.GetValues<PaletteMode>().Select(PaletteModel.NameOf);

    /// <summary>
    /// Reads the named command sequences.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each <c>action</c> is a name and a block of commands, written exactly the way a
    /// keybinding's commands are written - one command per child node, arguments as
    /// arguments - so there is no second syntax to learn and no quoting rules that
    /// differ from the ones next door.
    /// </para>
    /// <code>
    /// action "Dev layout" description="Two panes on 2" {
    ///     focus --workspace "2"
    ///     layout --set "master-left"
    ///     equalise
    /// }
    /// </code>
    /// <para>
    /// Validated here with the real parser rather than sent hopefully down the pipe.
    /// The palette already does this for what the user types in commands mode, and for
    /// the same reason: a mistake should be reported in the words the config file would
    /// have used, at the moment it can still be read, rather than as silence.
    /// </para>
    /// </remarks>
    private static List<PaletteMacro> ReadMacros(KdlNode node, List<Diagnostic>? diagnostics)
    {
        List<PaletteMacro> macros = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (KdlNode action in node.ChildrenNamed("action"))
        {
            if (action.Argument(0)?.AsString() is not { Length: > 0 } name)
            {
                diagnostics?.Add(Diagnostic.Error(
                    "DAL0005",
                    "A palette action must be given a name.",
                    action.Span,
                    "Write action \"Dev layout\" { focus --workspace \"2\" }."));

                continue;
            }

            if (!seen.Add(name))
            {
                diagnostics?.Add(Diagnostic.Warning(
                    "DAL0006",
                    $"Palette action '{name}' is declared more than once; both will be listed.",
                    action.Span,
                    "Two rows with the same name cannot be told apart in the list."));
            }

            List<string> commands = [];
            string? problem = null;

            foreach (KdlNode child in action.Children)
            {
                // The description can be written as a child rather than a property,
                // like every other setting in this file, so it is not a command.
                if (string.Equals(child.Name, "description", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Tokens are passed through directly rather than rebuilt into a string
                // and re-split. Re-splitting would destroy any argument containing a
                // quote - and the author's config has a workspace named `'`.
                List<string> tokens = [child.Name];

                foreach (KdlValue argument in child.Arguments)
                    tokens.Add(argument.AsString());

                string display = string.Join(' ', tokens);

                if (CommandParser.TryParseTokens(
                        tokens, display, child.Span, out WmCommand? _, out Diagnostic? error))
                {
                    commands.Add(display);
                    continue;
                }

                // Reported in the parser's own words, at the line it happened on.
                // Writing a friendlier message here would mean two vocabularies for
                // the same mistake - one in an action and a different one in a
                // keybinding three sections up.
                Diagnostic wrong = error!;

                diagnostics?.Add(Diagnostic.Error(
                    "DAL0007",
                    $"Palette action '{name}': {wrong.Message}",
                    wrong.Span,
                    wrong.Hint));

                // The first mistake, for the row itself. Showing every one of them on a
                // single line would mean showing none of them legibly.
                problem ??= wrong.Hint is { Length: > 0 } hint
                    ? $"{wrong.Message}  {hint}"
                    : wrong.Message;
            }

            if (commands.Count == 0 && problem is null)
            {
                diagnostics?.Add(Diagnostic.Warning(
                    "DAL0008",
                    $"Palette action '{name}' has no commands; it will do nothing.",
                    action.Span));

                continue;
            }

            macros.Add(new PaletteMacro(
                name,
                action.Property("description")?.AsString() ??
                    action.Child("description")?.Argument(0)?.AsString() ??
                    string.Empty,
                commands,
                problem));
        }

        return macros;
    }

    private static PalettePlacement? ParsePlacement(KdlNode node, List<Diagnostic>? diagnostics)
    {
        if (Setting(node, "placement") is not { } value) return null;

        PalettePlacement? placement = value.AsString().ToLowerInvariant() switch
        {
            "focused-monitor" or "focused" => PalettePlacement.FocusedMonitor,
            "cursor-monitor" or "cursor" => PalettePlacement.CursorMonitor,
            "primary" => PalettePlacement.Primary,
            _ => null,
        };

        if (placement is null)
        {
            diagnostics?.Add(Diagnostic.Warning(
                "DAL0009",
                $"Unknown placement '{value.AsString()}'; the palette will open on the focused monitor.",
                value.Span,
                "Available: focused-monitor, cursor-monitor, primary."));
        }

        return placement;
    }

    /// <summary>Accepted as a child node or as a property, like every other section.</summary>
    private static KdlValue? Setting(KdlNode node, string name) =>
        node.Child(name)?.Argument(0) ?? node.Property(name);

    private static string? Text(KdlNode node, string name) => Setting(node, name)?.AsString();

    private static bool? Boolean(KdlNode node, string name) =>
        Setting(node, name) is { } value && value.TryAsBool(out bool result) ? result : null;

    private static Colour? Colour(KdlNode node, string name, List<Diagnostic>? diagnostics)
    {
        if (Setting(node, name) is not { } value) return null;

        if (Shubbak.Core.Rendering.Colour.TryParse(value.AsString(), out Colour colour))
            return colour;

        diagnostics?.Add(Diagnostic.Warning(
            "DAL0010",
            $"'{value.AsString()}' is not a colour; '{name}' will keep its default.",
            value.Span,
            "Write a hex colour such as \"#16161C\" or \"#16161CFF\"."));

        return null;
    }

    /// <summary>
    /// Reads a number, and says so when it was not usable as written.
    /// </summary>
    /// <remarks>
    /// Clamping in silence is how a palette ends up 240 pixels wide for somebody who
    /// asked for 24 and is now looking for the bug in the wrong place.
    /// </remarks>
    private static int Clamp(
        KdlNode node, string name, int low, int high, int fallback, List<Diagnostic>? diagnostics)
    {
        if (Setting(node, name) is not { } value) return fallback;

        if (!value.TryAsInt(out int given))
        {
            diagnostics?.Add(Diagnostic.Warning(
                "DAL0011",
                $"'{name}' must be a whole number; it will keep its default of {fallback}.",
                value.Span));

            return fallback;
        }

        if (given < low || given > high)
        {
            int used = Math.Clamp(given, low, high);

            diagnostics?.Add(Diagnostic.Warning(
                "DAL0012",
                $"'{name}' is {given}, outside {low}-{high}; {used} will be used instead.",
                value.Span));

            return used;
        }

        return given;
    }
}
