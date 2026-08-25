using Shubbak.Core.Rendering;

namespace Dalil;

/// <summary>
/// Colours worked out from the configured ones.
/// </summary>
/// <remarks>
/// <para>
/// A chip, a pill and an accent bar each want a colour, and asking the user to
/// choose nine colours to get a palette that looks right is a good way to have
/// nobody configure any of them. These are derived instead, so setting
/// <c>background</c> and <c>match</c> moves everything else with them.
/// </para>
/// <para>
/// Every derived colour is opaque, computed by blending towards the background
/// rather than by lowering alpha. GDI has no real alpha: <c>GdiRenderer</c> resolves
/// a translucent colour against an assumed backdrop, which is right for a bar drawn
/// over one known colour and wrong for anything layered over something else. Blending
/// here means what is drawn is what was computed.
/// </para>
/// </remarks>
internal readonly record struct PaletteTheme
{
    /// <summary>Behind the mode name in the search box.</summary>
    public required Colour Chip { get; init; }

    /// <summary>The mode name itself.</summary>
    public required Colour ChipText { get; init; }

    /// <summary>The bar down the left of the selected row.</summary>
    public required Colour Accent { get; init; }

    /// <summary>Behind a badge.</summary>
    public required Colour Pill { get; init; }

    /// <summary>Badge text.</summary>
    public required Colour PillText { get; init; }

    /// <summary>Hairlines between the sections.</summary>
    public required Colour Rule { get; init; }

    /// <summary>The prompt glyph, and the caret.</summary>
    public required Colour Prompt { get; init; }

    public static PaletteTheme From(DalilConfigView config) => new()
    {
        // Far enough from the background to read as a shape, nowhere near far enough
        // to compete with the text inside it.
        Chip = config.Background.Lerp(config.Match, 0.22),
        ChipText = config.Match.Lerp(Colour.White, 0.25),

        Accent = config.Match,

        Pill = config.Background.Lerp(config.Border, 0.75),
        PillText = config.Secondary.Lerp(Colour.White, 0.12),

        // Quieter than the configured border, which draws the window's own outline
        // and would be too loud repeated twice across the middle.
        Rule = config.Background.Lerp(config.Border, 0.6),

        Prompt = config.Secondary.Lerp(config.Match, 0.4),
    };
}

/// <summary>The parts of the configuration the theme is derived from.</summary>
/// <remarks>
/// A narrow view rather than the whole config, so the derivation cannot quietly start
/// depending on a layout setting and have to be recomputed when one changes.
/// </remarks>
internal readonly record struct DalilConfigView(
    Colour Background,
    Colour Foreground,
    Colour Match,
    Colour Secondary,
    Colour Border);
