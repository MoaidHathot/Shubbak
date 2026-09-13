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

    // ---- named colours -----------------------------------------------------

    /// <summary>
    /// Runs a test with a known accent, and puts the hook back afterwards. The hook is
    /// process-wide, so a test that left it set would colour every test after it.
    /// </summary>
    private static void WithAccent(Colour? accent, Action test)
    {
        Func<Colour?>? previous = Colour.AccentSource;
        Colour.AccentSource = () => accent;

        try { test(); }
        finally { Colour.AccentSource = previous; }
    }

    [Theory]
    [InlineData("accent")]
    [InlineData("Accent")]
    [InlineData("ACCENT")]
    [InlineData("  accent  ")]
    public void AccentIsTheHostsAccent(string text) => WithAccent(new Colour(0x1D, 0xFB, 0x8D), () =>
    {
        Assert.True(Colour.TryParse(text, out Colour colour));
        Assert.Equal(new Colour(0x1D, 0xFB, 0x8D), colour);
    });

    [Fact]
    public void AccentIsOpaqueWhateverTheHostSays() => WithAccent(new Colour(0x1D, 0xFB, 0x8D, 0xC4), () =>
    {
        // The compositor's colorization value carries its own blend weight in the
        // alpha byte. That is not a property of the colour, and a bar background
        // written `accent` should be as opaque as one written in hex.
        Assert.True(Colour.TryParse("accent", out Colour colour));
        Assert.Equal(255, colour.A);
    });

    [Fact]
    public void AccentFallsBackToTheStockBlueWithoutAHost()
    {
        // A config written `accent` must parse everywhere the parser runs - in a
        // test, in a tool, on a machine whose compositor will not answer - or the
        // same file would be valid in one process and not another.
        WithAccent(null, () =>
        {
            Assert.True(Colour.TryParse("accent", out Colour colour));
            Assert.Equal(Colour.DefaultAccent, colour);
        });

        Func<Colour?>? previous = Colour.AccentSource;
        Colour.AccentSource = null;

        try
        {
            Assert.True(Colour.TryParse("accent", out Colour colour));
            Assert.Equal(Colour.DefaultAccent, colour);
        }
        finally
        {
            Colour.AccentSource = previous;
        }
    }

    [Fact]
    public void AccentIsReadEachTimeRatherThanOnce()
    {
        // The user changes their accent and the config is re-read; the new colour has
        // to be what the re-read sees.
        Colour current = new(0x00, 0x78, 0xD4);
        Func<Colour?>? previous = Colour.AccentSource;
        Colour.AccentSource = () => current;

        try
        {
            Assert.True(Colour.TryParse("accent", out Colour first));
            current = new Colour(0xF3, 0x8B, 0xA8);
            Assert.True(Colour.TryParse("accent", out Colour second));

            Assert.NotEqual(first, second);
            Assert.Equal(new Colour(0xF3, 0x8B, 0xA8), second);
        }
        finally
        {
            Colour.AccentSource = previous;
        }
    }

    [Theory]
    [InlineData("accent 40%", 102)]
    [InlineData("accent 100%", 255)]
    [InlineData("accent 0%", 0)]
    [InlineData("accent\t25%", 64)]
    public void AnOpacityMayFollowANamedColour(string text, int alpha) => WithAccent(new Colour(1, 2, 3), () =>
    {
        // There is no hex to append an alpha to, so a named colour takes its opacity
        // as a word: this is how a translucent accent pill is written.
        Assert.True(Colour.TryParse(text, out Colour colour));

        Assert.Equal(new Colour(1, 2, 3, (byte)alpha), colour);
    });

    [Fact]
    public void AnOpacityMayFollowAHexColourToo()
    {
        // One rule for both spellings, and it scales what is there: half of a colour
        // that is already half transparent is a quarter.
        Assert.True(Colour.TryParse("#8dbcff 50%", out Colour opaque));
        Assert.Equal(new Colour(0x8D, 0xBC, 0xFF, 128), opaque);

        Assert.True(Colour.TryParse("#8dbcff80 50%", out Colour translucent));
        Assert.Equal(64, translucent.A);
    }

    [Theory]
    [InlineData("accent 140%")]
    [InlineData("accent -5%")]
    [InlineData("accent lots%")]
    [InlineData("accent 40")]
    [InlineData("40%")]
    [InlineData("accent 40% 50%")]
    public void ANonsensicalOpacityIsRefused(string text) => WithAccent(new Colour(1, 2, 3), () =>
    {
        Assert.False(Colour.TryParse(text, out _));
    });

    [Fact]
    public void TheNamesAreListedForTheHintsThatMentionThem() =>
        Assert.Contains("accent", Colour.Names);
}
