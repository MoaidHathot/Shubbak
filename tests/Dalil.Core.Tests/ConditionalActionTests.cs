using Dalil.Core;
using Shubbak.Config;

namespace Dalil.Core.Tests;

/// <summary>
/// Actions tied to a context: <c>when-context=</c> and <c>unless-context=</c>.
/// </summary>
/// <remarks>
/// The feature exists so that a pair of rows - "Start focus timer" and "Stop focus
/// timer", "Mute" and "Unmute" - reads as one switch, with the half that applies shown
/// and the other kept back. The program behind the switch is driven by signals and has
/// no row of its own to edit, so the condition is the only way the list can follow it.
/// </remarks>
public sealed class ConditionalActionTests
{
    private const string Declared = """
        contexts {
            context "focusing" { }
            context "meeting-live" { }
            context "meeting-muted" { }
        }
        """;

    private const string Timer = Declared + """

        dalil {
            action "Start focus timer" unless-context="focusing" { signal "focus" "start" "25" }
            action "Stop focus timer" when-context="focusing" { signal "focus" "stop" }
            action "Tidy" { equalise }
        }
        """;

    private static IReadOnlyList<Diagnostic> Check(string source) =>
        DalilConfigLoader.Validate(source).Diagnostics;

    private static PaletteMacro Named(DalilConfig config, string name) =>
        Assert.Single(config.Macros, m => m.Name == name);

    private static WmStatus Holding(params string[] contexts) =>
        new(false, null, Contexts: contexts);

    private static IReadOnlyList<PaletteEntry> Rows(DalilConfig config, WmStatus? status, bool everything = false) =>
        PaletteEntries.ForMacros(config.Macros, CompletionSources.None, labels: null, status, everything);

    // ---- reading the keys ------------------------------------------------------------

    [Fact]
    public void BothKeysAreReadAsPropertiesAndAreNotCommands()
    {
        DalilConfig config = DalilConfigLoader.Load(Timer);

        PaletteMacro start = Named(config, "Start focus timer");
        PaletteMacro stop = Named(config, "Stop focus timer");

        Assert.Null(start.WhenContext);
        Assert.Equal("focusing", start.UnlessContext);
        Assert.Equal("focusing", stop.WhenContext);
        Assert.Null(stop.UnlessContext);

        // The condition did not leak into the sequence that is sent.
        Assert.Equal("signal focus start 25", Assert.Single(start.Commands));
        Assert.Equal("signal focus stop", Assert.Single(stop.Commands));
        Assert.Null(start.Problem);
        Assert.Null(stop.Problem);
    }

    [Fact]
    public void BothKeysAreReadAsChildrenToo()
    {
        // Every setting in this file can be written either way, and a child node that
        // was not recognised would be handed to the command parser as a verb.
        DalilConfig config = DalilConfigLoader.Load(Declared + """

            dalil {
                action "Stop focus timer" {
                    when-context "focusing"
                    signal "focus" "stop"
                }
                action "Start focus timer" {
                    unless-context "focusing"
                    signal "focus" "start"
                }
            }
            """);

        Assert.Equal("focusing", Named(config, "Stop focus timer").WhenContext);
        Assert.Equal("focusing", Named(config, "Start focus timer").UnlessContext);
        Assert.All(config.Macros, m => Assert.Null(m.Problem));
        Assert.All(config.Macros, m => Assert.Single(m.Commands));
    }

    [Fact]
    public void AnActionWithoutEitherKeyIsNotConditional()
    {
        PaletteMacro tidy = Named(DalilConfigLoader.Load(Timer), "Tidy");

        Assert.False(tidy.Conditional);
        Assert.True(tidy.Applies(null));
        Assert.True(tidy.Applies(["focusing"]));
    }

    [Fact]
    public void AGoodPairSaysNothing()
    {
        Assert.Empty(Check(Timer));
    }

    // ---- what the loader says --------------------------------------------------------

