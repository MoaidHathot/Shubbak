using Dalil.Core;

namespace Dalil.Tests;

/// <summary>
/// Routing a mode to the rows it shows.
/// </summary>
/// <remarks>
/// A lookup rather than a judgement, which is exactly why it is worth a test: a mode
/// added to the enum and forgotten here does not fail to build. It falls through to
/// the window list and looks, from the outside, like a prefix that quietly does
/// nothing.
/// </remarks>
public class PaletteSourcesTests
{
    private static PaletteEntry Row(string name) => new(name, string.Empty, [], string.Empty);

    private static PaletteSources Sources() => new(
        Windows: [Row("windows")],
        Commands: [Row("commands")],
        Workspaces: [Row("workspaces")],
        Layouts: [Row("layouts")],
        Monitors: [Row("monitors")],
        Scratchpad: [Row("scratchpad")],
        Help: [Row("help")],
        Completions: CompletionSources.None,
        Status: WmStatus.Unknown,
        Skipped: [Row("skipped")]);

    [Theory]
    [InlineData(PaletteMode.Windows, "windows")]
    [InlineData(PaletteMode.Commands, "commands")]
    [InlineData(PaletteMode.Workspaces, "workspaces")]
    [InlineData(PaletteMode.Layouts, "layouts")]
    [InlineData(PaletteMode.Monitors, "monitors")]
    [InlineData(PaletteMode.Scratchpad, "scratchpad")]
    [InlineData(PaletteMode.Help, "help")]
    [InlineData(PaletteMode.Inspect, "skipped")]
    public void EveryModeReachesItsOwnRows(PaletteMode mode, string expected) =>
        Assert.Equal(expected, Assert.Single(Sources().For(mode)).Primary);

    [Fact]
    public void NoModeQuietlyFallsThroughToTheWindowList()
    {
        // The failure this guards is silent by construction: the switch has a default,
        // so a new mode compiles and shows the window list under a prefix that
        // advertises something else.
        PaletteSources sources = Sources();

        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
        {
            if (mode is PaletteMode.Windows) continue;

            Assert.NotEqual("windows", Assert.Single(sources.For(mode)).Primary);
        }
    }

    [Fact]
    public void TheEmptySourcesAnswerEveryModeWithoutThrowing()
    {
        // What the palette shows before the first query lands, and when the window
        // manager is not running at all - which is exactly when somebody opens it.
        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
            Assert.NotNull(PaletteSources.Empty.For(mode));
    }

    [Fact]
    public void HelpStillWorksWithNothingFetched()
    {
        // Built rather than fetched, so the one mode that explains the palette works
        // when nothing else does.
        Assert.NotEmpty(PaletteSources.Empty.For(PaletteMode.Help));
    }

    [Fact]
    public void AnUnfilledInspectModeIsEmptyRatherThanNull()
    {
        // Skipped is optional on the record, so an older caller leaves it null. The
        // palette must get a list back either way.
        PaletteSources partial = Sources() with { Skipped = null };

        Assert.Empty(partial.For(PaletteMode.Inspect));
    }
}
