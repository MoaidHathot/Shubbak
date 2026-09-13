using Shubbak.Config;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Taj.Core;

namespace Taj.Core.Tests;

/// <summary>
/// The profile settings that shape the bar's surface: translucency, the compositor's
/// backdrop, floating margins, corners and the hairline border.
/// </summary>
/// <remarks>
/// None of these can be seen from here - the renderer and the compositor are on the
/// other side of the seam - so what is tested is that the config reaches the profile,
/// that <c>extends</c> carries it, and that the profile turns it into the style the
/// painter is given for the bar's root node.
/// </remarks>
public sealed class BarSurfaceTests
{
    private static (TajConfig Config, IReadOnlyList<Diagnostic> Diagnostics) Load(string source) =>
        TajConfigLoader.Load(source);

    private static BarProfile Profile(string source, string name = "default")
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = Load(source);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return config.Profiles[name];
    }

    // ---- reading the keys --------------------------------------------------

    [Fact]
    public void ABackgroundKeepsItsAlpha()
    {
        // The whole point. The renderer used to flatten this against itself, so the
        // alpha was read and then thrown away before it reached the screen.
        BarProfile profile = Profile("""
            bar {
                profile "default" { background "#181825b3" }
            }
            """);

        Assert.Equal(new Colour(0x18, 0x18, 0x25, 0xB3), profile.Background);
        Assert.Equal(profile.Background, profile.SurfaceStyle.Background);
    }

    [Theory]
    [InlineData("acrylic", BarBackdrop.Acrylic)]
    [InlineData("mica", BarBackdrop.Mica)]
    [InlineData("tabbed", BarBackdrop.Tabbed)]
    [InlineData("mica-alt", BarBackdrop.Tabbed)]
    [InlineData("none", BarBackdrop.None)]
    [InlineData("Acrylic", BarBackdrop.Acrylic)]
    public void TheBackdropIsNamed(string written, BarBackdrop expected)
    {
        Assert.Equal(expected, Profile($$"""
            bar {
                profile "default" { backdrop "{{written}}" }
            }
            """).Backdrop);
    }

    [Fact]
    public void ThereIsNoBackdropUnlessAsked()
    {
        // Nothing behind a translucent bar means the desktop as it is, which is a look
        // in its own right and the one that works on Windows 10.
        Assert.Equal(BarBackdrop.None, Profile("""
            bar {
                profile "default" { height 30 }
            }
            """).Backdrop);
    }

    [Fact]
    public void AnUnknownBackdropIsReportedAndIgnored()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                profile "default" { backdrop "acrilyc" }
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0024");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("acrilyc", warning.Message, StringComparison.Ordinal);
        Assert.Contains("acrylic", warning.Hint!, StringComparison.Ordinal);
        Assert.Equal(BarBackdrop.None, config.Profiles["default"].Backdrop);
    }

    [Fact]
    public void MarginRadiusAndBorderAreRead()
    {
        BarProfile profile = Profile("""
            bar {
                profile "default" {
                    margin 8
                    radius 10
                    border "#ffffff1a"
                }
            }
            """);

        Assert.Equal(8, profile.Margin);
        Assert.Equal(10, profile.Radius);
        Assert.Equal(new Colour(0xFF, 0xFF, 0xFF, 0x1A), profile.Border);
        Assert.True(profile.IsFloating);
    }

    [Fact]
    public void TheKeysCanBeWrittenAsProperties()
    {
        // The rest of the profile accepts either form, and so must these.
        BarProfile profile = Profile("""
            bar {
                profile "default" margin=6 radius=8 backdrop="mica" { height 30 }
            }
            """);

        Assert.Equal(6, profile.Margin);
        Assert.Equal(8, profile.Radius);
        Assert.Equal(BarBackdrop.Mica, profile.Backdrop);
    }

    [Theory]
    [InlineData("margin -4")]
    [InlineData("radius \"lots\"")]
    public void ANonsensicalMeasureIsReportedAndLeftAtZero(string setting)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = Load($$"""
            bar {
                profile "default" { {{setting}} }
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0025");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal(0, config.Profiles["default"].Margin);
        Assert.Equal(0, config.Profiles["default"].Radius);
    }

    [Fact]
    public void TheNewKeysAreNotMistakenForTypos()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = Load("""
            bar {
                profile "default" {
                    background "#1e1e2ecc"
                    backdrop "acrylic"
                    margin 8
                    radius 8
                    border "#ffffff14"
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0014");
    }

    // ---- inheritance -------------------------------------------------------

    [Fact]
    public void AVariantInheritsTheSurfaceItDoesNotMention()
    {
        // `extends` is for changing one thing. A presentation profile that only
        // empties a zone must not also lose its acrylic and its margins.
        BarProfile variant = Profile("""
            bar {
                profile "default" {
                    background "#181825b3"
                    backdrop "acrylic"
                    margin 8
                    radius 10
                    border "#ffffff1a"
                }
                profile "presentation" extends="default" {
                    height 24
                }
            }
            """, "presentation");

        Assert.Equal(24, variant.Height);
        Assert.Equal(new Colour(0x18, 0x18, 0x25, 0xB3), variant.Background);
        Assert.Equal(BarBackdrop.Acrylic, variant.Backdrop);
        Assert.Equal(8, variant.Margin);
        Assert.Equal(10, variant.Radius);
        Assert.Equal(new Colour(0xFF, 0xFF, 0xFF, 0x1A), variant.Border);
    }

    [Fact]
    public void AVariantCanDockWhatItsParentFloats()
    {
        BarProfile variant = Profile("""
            bar {
                profile "default" {
                    margin 8
                    radius 10
                }
                profile "presentation" extends="default" {
                    margin 0
                    radius 0
                }
            }
            """, "presentation");

        Assert.Equal(0, variant.Margin);
        Assert.Equal(0, variant.Radius);
        Assert.False(variant.IsFloating);
    }

    // ---- what the painter is given -----------------------------------------

    [Fact]
    public void AFloatingBarIsOutlinedAllRound()
    {
        VisualStyle surface = Profile("""
            bar {
                profile "default" {
                    margin 8
                    radius 10
                    border "#ffffff1a"
                }
            }
            """).SurfaceStyle;

        Assert.Equal(10, surface.CornerRadius);
        Assert.Equal(1, surface.BorderWidth);
        Assert.Equal(new Colour(0xFF, 0xFF, 0xFF, 0x1A), surface.BorderColour);
        Assert.Equal(BorderSides.All, surface.BorderSides);
    }

    [Fact]
    public void ADockedBarBordersOnlyTheEdgeThatFacesTheWindows()
    {
        // A hairline along the top of the screen, and down the seam between two
        // monitors, would mark nothing. The edge the windows meet is the one that
        // wants separating.
        VisualStyle top = Profile("""
            bar {
                profile "default" { border "#ffffff1a" }
            }
            """).SurfaceStyle;

        VisualStyle bottom = Profile("""
            bar {
                profile "default" {
                    edge "bottom"
                    border "#ffffff1a"
                }
            }
            """).SurfaceStyle;

        Assert.Equal(1, top.BorderWidth);
        Assert.Equal(BorderSides.Bottom, top.BorderSides);

        Assert.Equal(1, bottom.BorderWidth);
        Assert.Equal(BorderSides.Top, bottom.BorderSides);
    }

    [Fact]
    public void NoBorderIsNoBorder()
    {
        VisualStyle surface = Profile("""
            bar {
                profile "default" { height 30 }
            }
            """).SurfaceStyle;

        Assert.Equal(0, surface.BorderWidth);
        Assert.True(surface.BorderColour.IsTransparent);
    }

    [Fact]
    public void TheRootNodeCarriesTheSurface()
    {
        // The host clears the frame to nothing and lets the root paint the bar, so
        // the root has to be the one carrying the background, corners and border.
        BarProfile profile = Profile("""
            bar {
                profile "default" {
                    background "#181825b3"
                    margin 8
                    radius 10
                    border "#ffffff1a"
                }
            }
            """);

        using var model = new BarModel(profile);
        VisualNode root = model.Build();

        Assert.Equal("bar", root.Id);
        Assert.Equal(profile.SurfaceStyle, root.Style);
        Assert.Equal(new Colour(0x18, 0x18, 0x25, 0xB3), root.Style.Background);
        Assert.Equal(10, root.Style.CornerRadius);
        Assert.Equal(1, root.Style.BorderWidth);
    }

    [Fact]
    public void TheStockBarIsUnchanged()
    {
        // Nobody asked for any of this: an opaque, docked, square, unbordered bar, so
        // the default profile looks exactly as it did before the surface was styleable.
        BarProfile stock = TajConfigLoader.CreateDefault().Default;

        Assert.Equal(255, stock.Background.A);
        Assert.Equal(BarBackdrop.None, stock.Backdrop);
        Assert.Equal(0, stock.Margin);
        Assert.Equal(0, stock.Radius);
        Assert.True(stock.Border.IsTransparent);
        Assert.Equal(0, stock.SurfaceStyle.BorderWidth);
        Assert.Equal(0, stock.SurfaceStyle.CornerRadius);
    }
}
