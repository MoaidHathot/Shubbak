using Shubbak.Config;
using Shubbak.Core.Rendering;
using Taj.Core;
using Taj.Core.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// Tests for the bar's appearance controls.
/// </summary>
public sealed class BarAppearanceTests
{
    private static TajConfig LoadOk(string source)
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load(source);

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return config;
    }

    private static IWidget FirstWidget(TajConfig config) =>
        config.Profiles["default"].Zones[0].Widgets[0];

    // ---- typography --------------------------------------------------------

    [Fact]
    public void AWidgetCanSetItsOwnSize()
    {
        // The model and the renderer always supported this; only a config key was
        // missing, so a user could not reproduce what Taj's own default profile does.
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    font-size 14
                    zone "right" { text id="clock" template="{{ clock }}" font-size=20 }
                }
            }
            """);

        Assert.Equal(20, ((TemplateWidget)FirstWidget(config)).Style.Font.Size);
    }

    [Fact]
    public void AWidgetInheritsTheProfileSizeWhenItSetsNone()
    {
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    font-size 14
                    zone "right" { text id="clock" template="{{ clock }}" }
                }
            }
            """);

        Assert.Equal(14, ((TemplateWidget)FirstWidget(config)).Style.Font.Size);
    }

    [Fact]
    public void AWidgetCanBeBoldOrItalic()
    {
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "right" { text id="layout" template="{{ layout }}" bold=#true italic=#true }
                }
            }
            """);

        FontStyle font = ((TemplateWidget)FirstWidget(config)).Style.Font;

        Assert.True(font.Bold);
        Assert.True(font.Italic);
    }

    // ---- workspace states --------------------------------------------------

    [Fact]
    public void FocusedStylingIsOptional()
    {
        // Right on a single monitor, where the focused workspace and the displayed
        // one are never different.
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "left" { workspaces active-colour="#1e1e2e" }
                }
            }
            """);

        Assert.Null(((WorkspacesWidget)FirstWidget(config)).FocusedStyle);
    }

    [Fact]
    public void AFocusedColourIsRead()
    {
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "left" { workspaces active-colour="#8dbcff" focused-colour="#1dfb8d" }
                }
            }
            """);

        VisualStyle? focused = ((WorkspacesWidget)FirstWidget(config)).FocusedStyle;

        Assert.NotNull(focused);
        Assert.Equal(new Colour(0x1D, 0xFB, 0x8D), focused.Value.Foreground);
    }

    [Fact]
    public void TheActiveBackgroundCanBeSuppressed()
    {
        // Fully transparent means "do not fill", which is how a foreground-only
        // indicator is expressed.
        TajConfig config = LoadOk("""
            bar {
                profile "default" {
                    zone "left" { workspaces active-background="#00000000" active-colour="#8dbcff" }
                }
            }
            """);

        Assert.True(((WorkspacesWidget)FirstWidget(config)).ActiveStyle.Background.IsTransparent);
    }

    [Fact]
    public void TheFocusedWorkspaceIsStyledSeparatelyFromTheDisplayedOnes()
    {
        var widget = new WorkspacesWidget("workspaces")
        {
            ActiveStyle = VisualStyle.Default with { Foreground = new Colour(0x8D, 0xBC, 0xFF) },
            FocusedStyle = VisualStyle.Default with { Foreground = new Colour(0x1D, 0xFB, 0x8D) },
            OccupiedStyle = VisualStyle.Default with { Foreground = new Colour(0xFF, 0xFF, 0xFF) },
        };

        string encoded = WorkspacesWidget.Encode(
        [
            new WorkspacesWidget.WorkspaceEntry("1", "One", Active: true, HasWindows: true, Focused: true),
            new WorkspacesWidget.WorkspaceEntry("2", "Two", Active: true, HasWindows: true),
            new WorkspacesWidget.WorkspaceEntry("3", "Three", Active: false, HasWindows: true),
        ]);

        VisualNode built = widget.Build(new Dictionary<string, string?> { ["workspaces"] = encoded });

        Assert.Equal(new Colour(0x1D, 0xFB, 0x8D), built.Children[0].Style.Foreground);
        Assert.Equal(new Colour(0x8D, 0xBC, 0xFF), built.Children[1].Style.Foreground);
        Assert.Equal(new Colour(0xFF, 0xFF, 0xFF), built.Children[2].Style.Foreground);
    }

    [Fact]
    public void FocusSurvivesTheEncoding()
    {
        string encoded = WorkspacesWidget.Encode(
            [new WorkspacesWidget.WorkspaceEntry("1", "One", true, true, Focused: true)]);

        WorkspacesWidget.WorkspaceEntry decoded = Assert.Single(WorkspacesWidget.Decode(encoded));

        Assert.True(decoded.Focused);
    }

    [Fact]
    public void AnEncodingWithoutFocusStillDecodes()
    {
        // Forward compatibility runs both ways: a host that predates the field must
        // not make the bar throw.
        WorkspacesWidget.WorkspaceEntry decoded =
            Assert.Single(WorkspacesWidget.Decode("1|One|1|1"));

        Assert.False(decoded.Focused);
        Assert.True(decoded.Active);
    }

    // ---- layout icons ------------------------------------------------------

    [Theory]
    [InlineData("splith", "\u2502\u2502")]
    [InlineData("splitv", "\u2261")]
    [InlineData("grid", "\u253C")]
    [InlineData("monocle", "\u25A0")]
    public void LayoutNamesBecomeGlyphs(string layout, string expected)
    {
        Assert.Equal(
            expected,
            Template.Render("{{ layout | icon }}", new Dictionary<string, string?> { ["layout"] = layout }));
    }

    [Theory]
    [InlineData("splith")]
    [InlineData("splitv")]
    [InlineData("fibonacci")]
    [InlineData("fibonacci-v")]
    [InlineData("fibonacci-mirrored")]
    [InlineData("master-left")]
    [InlineData("master-right")]
    [InlineData("master-top")]
    [InlineData("master-bottom")]
    [InlineData("grid")]
    [InlineData("monocle")]
    public void EveryLayoutIconIsAGlyphSegoeUiVariableActuallyHas(string layout)
    {
        // The icons are drawn in whatever font the bar is set to, and the shapes that
        // read most obviously are missing from both Segoe UI Variable Text and Segoe
        // UI. A missing glyph is measured at the width of the substitute box and drawn
        // at the width of a borrowed glyph, so it came out clipped rather than absent -
        // which reads as a rendering fault rather than a missing character.
        //
        // Ranges rather than a font query, so this holds on a machine without the font
        // and states the actual constraint: box-drawing and block elements are covered,
        // the geometric-shapes block largely is not.
        string icon = Template.Render(
            "{{ layout | icon }}",
            new Dictionary<string, string?> { ["layout"] = layout });

        Assert.NotEqual(layout, icon);

        foreach (char c in icon)
        {
            bool covered =
                (c >= '\u2500' && c <= '\u259F') ||   // box drawing and block elements
                c == '\u25A0' || c == '\u25A1' ||     // the two squares that are present
                c == '\u2261';                        // identical to

            Assert.True(
                covered,
                $"'{layout}' uses U+{(int)c:X4}, which is outside the ranges the bar " +
                "font covers; it will be borrowed from another font, mismeasured, " +
                "and clipped");
        }
    }

    [Theory]
    [InlineData("fibonacci")]
    [InlineData("master-left")]
    [InlineData("grid")]
    public void LayoutsThatLookDifferentGetDifferentGlyphs(string layout)
    {
        // master-left and fibonacci both used U+25E7, so two arrangements that look
        // nothing alike showed the same symbol.
        string[] all =
        [
            "splith", "splitv", "fibonacci", "fibonacci-v", "fibonacci-mirrored",
            "master-left", "master-right", "master-top", "master-bottom",
            "grid", "monocle",
        ];

        string icon = Render(layout);

        Assert.Single(all, other => Render(other) == icon);

        static string Render(string name) => Template.Render(
            "{{ layout | icon }}",
            new Dictionary<string, string?> { ["layout"] = name });
    }

    [Fact]
    public void AnUnknownLayoutKeepsItsName()
    {
        // A custom layout should read as itself rather than vanishing.
        Assert.Equal(
            "my-layout",
            Template.Render(
                "{{ layout | icon }}",
                new Dictionary<string, string?> { ["layout"] = "my-layout" }));
    }
}
