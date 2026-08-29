using Dalil.Core;
using Shubbak.Config;

namespace Dalil.Core.Tests;

/// <summary>
/// What a broken configuration does to a palette that is already running.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for was worse than silence. A stray brace mid-edit made the
/// file unparseable, the loader answered with defaults, and the reload applied them -
/// so the palette's colours, its size, its prefixes and every action in it were quietly
/// replaced by stock ones. A visible change, with nothing anywhere to connect it to the
/// keystroke that caused it, in the project whose headline promise is that the config
/// file does not fail silently.
/// </para>
/// <para>
/// Defaults are still the right answer at startup, where there is nothing to keep.
/// </para>
/// </remarks>
public sealed class DalilConfigSafetyTests
{
    private const string Good = """
        dalil {
            width 900
            background "#101014"
            action "Tidy" { equalise }
        }
        """;

    // ---- a file that will not parse ---------------------------------------------------

    [Fact]
    public void AFileThatWillNotParseIsNotUsable()
    {
        DalilConfigLoad load = DalilConfigLoader.Validate("dalil { width ");

        Assert.False(load.Usable);
    }

    [Fact]
    public void ItStillYieldsDefaultsForAFirstLoad()
    {
        // Startup has nothing to keep, so a broken file degrades to a working palette
        // rather than to no palette.
        DalilConfigLoad load = DalilConfigLoader.Validate("dalil { width ");

        Assert.Equal(new DalilConfig().Width, load.Config.Width);
    }

    [Fact]
    public void ItReportsNothingItself()
    {
        // The window manager reads the same text and reports syntax errors with carets,
        // and check-config runs both - so repeating them here would print every one
        // twice. Which is exactly why Usable has to be carried separately: there are no
        // diagnostics to infer it from.
        DalilConfigLoad load = DalilConfigLoader.Validate("dalil { width ");

        Assert.Empty(load.Diagnostics);
        Assert.False(load.Usable);
    }

    // ---- a file that parses but says something wrong -------------------------------------

    [Fact]
    public void AnErrorInTheSectionIsAlsoNotUsable()
    {
        // An unnamed action is an error, and applying the rest over a running palette
        // would drop whatever the user had before alongside the mistake.
        DalilConfigLoad load = DalilConfigLoader.Validate("dalil { action { equalise } }");

        Assert.False(load.Usable);
        Assert.Contains(load.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AWarningIsNotEnoughToRefuseIt()
    {
        // A misspelt setting is worth saying and is not worth throwing the rest away
        // for. Everything else in the section still applies.
        DalilConfigLoad load = DalilConfigLoader.Validate("dalil { width 900; nonsense 1 }");

        Assert.True(load.Usable);
        Assert.Equal(900, load.Config.Width);
        Assert.Contains(load.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void AGoodFileIsUsableAndSaysNothing()
    {
        DalilConfigLoad load = DalilConfigLoader.Validate(Good);

        Assert.True(load.Usable);
        Assert.Empty(load.Diagnostics);
        Assert.Equal(900, load.Config.Width);
    }

    [Fact]
    public void AFileWithNoPaletteSectionIsPerfectlyUsable()
    {
        // Most people have no dalil block. Refusing to apply one would mean refusing to
        // apply anything at all.
        DalilConfigLoad load = DalilConfigLoader.Validate("general { }");

        Assert.True(load.Usable);
    }

    // ---- what the user is told ------------------------------------------------------------

    [Fact]
    public void TheCommandListOffersToShowTheProblemsWhenThereAreSome()
    {
        PaletteEntry row = Assert.Single(
            PaletteEntries.ForBuiltins(problems: 3),
            e => e.Command == PaletteEntries.BuiltinConfig);

        Assert.Contains("3 problems", row.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void OneProblemIsNotThreeProblems()
    {
        PaletteEntry row = Assert.Single(
            PaletteEntries.ForBuiltins(problems: 1),
            e => e.Command == PaletteEntries.BuiltinConfig);

        Assert.Contains("1 problem ", row.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanConfigurationOffersNothing()
    {
        // A row promising to list problems and then listing none is a row that teaches
        // you to ignore it.
        Assert.DoesNotContain(
            PaletteEntries.ForBuiltins(problems: 0),
            e => e.Command == PaletteEntries.BuiltinConfig);
    }

    [Fact]
    public void TheProblemsRowOutranksEverythingElseInTheCommandList()
    {
        // Somebody whose settings are being ignored is not looking for anything else.
        IReadOnlyList<PaletteEntry> builtins = PaletteEntries.ForBuiltins(problems: 2);

        PaletteEntry config = builtins.Single(e => e.Command == PaletteEntries.BuiltinConfig);
        PaletteEntry diagnose = builtins.Single(e => e.Command == PaletteEntries.BuiltinDiagnose);

        Assert.True(config.Rank > diagnose.Rank);
    }

    [Fact]
    public void EachDiagnosticBecomesAReadableRow()
    {
        IReadOnlyList<Diagnostic> diagnostics =
            DalilConfigLoader.Validate("dalil { nonsense 1 }").Diagnostics;

        PaletteEntry row = Assert.Single(PaletteEntries.ForDiagnostics(diagnostics, "shubbak.kdl"));

        // The code and the severity are badges, so "error" narrows the list to the
        // things that actually stopped a setting applying.
        Assert.Contains("DAL0001", row.Badges);
        Assert.Contains("warning", row.Badges);

        // And the whole thing, with its position, is what Ctrl+C yields - something an
        // editor can be told to jump to.
        Assert.Contains("shubbak.kdl", row.Expands!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorRowIsDrawnAsOne()
    {
        IReadOnlyList<Diagnostic> diagnostics =
            DalilConfigLoader.Validate("dalil { action { equalise } }").Diagnostics;

        Assert.Contains(PaletteEntries.ForDiagnostics(diagnostics), r => r.Destructive);
    }

    [Fact]
    public void TheHintIsOnTheRowRatherThanALevelDown()
    {
        // A diagnostic without its hint is half of what the loader knows, and the half
        // that says what to do about it.
        IReadOnlyList<Diagnostic> diagnostics =
            DalilConfigLoader.Validate("""dalil { placement "elsewhere" }""").Diagnostics;

        PaletteEntry row = Assert.Single(PaletteEntries.ForDiagnostics(diagnostics));

        Assert.Contains("cursor-monitor", row.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyListStillSaysSo()
    {
        // Push refuses an empty frame, so returning nothing would answer "show me the
        // problems" by appearing to do nothing at all.
        PaletteEntry row = Assert.Single(PaletteEntries.ForDiagnostics([], "shubbak.kdl"));

        Assert.Contains("Nothing wrong", row.Primary, StringComparison.Ordinal);
    }

    // ---- counting -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(1, 0, "1 error")]
    [InlineData(2, 0, "2 errors")]
    [InlineData(0, 1, "1 warning")]
    [InlineData(0, 3, "3 warnings")]
    [InlineData(2, 5, "2 errors, 5 warnings")]
    [InlineData(1, 1, "1 error, 1 warning")]
    public void ProblemsAreCountedInWords(int errors, int warnings, string expected)
    {
        Assert.Equal(expected, new DiagnosticCounts(errors, warnings).Describe());
    }

    [Fact]
    public void NothingWrongIsNotSomethingToReport()
    {
        Assert.False(new DiagnosticCounts(0, 0).Any);
        Assert.True(new DiagnosticCounts(0, 1).Any);
    }
}
