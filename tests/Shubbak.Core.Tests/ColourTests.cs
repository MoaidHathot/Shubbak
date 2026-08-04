using Shubbak.Core.Rendering;

namespace Shubbak.Core.Tests;

/// <summary>
/// Parsing the colours people write in config.
/// </summary>
/// <remarks>
/// Shared by the bar and the window manager. It began in the bar and was exercised
/// only through bar config loading; the window manager then grew a second, shorter
/// copy for its focus borders, which accepted eight hex digits and silently dropped
/// the alpha. Two parsers, one of them wrong, and nothing testing either directly.
/// </remarks>
public sealed class ColourTests
{
    [Theory]
    [InlineData("#1dfb8d", 0x1D, 0xFB, 0x8D)]
    [InlineData("1dfb8d", 0x1D, 0xFB, 0x8D)]
    [InlineData("#1DFB8D", 0x1D, 0xFB, 0x8D)]
    [InlineData("  #1dfb8d  ", 0x1D, 0xFB, 0x8D)]
    public void SixDigitsAreRedGreenBlue(string text, byte r, byte g, byte b)
    {
        Assert.True(Colour.TryParse(text, out Colour colour));

        Assert.Equal(new Colour(r, g, b), colour);
        Assert.Equal(255, colour.A);
    }

    [Fact]
    public void ThreeDigitsAreDoubled()
    {
        // #abc means #aabbcc, as in CSS - not #a0b0c0.
        Assert.True(Colour.TryParse("#abc", out Colour colour));

        Assert.Equal(new Colour(0xAA, 0xBB, 0xCC), colour);
    }

    [Fact]
    public void EightDigitsCarryAlphaLast()
    {
        // The case the window manager's copy got wrong: it accepted the length and
        // then read only the first six digits, so a colour written with alpha was
        // quietly opaque and nothing said so.
        Assert.True(Colour.TryParse("#1dfb8d80", out Colour colour));

        Assert.Equal(new Colour(0x1D, 0xFB, 0x8D, 0x80), colour);
        Assert.Equal(0x80, colour.A);
    }

    [Fact]
    public void AlphaIsLastRatherThanFirst()
    {
        // CSS order, not Win32's #AARRGGBB. Config is written by people who know CSS,
        // and reinterpreting their colours silently would be a baffling bug.
        Assert.True(Colour.TryParse("#ff000080", out Colour colour));

        Assert.Equal(0xFF, colour.R);
        Assert.Equal(0x80, colour.A);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#123456789")]
    [InlineData("#gggggg")]
    [InlineData("rebeccapurple")]
    public void AnythingElseIsRefusedRatherThanGuessed(string? text)
    {
        // Refused rather than defaulted: a mistyped colour that silently became black
        // would look exactly like a border that had stopped working.
        Assert.False(Colour.TryParse(text, out Colour colour));
        Assert.Equal(default, colour);
    }

    [Fact]
    public void RoundTripsThroughItsOwnText()
    {
        Assert.True(Colour.TryParse("#1dfb8d", out Colour opaque));
        Assert.Equal("#1DFB8D", opaque.ToString());

        Assert.True(Colour.TryParse("#1dfb8d80", out Colour translucent));
        Assert.Equal("#1DFB8D80", translucent.ToString());

        // And back again, so the text form is a colour the parser accepts.
        Assert.True(Colour.TryParse(translucent.ToString(), out Colour again));
        Assert.Equal(translucent, again);
    }

    [Fact]
    public void BlendingStopsAtBothEnds()
    {
        var black = Colour.Black;
        var white = Colour.White;

        Assert.Equal(black, black.Lerp(white, 0));
        Assert.Equal(white, black.Lerp(white, 1));

        // Clamped rather than extrapolated, so a caller that computes a ratio slightly
        // outside the range gets a colour rather than an overflowed byte.
        Assert.Equal(black, black.Lerp(white, -5));
        Assert.Equal(white, black.Lerp(white, 5));
    }

    [Fact]
    public void TransparentIsRecognisedByItsAlphaAlone()
    {
        Assert.True(Colour.Transparent.IsTransparent);
        Assert.False(Colour.Black.IsTransparent);

        // Black and transparent-black differ only in alpha, and the bar relies on
        // telling them apart to decide whether to fill at all.
        Assert.NotEqual(Colour.Black, Colour.Transparent);
        Assert.True(Colour.Black.WithAlpha(0).IsTransparent);
    }
}
