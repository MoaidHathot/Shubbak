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

    /// <summary>Reads the section from configuration text.</summary>
    public static DalilConfig Load(string source)
    {
        KdlParseResult parsed = KdlParser.Parse(source);

        // A file that does not parse is the window manager's problem to report, with
        // carets and hints. Falling back to defaults here means a broken config
        // degrades to a working palette rather than to no palette, and the user is
        // already being told what is wrong by the thing that owns the file.
        if (parsed.HasErrors) return new DalilConfig();

        return parsed.Document.Node("dalil") is { } node ? Read(node) : new DalilConfig();
    }

    private static DalilConfig Read(KdlNode node)
    {
        var defaults = new DalilConfig();

        return new DalilConfig
        {
            OpenOnSignal = Text(node, "open-on-signal") ?? defaults.OpenOnSignal,

            // Clamped rather than trusted. A palette one pixel wide is not a
            // configuration anybody meant, and it is indistinguishable on screen from
            // the process having failed to start.
            Width = Clamp(Number(node, "width"), 240, 2400, defaults.Width),
            RowHeight = Clamp(Number(node, "row-height"), 16, 120, defaults.RowHeight),
            VisibleRows = Clamp(Number(node, "visible-rows"), 1, 40, defaults.VisibleRows),
            FontSize = Clamp(Number(node, "font-size"), 6, 48, defaults.FontSize),

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

            Placement = ParsePlacement(Text(node, "placement")) ?? defaults.Placement,

            Prefixes = ReadPrefixes(node),
            Macros = ReadMacros(node),

            Background = Colour(node, "background") ?? defaults.Background,
            Foreground = Colour(node, "foreground") ?? defaults.Foreground,
            Match = Colour(node, "match") ?? defaults.Match,
            Secondary = Colour(node, "secondary") ?? defaults.Secondary,
            SelectionBackground = Colour(node, "selection-background") ?? defaults.SelectionBackground,
            Border = Colour(node, "border") ?? defaults.Border,
            Danger = Colour(node, "danger") ?? defaults.Danger,

            FontFamily = Text(node, "font") ?? defaults.FontFamily,
        };
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
    private static Dictionary<PaletteMode, char> ReadPrefixes(KdlNode node)
    {
        if (node.Child("prefixes") is not { } block) return new Dictionary<PaletteMode, char>();

        Dictionary<PaletteMode, char> table = [];

        foreach (KdlNode child in block.Children)
        {
            if (PaletteModel.ModeNamed(child.Name) is not { } mode) continue;

            string spelling = child.Argument(0)?.AsString() ?? string.Empty;

            // Exactly one character, or none. A two-character prefix is not something
            // this can honour - the mode is decided by looking at the first character
            // of the query - so accepting it would mean silently using the first
            // letter of whatever was written.
            table[mode] = spelling.Length == 1 ? spelling[0] : '\0';
        }

        return table;
    }

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
    private static List<PaletteMacro> ReadMacros(KdlNode node)
    {
        List<PaletteMacro> macros = [];

        foreach (KdlNode action in node.ChildrenNamed("action"))
        {
            if (action.Argument(0)?.AsString() is not { Length: > 0 } name) continue;

            List<string> commands = [];
            string? problem = null;

            foreach (KdlNode child in action.Children)
            {
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

                // The first mistake, in the parser's own words. Reporting every one of
                // them in a row that is a single line long would mean reporting none of
                // them legibly.
                problem ??= error!.Hint is { Length: > 0 } hint
                    ? $"{error.Message}  {hint}"
                    : error!.Message;
            }

            if (commands.Count == 0 && problem is null) continue;

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

    private static PalettePlacement? ParsePlacement(string? text) => text?.ToLowerInvariant() switch
    {
        "focused-monitor" or "focused" => PalettePlacement.FocusedMonitor,
        "cursor-monitor" or "cursor" => PalettePlacement.CursorMonitor,
        "primary" => PalettePlacement.Primary,
        _ => null,
    };

    /// <summary>Accepted as a child node or as a property, like every other section.</summary>
    private static KdlValue? Setting(KdlNode node, string name) =>
        node.Child(name)?.Argument(0) ?? node.Property(name);

    private static string? Text(KdlNode node, string name) => Setting(node, name)?.AsString();

    private static int? Number(KdlNode node, string name) =>
        Setting(node, name) is { } value && value.TryAsInt(out int result) ? result : null;

    private static bool? Boolean(KdlNode node, string name) =>
        Setting(node, name) is { } value && value.TryAsBool(out bool result) ? result : null;

    private static Colour? Colour(KdlNode node, string name) =>
        Shubbak.Core.Rendering.Colour.TryParse(Text(node, name), out Colour colour) ? colour : null;

    private static int Clamp(int? value, int low, int high, int fallback) =>
        value is { } given ? Math.Clamp(given, low, high) : fallback;
}
