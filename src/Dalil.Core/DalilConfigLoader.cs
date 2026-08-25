using Shubbak.Config;
using Shubbak.Config.Kdl;
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
        "show-unmanaged", "action-guard", "placement", "background", "foreground", "match",
        "secondary", "selection-background", "border", "font", "font-size",
    ];

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
            ActionGuard = Boolean(node, "action-guard") ?? defaults.ActionGuard,
            Placement = ParsePlacement(Text(node, "placement")) ?? defaults.Placement,

            Background = Colour(node, "background") ?? defaults.Background,
            Foreground = Colour(node, "foreground") ?? defaults.Foreground,
            Match = Colour(node, "match") ?? defaults.Match,
            Secondary = Colour(node, "secondary") ?? defaults.Secondary,
            SelectionBackground = Colour(node, "selection-background") ?? defaults.SelectionBackground,
            Border = Colour(node, "border") ?? defaults.Border,

            FontFamily = Text(node, "font") ?? defaults.FontFamily,
        };
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
