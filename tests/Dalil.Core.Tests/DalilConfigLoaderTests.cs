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
        "confirm-destructive" => "confirm-destructive #true",
        "action-guard" => "action-guard #true",
        "show-icons" => "show-icons #true",
        "shrink-to-fit" => "shrink-to-fit #true",
        "placement" => """placement "primary" """,
        "font" => """font "Segoe UI" """,
        "font-size" => "font-size 15",
        _ => $"{key} \"#202028\"",
    };

    // ---- prefixes ------------------------------------------------------------------

    [Fact]
    public void APrefixCanBeMovedToACharacterTheKeyboardCanActuallyType()
    {
        // The reason this setting exists. On a German layout `~` is AltGr+Plus and
        // behaves as a dead key, so the character does not arrive until the next
        // keypress and the mode never changes - somebody on that keyboard could not
        // reach layouts mode by typing at all, and nothing on screen would say why.
        DalilConfig config = DalilConfigLoader.Load("""
            dalil {
                prefixes {
                    layouts "l"
                }
            }
            """);

        PalettePrefixes prefixes = PalettePrefixes.With(config.Prefixes);

        Assert.Equal('l', prefixes.PrefixFor(PaletteMode.Layouts));
        Assert.Equal(PaletteMode.Layouts, prefixes.ModeOf("lfib"));
    }

    [Fact]
    public void MovingOnePrefixLeavesTheRestWhereTheyWere()
    {
        // Overlaid rather than replacing the table wholesale. Somebody remapping the
        // one prefix their keyboard cannot produce should not thereby lose the six
        // that were working.
        DalilConfig config = DalilConfigLoader.Load("""dalil { prefixes { layouts "l" } }""");

        PalettePrefixes prefixes = PalettePrefixes.With(config.Prefixes);

        Assert.Equal('>', prefixes.PrefixFor(PaletteMode.Commands));
        Assert.Equal('!', prefixes.PrefixFor(PaletteMode.Inspect));
    }

    [Fact]
    public void APrefixCanBeGivenUpAltogether()
    {
        // Freeing the character up for searching. The mode is still reachable by Tab
        // and by its jump key, so this loses nothing but the shortcut.
        DalilConfig config = DalilConfigLoader.Load("""dalil { prefixes { monitors "" } }""");

        PalettePrefixes prefixes = PalettePrefixes.With(config.Prefixes);

        Assert.Equal('\0', prefixes.PrefixFor(PaletteMode.Monitors));
        Assert.Equal(PaletteMode.Windows, prefixes.ModeOf("%foo"));
    }

    [Fact]
    public void AnExplicitPrefixWinsTheCharacterFromWhicheverDefaultHadIt()
    {
        // Otherwise remapping onto a taken character would be silently refused, and
        // the user would be left pressing a key that does nothing with no way to find
        // out that their own configuration had been overruled.
        DalilConfig config = DalilConfigLoader.Load("""dalil { prefixes { layouts ">" } }""");

        PalettePrefixes prefixes = PalettePrefixes.With(config.Prefixes);

        Assert.Equal(PaletteMode.Layouts, prefixes.ModeOf(">fib"));
        Assert.Equal('\0', prefixes.PrefixFor(PaletteMode.Commands));
    }

    // ---- macros --------------------------------------------------------------------

    [Fact]
    public void ANamedSequenceBecomesOneRow()
    {
        // The answer to keybindings being a scarce resource. There are only so many
        // chords a person can hold, so anything done occasionally never gets bound and
        // is then done by hand for ever.
        DalilConfig config = DalilConfigLoader.Load("""
            dalil {
                action "Dev layout" description="Two panes on 2" {
                    focus --workspace "2"
                    layout --set "master-left"
                    equalise
                }
            }
            """);

        PaletteMacro macro = Assert.Single(config.Macros);

        Assert.Equal("Dev layout", macro.Name);
        Assert.Equal("Two panes on 2", macro.Description);
        Assert.Equal(3, macro.Commands.Count);
        Assert.Null(macro.Problem);
    }

    [Fact]
    public void AMacroIsSentAsOneMessageSoItCannotBeInterleaved()
    {
        DalilConfig config = DalilConfigLoader.Load("""
            dalil {
                action "Tidy" {
                    equalise
                    wm-redraw
                }
            }
            """);

        PaletteEntry row = Assert.Single(PaletteEntries.ForMacros(config.Macros));

        // The pipe takes a newline-separated sequence and stops at the first failure,
        // which is what keeps a half-applied layout from being possible.
        Assert.Equal("equalise\nwm-redraw", row.Command);
    }

    [Fact]
    public void AMacroWithAMistakeSaysSoRatherThanVanishing()
    {
        // Validated with the real parser at load time, so the message is the one the
        // config file would have given. Dropping the macro in silence would leave the
        // user looking for a row that is not there and concluding the feature does not
        // work.
        DalilConfig config = DalilConfigLoader.Load("""
            dalil {
                action "Broken" {
                    focus --direction "sideways"
                }
            }
            """);

        PaletteMacro macro = Assert.Single(config.Macros);
        Assert.NotNull(macro.Problem);

        PaletteEntry row = Assert.Single(PaletteEntries.ForMacros(config.Macros));

        Assert.Equal(string.Empty, row.Command);
        Assert.Contains("cannot run", row.Badges);
    }

    [Fact]
    public void NoMacrosIsTheOrdinaryCase()
    {
        Assert.Empty(DalilConfigLoader.Load("dalil { width 640 }").Macros);
    }

    // ---- what action-guard became ---------------------------------------------------

    [Fact]
    public void TheOldGuardSettingStillMeansAskBeforeSomethingIrreversible()
    {
        // Nobody's configuration breaks. What changed is that it no longer disables the
        // eight reversible chords it used to take down with it - which was never
        // something anybody configured on purpose.
        Assert.False(DalilConfigLoader.Load("dalil { action-guard #false }").ConfirmDestructive);
        Assert.True(DalilConfigLoader.Load("dalil { action-guard #true }").ConfirmDestructive);
    }

    [Fact]
    public void TheNewNameWinsWhenBothAreWritten()
    {
        Assert.True(DalilConfigLoader.Load(
            "dalil { confirm-destructive #true; action-guard #false }").ConfirmDestructive);
    }

    [Fact]
    public void AskingIsOnByDefault()
    {
        Assert.True(new DalilConfig().ConfirmDestructive);
    }
}
