using Dalil.Core;
using Shubbak.Config;

namespace Dalil.Core.Tests;

/// <summary>
/// What <c>shubbak check-config</c> says about the <c>dalil</c> section.
/// </summary>
/// <remarks>
/// The section was on the window manager's allow-list by name and its contents were
/// checked by nothing at all, so <c>dalil { with-icons #true }</c> was accepted in
/// silence and did nothing for ever. That is the exact failure this project exists not
/// to have, and it had it in one section for as long as the section existed.
/// </remarks>
public sealed class DalilValidationTests
{
    private static IReadOnlyList<Diagnostic> Check(string source) =>
        DalilConfigLoader.Validate(source).Diagnostics;

    private static Diagnostic Single(string source, string code)
    {
        IReadOnlyList<Diagnostic> all = Check(source);

        return Assert.Single(all, d => d.Code == code);
    }

    // ---- nothing to say about a good file --------------------------------------------

    [Fact]
    public void AConfigWithNoPaletteSectionSaysNothing()
    {
        // Most people have no dalil block, and a validator that complained about its
        // absence would be worse than the silence it replaced.
        Assert.Empty(Check("general { }"));
    }

    [Fact]
    public void AGoodPaletteSectionSaysNothing()
    {
        Assert.Empty(Check("""
            dalil {
                open-on-signal "palette"
                width 720
                visible-rows 12
                confirm-destructive #true
                placement "cursor-monitor"
                background "#16161C"
                prefixes { layouts "l" }
                action "Tidy" { equalise }
            }
            """));
    }

    [Fact]
    public void SyntaxErrorsAreLeftToTheWindowManager()
    {
        // It reads the same text and reports them with carets, and check-config runs
        // both - so repeating them here would print every syntax error twice.
        Assert.Empty(Check("dalil { width "));
    }

    // ---- settings --------------------------------------------------------------------