    [Fact]
    public void AContextNothingDeclaresIsAWarningWithAGuess()
    {
        Diagnostic wrong = Assert.Single(Check(Declared + """

            dalil {
                action "Stop focus timer" when-context="focussing" { signal "focus" "stop" }
            }
            """), d => d.Code == "DAL0021");

        Assert.Equal(DiagnosticSeverity.Warning, wrong.Severity);
        Assert.Contains("when-context", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("'focussing'", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("focusing", wrong.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUndeclaredContextIsKeptAsWrittenSoTheRowBehavesAsTheWarningSays()
    {
        // The row is correct and the context never holds: a when-context row is kept
        // back for ever, an unless-context row is always offered. Dropping the
        // condition instead would make the row do the opposite of what the file says.
        DalilConfig config = DalilConfigLoader.Load("""
            dalil {
                action "Stop" when-context="nothing" { equalise }
                action "Start" unless-context="nothing" { equalise }
            }
            """);

        Assert.Equal("nothing", Named(config, "Stop").WhenContext);
        Assert.Equal("nothing", Named(config, "Start").UnlessContext);

        IReadOnlyList<PaletteEntry> rows = Rows(config, Holding());

        Assert.Equal("Start", Assert.Single(rows).Primary);
    }

    [Fact]
    public void WithNoContextsSectionTheHintSaysToAddOne()
    {
        Diagnostic wrong = Assert.Single(Check("""
            dalil {
                action "Stop" when-context="focusing" { equalise }
            }
            """), d => d.Code == "DAL0021");

        Assert.Contains("No contexts are declared", wrong.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void UnlessContextIsCheckedAgainstTheDeclaredNamesToo()
    {
        Diagnostic wrong = Assert.Single(Check(Declared + """

            dalil {
                action "Start" unless-context="focus" { equalise }
            }
            """), d => d.Code == "DAL0021");

        Assert.Contains("unless-context", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeclaredNamesAreMatchedWithoutRegardToCase()
    {
        // The window manager compares context names that way, so a condition written
        // in another case follows the same context rather than waiting for a twin.
        Assert.DoesNotContain(Check(Declared + """

            dalil {
                action "Stop" when-context="Focusing" { equalise }
            }
            """), d => d.Code == "DAL0021");
    }

    [Fact]
    public void BothKeysNamingOneContextIsARowThatCanNeverBeOffered()
    {
        Diagnostic wrong = Assert.Single(Check(Declared + """

            dalil {
                action "Never" when-context="focusing" unless-context="focusing" { equalise }
            }
            """), d => d.Code == "DAL0022");

        Assert.Equal(DiagnosticSeverity.Warning, wrong.Severity);
        Assert.Contains("never be offered", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoDifferentContextsOnOneActionAreFine()
    {
        Assert.Empty(Check(Declared + """

            dalil {
                action "Mute" when-context="meeting-live" unless-context="focusing" { signal "ayn" "microphone" "mute" }
            }
            """));
    }

    [Fact]
    public void AMisspeltKeyOnAnActionIsReportedRatherThanIgnored()
    {
        // The failure the feature is most likely to produce: a condition that was
        // typed slightly wrong is no condition at all, and the row is offered whatever
        // holds with nothing anywhere to say that the file asked otherwise.
        Diagnostic wrong = Assert.Single(Check(Declared + """

            dalil {
                action "Stop" when-contxt="focusing" { equalise }
            }
            """), d => d.Code == "DAL0023");

        Assert.Equal(DiagnosticSeverity.Warning, wrong.Severity);
        Assert.Contains("when-contxt", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("when-context", wrong.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptionIsStillTheOrdinaryProperty()
    {
        Assert.Empty(Check("""dalil { action "Tidy" description="Equal shares" { equalise } }"""));
    }

    // ---- judging the condition -------------------------------------------------------

    [Fact]
    public void WhenContextHoldsOnlyWhileTheContextIsHeld()
    {
        PaletteMacro stop = Named(DalilConfigLoader.Load(Timer), "Stop focus timer");

        Assert.True(stop.Conditional);
        Assert.True(stop.Applies(["focusing"]));
        Assert.True(stop.Applies(["presenting", "focusing"]));
        Assert.False(stop.Applies([]));
        Assert.False(stop.Applies(null));
        Assert.False(stop.Applies(["presenting"]));
    }

    [Fact]
    public void UnlessContextHoldsOnlyWhileTheContextIsNotHeld()
    {
        PaletteMacro start = Named(DalilConfigLoader.Load(Timer), "Start focus timer");

        Assert.True(start.Applies(null));
        Assert.True(start.Applies([]));
        Assert.True(start.Applies(["presenting"]));
        Assert.False(start.Applies(["focusing"]));
    }

    [Fact]
    public void HeldNamesAreComparedWithoutRegardToCase()
    {
        PaletteMacro stop = Named(DalilConfigLoader.Load(Timer), "Stop focus timer");

        Assert.True(stop.Applies(["FOCUSING"]));
    }

    [Fact]
    public void TheReasonIsWordedAsTheRule()
    {
        // Read on a row shown precisely to explain why it is not in the ordinary list,
        // so the rule - what would make it appear - is what the reader needs.
        DalilConfig config = DalilConfigLoader.Load(Timer);

        Assert.Equal("only while context 'focusing' holds", Named(config, "Stop focus timer").WhyNotNow(null));
        Assert.Equal("not while context 'focusing' holds", Named(config, "Start focus timer").WhyNotNow(["focusing"]));
        Assert.Null(Named(config, "Tidy").WhyNotNow(["focusing"]));
    }

    [Fact]
    public void BothKeysMustHoldTogether()
    {
        PaletteMacro mute = Named(DalilConfigLoader.Load(Declared + """

            dalil {
                action "Mute" when-context="meeting-live" unless-context="focusing" { equalise }
            }
            """), "Mute");

        Assert.True(mute.Applies(["meeting-live"]));
        Assert.False(mute.Applies(["meeting-live", "focusing"]));
        Assert.False(mute.Applies(["focusing"]));
        Assert.False(mute.Applies([]));
    }

    // ---- the rows --------------------------------------------------------------------

    [Fact]
    public void TheCommandListShowsTheHalfThatApplies()
    {
        DalilConfig config = DalilConfigLoader.Load(Timer);

        IReadOnlyList<PaletteEntry> idle = Rows(config, Holding());
        Assert.Equal(["Start focus timer", "Tidy"], idle.Select(r => r.Primary));

        IReadOnlyList<PaletteEntry> running = Rows(config, Holding("focusing"));
        Assert.Equal(["Stop focus timer", "Tidy"], running.Select(r => r.Primary));
    }

    [Fact]
    public void TheRowThatIsShownIsAnOrdinaryRow()
    {
        PaletteEntry stop = Assert.Single(Rows(DalilConfigLoader.Load(Timer), Holding("focusing")), r => r.Primary == "Stop focus timer");

        Assert.False(stop.Unavailable);
        Assert.Equal("signal focus stop", stop.Command);
        Assert.Equal(["macro"], stop.Badges);
        Assert.Equal(10, stop.Rank);
    }

    [Fact]
    public void WithoutAStatusEveryRowIsOffered()
    {
        // The overloads that predate conditions know nothing about what holds, and
        // offer everything as they always did.
        DalilConfig config = DalilConfigLoader.Load(Timer);

        Assert.Equal(3, PaletteEntries.ForMacros(config.Macros).Count);
        Assert.Equal(3, PaletteEntries.ForMacros(config.Macros, CompletionSources.None, labels: null).Count);
        Assert.Equal(3, Rows(config, status: null).Count);
    }

    [Fact]
    public void TheListOfEveryActionShowsTheOtherHalfGreyedWithTheReason()
    {
        IReadOnlyList<PaletteEntry> rows = Rows(DalilConfigLoader.Load(Timer), Holding("focusing"), everything: true);

        Assert.Equal(3, rows.Count);

        PaletteEntry start = Assert.Single(rows, r => r.Primary == "Start focus timer");

        Assert.True(start.Unavailable);
        Assert.Equal(string.Empty, start.Command);
        Assert.Equal("not while context 'focusing' holds", start.Secondary);
        Assert.Contains("not now", start.Badges);
        Assert.Contains("macro", start.Badges);

        // Below the live rows, so the switch's other half reads as such.
        PaletteEntry stop = Assert.Single(rows, r => r.Primary == "Stop focus timer");
        Assert.True(start.Rank < stop.Rank);
    }

    [Fact]
    public void AMistakeOutranksACondition()
    {
        // A row that did not parse says so whether or not it would be offered: the
        // mistake is the more urgent thing to know about it, and a condition that hid
        // it would hide the one message that points at the line.
        DalilConfig config = DalilConfigLoader.Load(Declared + """

            dalil {
                action "Stop" when-context="focusing" { focus --direction "sideways" }
            }
            """);

        PaletteEntry row = Assert.Single(Rows(config, Holding()));

        Assert.Contains("cannot run", row.Badges);
        Assert.DoesNotContain("not now", row.Badges);
    }

    [Fact]
    public void APromptingRowIsKeptBackLikeAnyOther()
    {
        // The prompt is not built for a row that does not apply; the condition is
        // judged before the questions are.
        DalilConfig config = DalilConfigLoader.Load(Declared + """

            dalil {
                action "Focus for..." unless-context="focusing" {
                    param "m" values="15 25 45"
                    signal "focus" "start" "{m}"
                }
            }
            """);

        Assert.Empty(Rows(config, Holding("focusing")));

        PaletteEntry offered = Assert.Single(Rows(config, Holding()));
        Assert.True(offered.Prompts);
    }

    [Fact]
    public void AStatusWithNoContextsHoldsNone()
    {
        // A daemon that predates contexts sends null; the palette's offline status is
        // null too. Neither holds anything, so a when-context row waits and an
        // unless-context row is offered.
        IReadOnlyList<PaletteEntry> rows = Rows(DalilConfigLoader.Load(Timer), WmStatus.Offline);

        Assert.Equal(["Start focus timer", "Tidy"], rows.Select(r => r.Primary));
    }
}
