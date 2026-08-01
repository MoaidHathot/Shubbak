using Shubbak.Config;
using Shubbak.Config.Kdl;

namespace Shubbak.Config.Tests;

/// <summary>Tests for the KDL parser.</summary>
public sealed class KdlParserTests
{
    private static KdlDocument ParseOk(string source)
    {
        KdlParseResult result = KdlParser.Parse(source);

        Assert.False(
            result.HasErrors,
            "Unexpected errors:\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        return result.Document;
    }

    [Fact]
    public void ParsesNodesWithArgumentsAndProperties()
    {
        KdlDocument document = ParseOk("""workspace "3" display-name="Code" monitor=0""");

        KdlNode node = Assert.Single(document.Nodes);
        Assert.Equal("workspace", node.Name);
        Assert.Equal("3", node.Argument(0)!.AsString());
        Assert.Equal("Code", node.Property("display-name")!.AsString());
        Assert.True(node.Property("monitor")!.TryAsInt(out int monitor));
        Assert.Equal(0, monitor);
    }

    [Fact]
    public void ParsesNestedChildren()
    {
        KdlDocument document = ParseOk("""
            gaps {
                inner 6
                outer {
                    top 26
                    left 4
                }
            }
            """);

        KdlNode gaps = Assert.Single(document.Nodes);
        Assert.Equal(6, (int)gaps.Child("inner")!.Argument(0)!.NumberValue);
        Assert.Equal(26, (int)gaps.Child("outer")!.Child("top")!.Argument(0)!.NumberValue);
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("-17", -17)]
    [InlineData("0x1F", 31)]
    [InlineData("0o17", 15)]
    [InlineData("0b1011", 11)]
    [InlineData("1_000", 1000)]
    public void ParsesNumbersInEveryBase(string literal, int expected)
    {
        KdlDocument document = ParseOk($"value {literal}");
        Assert.True(document.Nodes[0].Argument(0)!.TryAsInt(out int actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("#true", true)]
    [InlineData("true", true)]
    [InlineData("#false", false)]
    [InlineData("false", false)]
    public void AcceptsBothKdlOneAndKdlTwoKeywordSpellings(string literal, bool expected)
    {
        // The distinction is invisible to someone copying an example from the web,
        // so rejecting one spelling would be a pointless obstacle.
        KdlDocument document = ParseOk($"enabled {literal}");
        Assert.True(document.Nodes[0].Argument(0)!.TryAsBool(out bool actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParsesEscapesInQuotedStrings()
    {
        KdlDocument document = ParseOk("""value "a\tb\nc\"d\\e" """);
        Assert.Equal("a\tb\nc\"d\\e", document.Nodes[0].Argument(0)!.AsString());
    }

    [Fact]
    public void RawStringsNeedNoEscaping()
    {
        // The whole reason raw strings exist here: regexes are unreadable when every
        // backslash has to be doubled.
        KdlDocument document = ParseOk("""pattern r"Chrome_WidgetWin_1|\d+\s*" """);
        Assert.Equal(@"Chrome_WidgetWin_1|\d+\s*", document.Nodes[0].Argument(0)!.AsString());
    }

    [Fact]
    public void RawStringsWithHashesCanContainQuotes()
    {
        KdlDocument document = ParseOk("""pattern r#"say "hello""#""");
        Assert.Equal("say \"hello\"", document.Nodes[0].Argument(0)!.AsString());
    }

    [Fact]
    public void SkipsLineAndBlockComments()
    {
        KdlDocument document = ParseOk("""
            // a line comment
            first 1
            /* a block
               comment /* that nests */ still inside */
            second 2
            """);

        Assert.Equal(2, document.Nodes.Count);
        Assert.Equal("first", document.Nodes[0].Name);
        Assert.Equal("second", document.Nodes[1].Name);
    }

    [Fact]
    public void SlashdashCommentsOutTheFollowingNode()
    {
        KdlDocument document = ParseOk("""
            kept 1
            /-removed 2
            also-kept 3
            """);

        Assert.Equal(2, document.Nodes.Count);
        Assert.DoesNotContain(document.Nodes, n => n.Name == "removed");
    }

    [Fact]
    public void BackslashContinuesALine()
    {
        KdlDocument document = ParseOk("""
            node "a" \
                 "b"
            """);

        KdlNode node = Assert.Single(document.Nodes);
        Assert.Equal(2, node.Arguments.Count);
    }

    [Fact]
    public void SemicolonSeparatesNodesOnOneLine()
    {
        // Needed so command blocks can be written inline:
        //   bind "alt+3" { move --workspace 3; focus --workspace 3 }
        KdlDocument document = ParseOk("first 1; second 2");

        Assert.Equal(2, document.Nodes.Count);
    }

    [Fact]
    public void BarePunctuationParsesAsAString()
    {
        // Workspace names in the author's config include these.
        KdlDocument document = ParseOk("""
            workspace "-"
            workspace "\\"
            workspace "`"
            """);

        Assert.Equal(["-", "\\", "`"],
            document.Nodes.Select(n => n.Argument(0)!.AsString()));
    }

    [Fact]
    public void UnterminatedStringIsReportedWithItsPosition()
    {
        KdlParseResult result = KdlParser.Parse("""
            good "value"
            bad "unterminated
            """);

        Diagnostic error = Assert.Single(result.Diagnostics, d => d.Code == "SHB0008");
        Assert.Equal(2, error.Span.Start.Line);
        Assert.NotNull(error.Hint);
    }

    [Fact]
    public void ParsingRecoversSoAllErrorsAreReportedAtOnce()
    {
        // Reporting one error per run would turn fixing a config into a slow
        // fix-and-retry loop.
        KdlParseResult result = KdlParser.Parse("""
            first "unterminated
            second "also unterminated
            """);

        Diagnostic[] errors = [.. result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];

        Assert.Equal(2, errors.Length);
        Assert.Equal([1, 2], errors.Select(e => e.Span.Start.Line).Order());

        // The first is diagnosed more precisely - it ran into a newline rather than
        // the end of the file - and both carry a hint.
        Assert.Equal("SHB0009", errors[0].Code);
        Assert.Equal("SHB0008", errors[1].Code);
        Assert.All(errors, e => Assert.NotNull(e.Hint));
    }

    [Fact]
    public void DiagnosticsRenderWithACaretUnderTheOffendingText()
    {
        const string Source = """
            general {
                inner "oops
            }
            """;

        KdlParseResult result = KdlParser.Parse(Source);
        Diagnostic error = result.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error);

        string rendered = error.Render(Source, "shubbak.kdl");

        Assert.Contains("shubbak.kdl:2:", rendered, StringComparison.Ordinal);
        Assert.Contains("^", rendered, StringComparison.Ordinal);
        Assert.Contains("inner \"oops", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatePropertyIsWarnedAbout()
    {
        KdlParseResult result = KdlParser.Parse("""node key="a" key="b" """);

        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0006");
        Assert.Equal("b", result.Document.Nodes[0].Property("key")!.AsString());
    }
}