    [Fact]
    public void AMisspeltSettingIsReportedAndGuessedAt()
    {
        Diagnostic wrong = Single("dalil { show-icon #true }", "DAL0001");

        Assert.Contains("show-icon", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("show-icons", wrong.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingWrittenAsAPropertyIsCheckedToo()
    {
        // Both shapes are accepted everywhere in this configuration, so checking only
        // child nodes would leave half the misspellings unreported depending on which
        // style somebody prefers.
        Assert.Single(Check("""dalil width=720 nonsense=1 { }"""), d => d.Code == "DAL0001");
    }

    [Fact]
    public void EveryDocumentedSettingSurvivesTheCheck()
    {
        // Guards the list against the code: a setting Read looks at but KnownKeys has
        // forgotten would be reported as a misspelling of itself.
        string body = string.Join('\n', DalilConfigLoader.KnownKeys.Select(Sample));

        Assert.DoesNotContain(Check($"dalil {{\n{body}\n}}"), d => d.Code == "DAL0001");
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

    [Fact]
    public void ANumberOutsideItsRangeSaysWhatWillBeUsedInstead()
    {
        // Clamping in silence is how a palette ends up 240 pixels wide for somebody who
        // asked for 24 and is now looking for the bug in the wrong place.
        Diagnostic wrong = Single("dalil { width 24 }", "DAL0012");

        Assert.Contains("240", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumberThatIsNotANumberKeepsTheDefault()
    {
        Assert.Contains("720", Single("""dalil { width "wide" }""", "DAL0011").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownPlacementListsTheRealOnes()
    {
        Assert.Contains(
            "cursor-monitor",
            Single("""dalil { placement "elsewhere" }""", "DAL0009").Hint ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotAColourIsReported()
    {
        Assert.Contains(
            "background",
            Single("""dalil { background "octarine" }""", "DAL0010").Message,
            StringComparison.Ordinal);
    }

    // ---- prefixes ---------------------------------------------------------------------

    [Fact]
    public void APrefixForSomethingThatIsNotAModeIsReported()
    {
        Diagnostic wrong = Single("""dalil { prefixes { gibberish "g" } }""", "DAL0002");

        Assert.Contains("scratchpad", wrong.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void APrefixLongerThanOneCharacterCouldNeverMatch()
    {
        // The mode is decided from the first character typed, so accepting "lay" would
        // mean silently using "l" and leaving the user to work that out.
        Diagnostic wrong = Single("""dalil { prefixes { layouts "lay" } }""", "DAL0003");

        Assert.Contains("single character", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GivingUpAPrefixIsNotAMistake()
    {
        Assert.Empty(Check("""dalil { prefixes { monitors "" } }"""));
    }

    [Fact]
    public void TakingACharacterFromAnotherModeSaysWhichModeLostIt()
    {
        // Accepted - an explicit wish beats a default, deliberately - and its quiet
        // consequence is that the command list loses the prefix it has always had.
        Diagnostic wrong = Single("""dalil { prefixes { monitors ">" } }""", "DAL0004");

        Assert.Contains("commands", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepingAModesOwnPrefixIsNotAClashWithItself()
    {
        Assert.Empty(Check("""dalil { prefixes { commands ">" } }"""));
    }

    [Fact]
    public void SwappingTwoPrefixesIsNotAClashEither()
    {
        // Both give theirs up in the same block, so neither is taking anything the
        // other still holds.
        Assert.DoesNotContain(
            Check("""dalil { prefixes { commands "#"; workspaces ">" } }"""),
            d => d.Code == "DAL0004");
    }

    // ---- actions -----------------------------------------------------------------------

    [Fact]
    public void AnActionWithNoNameIsAnError()
    {
        Assert.Equal(
            DiagnosticSeverity.Error,
            Single("dalil { action { equalise } }", "DAL0005").Severity);
    }

    [Fact]
    public void TwoActionsWithOneNameCannotBeToldApart()
    {
        Assert.Single(
            Check("dalil { action \"A\" { equalise }\n action \"A\" { wm-redraw } }"),
            d => d.Code == "DAL0006");
    }

    [Fact]
    public void ACommandThatDoesNotParseIsReportedInTheParsersOwnWords()
    {
        // Rather than a friendlier message written here, which would mean two
        // vocabularies for the same mistake - one in an action and a different one in
        // a keybinding three sections up.
        Diagnostic wrong = Single(
            """dalil { action "Broken" { focus --direction "sideways" } }""", "DAL0007");

        Assert.Equal(DiagnosticSeverity.Error, wrong.Severity);
        Assert.Contains("sideways", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("Broken", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnActionWithNothingInItWouldDoNothing()
    {
        Assert.Single(Check("""dalil { action "Empty" { } }"""), d => d.Code == "DAL0008");
    }

    [Fact]
    public void ADescriptionWrittenAsAChildIsNotMistakenForACommand()
    {
        // Every other setting in this file can be a child node or a property, so
        // somebody will write it as a child - and it must not then be parsed as a verb
        // called "description".
        Assert.Empty(Check("""
            dalil {
                action "Tidy" {
                    description "puts things back"
                    equalise
                }
            }
            """));
    }

    [Fact]
    public void TheRowStillSaysWhatWentWrongWhenNobodyIsCollectingDiagnostics()
    {
        // The palette itself loads without a diagnostic list, and the broken macro has
        // to explain itself on the row - the user is not running check-config at the
        // moment they press Enter on it.
        DalilConfig config = DalilConfigLoader.Load(
            """dalil { action "Broken" { focus --direction "sideways" } }""");

        PaletteMacro macro = Assert.Single(config.Macros);

        Assert.NotNull(macro.Problem);
        Assert.Contains("sideways", macro.Problem!, StringComparison.Ordinal);
    }

    // ---- questions an action asks before it runs -------------------------------------

    [Fact]
    public void AParameterisedActionWithNothingWrongWithItSaysNothing()
    {
        Assert.Empty(Check("""
            dalil {
                action "Send it to..." {
                    param "ws" from="workspaces"
                    move --workspace "{ws}"
                }
            }
            """));
    }

    [Fact]
    public void APlaceholderInADirectionIsCheckedWithARealDirection()
    {
        // `focus --direction "{d}"` is not a direction and never will be, so the line
        // can only be checked once the question has been answered. Probing it with a
        // real one checks everything about the line except the value the user supplies.
        Assert.Empty(Check("""
            dalil {
                action "Shove it..." {
                    param "d" from="directions"
                    move --direction "{d}"
                }
            }
            """));
    }

    [Fact]
    public void AWrittenOutChoiceThatTheParserWillRefuseIsRefusedHere()
    {
        // The probe uses the first written value, so a list containing something the
        // parser cannot accept is caught at the line that declared it rather than at a
        // keystroke a fortnight later.
        Diagnostic wrong = Single("""
            dalil {
                action "Shove it..." {
                    param "d" values="sideways left"
                    move --direction "{d}"
                }
            }
            """, "DAL0007");

        Assert.Contains("sideways", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APlaceholderNobodyDeclaredIsReported()
    {
        // The one mistake the parser cannot catch: `focus --workspace "{wsp}"` is a
        // perfectly valid request to focus a workspace literally called "{wsp}", so it
        // parses, loads, runs, and is refused at the far end of a keystroke.
        Diagnostic wrong = Single("""
            dalil {
                action "Send it to..." {
                    param "ws" from="workspaces"
                    move --workspace "{wsp}"
                }
            }
            """, "DAL0013");

        Assert.Contains("{wsp}", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuestionNothingAsksIsReported()
    {
        Diagnostic wrong = Single("""
            dalil {
                action "Tidy" {
                    param "ws" from="workspaces"
                    equalise
                }
            }
            """, "DAL0014");

        Assert.Equal(DiagnosticSeverity.Warning, wrong.Severity);
        Assert.Contains("never uses it", wrong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnusedQuestionIsDroppedSoTheRowDoesNotStopToAskIt()
    {
        // The warning is not enough on its own. A row that collects a value and then
        // runs a sequence which ignores it reads as the palette having lost the answer.
        DalilConfig config = DalilConfigLoader.Load("""
            dalil {
                action "Tidy" {
                    param "ws" from="workspaces"
                    equalise
                }
            }
            """);

        Assert.False(Assert.Single(config.Macros).Asks);
    }

    [Fact]
    public void AParamWithNoNameIsReported()
    {
        Single("""dalil { action "Go" { param; focus --workspace "1" } }""", "DAL0015");
    }

    [Fact]
    public void AParamDeclaredTwiceIsReported()
    {
        Single("""
            dalil {
                action "Go" {
                    param "ws" from="workspaces"
                    param "ws" from="layouts"
                    focus --workspace "{ws}"
                }
            }
            """, "DAL0016");
    }

    [Fact]
    public void AListNobodyHasIsReportedWithASuggestion()
    {
        Diagnostic wrong = Single("""
            dalil {
                action "Go" {
                    param "l" from="layout s"
                    layout --set "{l}"
                }
            }
            """, "DAL0017");

        Assert.Contains("layouts", wrong.Hint!, StringComparison.Ordinal);
    }
}
