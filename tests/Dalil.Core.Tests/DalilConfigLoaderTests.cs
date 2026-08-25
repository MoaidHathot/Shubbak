using Dalil.Core;
using Shubbak.Core.Rendering;

namespace Dalil.Core.Tests;

/// <summary>
/// Reading the <c>dalil</c> section of the shared configuration.
/// </summary>
/// <remarks>
/// Every setting is optional. A palette that does nothing until it has been
/// configured is a palette nobody tries, so the interesting cases here are the ones
/// where the configuration is absent, wrong, or hostile.
/// </remarks>
public sealed class DalilConfigLoaderTests
{
    [Fact]
    public void AnEmptyConfigurationIsAWorkingPalette()
    {
        DalilConfig config = DalilConfigLoader.Load("");

        Assert.Equal("palette", config.OpenOnSignal);
        Assert.True(config.Width > 0);
        Assert.True(config.VisibleRows > 0);
    }

    [Fact]
    public void AConfigurationWithNoDalilSectionIsAlsoFine()
    {
        DalilConfig config = DalilConfigLoader.Load("""
            general {
                hide-method "cloak"
            }
            """);

        Assert.Equal(new DalilConfig().Width, config.Width);
    }

    [Fact]
    public void SettingsAreRead()
    {
        DalilConfig config = DalilConfigLoader.Load("""
            dalil {
                open-on-signal "finder"
                width 900
                row-height 40
                visible-rows 8
                close-on-blur #false
                show-unmanaged #false
                placement "cursor-monitor"
                font "Cascadia Code"
                font-size 14
            }
            """);

        Assert.Equal("finder", config.OpenOnSignal);
        Assert.Equal(900, config.Width);
        Assert.Equal(40, config.RowHeight);
        Assert.Equal(8, config.VisibleRows);
        Assert.False(config.CloseOnBlur);
        Assert.False(config.ShowUnmanaged);
        Assert.Equal(PalettePlacement.CursorMonitor, config.Placement);
        Assert.Equal("Cascadia Code", config.FontFamily);
        Assert.Equal(14, config.FontSize);
    }

    [Fact]
    public void SettingsAreAlsoAcceptedAsProperties()
    {
        // Both spellings work everywhere else in this configuration, so both must
        // work here or the section becomes a special case to remember.
        DalilConfig config = DalilConfigLoader.Load("""dalil width=880 visible-rows=6""");

        Assert.Equal(880, config.Width);
        Assert.Equal(6, config.VisibleRows);
    }

    [Fact]
    public void ColoursAreRead()
    {
        DalilConfig config = DalilConfigLoader.Load("""
            dalil {
                background "#101014"
                match "#7ABEFF"
            }
            """);

        Assert.Equal(new Colour(0x10, 0x10, 0x14), config.Background);
        Assert.Equal(new Colour(0x7A, 0xBE, 0xFF), config.Match);
    }

    [Fact]
    public void NonsensicalSizesAreClampedRatherThanObeyed()
    {
        DalilConfig config = DalilConfigLoader.Load("""
            dalil {
                width 3
                visible-rows 9000
                row-height 0
            }
            """);

        // A palette one pixel wide is indistinguishable on screen from the process
        // having failed to start, which is a miserable thing to debug.
        Assert.True(config.Width >= 240);
        Assert.True(config.VisibleRows <= 40);
        Assert.True(config.RowHeight >= 16);
    }

    [Fact]
    public void AnUnknownPlacementFallsBackRatherThanFailing()
    {
        DalilConfig config = DalilConfigLoader.Load("""dalil { placement "somewhere-else" }""");

        Assert.Equal(PalettePlacement.FocusedMonitor, config.Placement);
    }

    [Fact]
    public void AFileThatDoesNotParseStillYieldsAWorkingPalette()
    {
        DalilConfig config = DalilConfigLoader.Load("dalil { width ");

        // The window manager owns this file and reports its syntax errors properly,
        // with carets and hints. Failing here as well would replace a good diagnostic
        // with a second, worse one - and would take the palette down over a mistake
        // in a section it may not even be mentioned in.
        Assert.Equal(new DalilConfig().Width, config.Width);
    }

    [Fact]
    public void EveryDocumentedKeyIsUnderstood()
    {
        // The list exists so a misspelt key can be reported rather than silently
        // ignored. If it drifts from what Read actually looks at, that reporting
        // starts lying.
        string body = string.Join('\n', DalilConfigLoader.KnownKeys.Select(Sample));
        DalilConfig config = DalilConfigLoader.Load($"dalil {{\n{body}\n}}");

        Assert.Equal("test", config.OpenOnSignal);
        Assert.Equal(640, config.Width);
    }

    private static string Sample(string key) => key switch
    {
        "open-on-signal" => """open-on-signal "test" """,
        "width" => "width 640",
        "row-height" => "row-height 30",
        "visible-rows" => "visible-rows 10",
        "close-on-blur" => "close-on-blur #true",
        "show-unmanaged" => "show-unmanaged #true",
        "action-guard" => "action-guard #true",
        "placement" => """placement "primary" """,
        "font" => """font "Segoe UI" """,
        "font-size" => "font-size 15",
        _ => $"{key} \"#202028\"",
    };
}
