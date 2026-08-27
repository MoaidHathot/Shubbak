using Shubbak.Core.Commands;

namespace Shubbak.Config.Tests;

/// <summary>
/// Suspending, and how it differs from pausing.
/// </summary>
/// <remarks>
/// <para>
/// The two are one word apart and do very different things, which is the whole risk
/// in having both. Pausing stops Shubbak rearranging the desktop and <em>keeps</em>
/// the low-level keyboard hook, so every bound chord is still swallowed. Suspending
/// releases the hook.
/// </para>
/// <para>
/// The difference is the reason suspend exists. Somebody about to play a game does not
/// care whether windows are being arranged - nothing is opening or closing - they care
/// that a chord Shubbak swallows is a chord the game never receives. Until this
/// existed the only way to get that was to exit the window manager, which un-conceals
/// every window on every workspace on the way out and costs seconds to undo.
/// </para>
/// </remarks>
public sealed class SuspendCommandTests
{
    private static WmCommand Parse(string text)
    {
        Assert.True(
            CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error),
            error?.Message);

        return command!;
    }

    [Theory]
    [InlineData("wm-suspend", typeof(SuspendCommand))]
    [InlineData("wm-resume", typeof(ResumeCommand))]
    [InlineData("wm-toggle-suspend", typeof(ToggleSuspendCommand))]
    public void TheVerbsParse(string text, Type expected)
    {
        Assert.IsType(expected, Parse(text));
    }

    /// <summary>Pausing is untouched, and must stay that way.</summary>
    /// <remarks>
    /// Anyone already binding <c>wm-toggle-pause</c> expects what it has always done.
    /// Folding the two together would have been the tidier-looking change and would
    /// have silently altered the meaning of an existing key.
    /// </remarks>
    [Fact]
    public void PauseStillMeansWhatItMeant()
    {
        Assert.IsType<TogglePauseCommand>(Parse("wm-toggle-pause"));
    }

    /// <summary>
    /// Holding the key must not suspend and resume at the hardware repeat rate.
    /// </summary>
    [Theory]
    [InlineData("wm-suspend")]
    [InlineData("wm-resume")]
    [InlineData("wm-toggle-suspend")]
    public void HoldingTheKeyDoesNotRepeatIt(string text)
    {
        Assert.False(Parse(text).RepeatsOnHold);
    }

    /// <summary>
    /// Every verb is in the catalogue, so it is discoverable and spell-checked.
    /// </summary>
    /// <remarks>
    /// The catalogue is what <c>shubbak query commands</c> serves and what the palette
    /// lists. A command reachable only by knowing it exists is a command nobody finds -
    /// which matters here, because the whole point is that people currently solve this
    /// by killing the process.
    /// </remarks>
    [Theory]
    [InlineData("wm-suspend")]
    [InlineData("wm-resume")]
    [InlineData("wm-toggle-suspend")]
    public void TheVerbsAreCatalogued(string verb)
    {
        Assert.Contains(verb, CommandCatalogue.Verbs);
    }

    /// <summary>
    /// The summaries have to tell the two apart, because the names do not.
    /// </summary>
    /// <remarks>
    /// Somebody scanning the command list for a way to stop Shubbak eating their keys
    /// has two candidates one word apart. If both summaries say "suspend tiling" the
    /// list has not helped them.
    /// </remarks>
    [Fact]
    public void TheSummariesDistinguishSuspendingFromPausing()
    {
        string suspend = SummaryOf("wm-toggle-suspend");
        string pause = SummaryOf("wm-toggle-pause");

        Assert.Contains("keyboard", suspend, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keyboard", pause, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(suspend, pause);
    }

    private static string SummaryOf(string verb) =>
        CommandCatalogue.Find(verb)?.Summary
            ?? throw new InvalidOperationException($"'{verb}' is not in the catalogue");
}
