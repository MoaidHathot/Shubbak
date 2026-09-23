namespace Shubbak.Config.Tests;

/// <summary>
/// Adding a rule to somebody's file, and taking it out again, without touching the
/// rest of it.
/// </summary>
/// <remarks>
/// The file is hand-written configuration. Every test here that checks bytes is
/// checking the one promise that matters: the lines that were not edited are the same
/// lines afterwards, line endings, byte-order mark and trailing newline included.
/// </remarks>
public sealed class ConfigEditorTests
{
    private const string Rule = """
        rules {
            rule "ignore teams" {
                match {
                    class "TeamsWebView"
                    process "ms-teams.exe"
                }
                do {
                    ignore
                }
            }
        }
        """;

    private const string Existing = """
        general {
            follow-window-on-move #true
        }

        rules {
            rule "float calc" {
                match { process "CalculatorApp" }
                do { float }
            }
        }
        """;

    // ---- adding ---------------------------------------------------------------------

    [Fact]
    public void ARuleIsAppendedAfterABlankLineAndTheFileStillEndsOnANewline()
    {
        ConfigEdit edit = ConfigEditor.PlanAddition(Existing + "\n", Rule, allowShellExec: false, marker: null);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.StartsWith(Existing + "\n\n", edit.Text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", edit.Text, StringComparison.Ordinal);
        Assert.Equal(["ignore teams"], edit.Names);

        // The rule that was read is the rule that landed.
        Assert.Contains(Rule.Replace("\r\n", "\n", StringComparison.Ordinal), edit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLineReportedIsWhereTheRuleBegins()
    {
        ConfigEdit edit = ConfigEditor.PlanAddition(Existing + "\n", Rule, allowShellExec: false, marker: null);

        string[] lines = edit.Text.Split('\n');

        // 1-based, and the same count the loader's spans use, so the number a report
        // shows is the number a removal will be asked for.
        Assert.Contains("rule \"ignore teams\"", lines[edit.Line - 1], StringComparison.Ordinal);
        Assert.Equal(edit.Line, ConfigLoader.Load(edit.Text).Config.Rules[^1].Span.Start.Line);
    }

    [Fact]
    public void AFileWithNoTrailingNewlineGetsOneBeforeTheAddition()
    {
        ConfigEdit edit = ConfigEditor.PlanAddition(Existing, Rule, allowShellExec: false, marker: null);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.StartsWith(Existing + "\n\n", edit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileAlreadyEndingOnABlankLineDoesNotGetASecond()
    {
        ConfigEdit edit = ConfigEditor.PlanAddition(Existing + "\n\n", Rule, allowShellExec: false, marker: null);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.StartsWith(Existing + "\n\nrules {", edit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFilesLineEndingsAreUsedForTheAddition()
    {
        string crlf = Existing.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";

        ConfigEdit edit = ConfigEditor.PlanAddition(crlf, Rule, allowShellExec: false, marker: null);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.DoesNotContain(edit.Text.Replace("\r\n", "", StringComparison.Ordinal), "\n", StringComparison.Ordinal);
        Assert.EndsWith("}\r\n", edit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFileTakesTheRuleAlone()
    {
        ConfigEdit edit = ConfigEditor.PlanAddition(string.Empty, Rule, allowShellExec: false, marker: null);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.StartsWith("rules {", edit.Text, StringComparison.Ordinal);
        Assert.Equal(2, edit.Line);
    }

    [Fact]
    public void TheMarkerIsWrittenAboveTheBlock()
    {
        ConfigEdit edit = ConfigEditor.PlanAddition(
            Existing + "\n", Rule, allowShellExec: false, marker: ConfigEditor.Marker + " on 2026-09-13");

        Assert.Contains("\n\n// Added by Shubbak on 2026-09-13\nrules {", edit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BareRulesAreWrappedInABlock()
    {
        // What a removal hands back is the rule alone, and "put it back" has to work.
        ConfigEdit edit = ConfigEditor.PlanAddition(
            Existing + "\n",
            "rule \"x\" {\n    match { process \"x\" }\n    do { tile }\n}",
            allowShellExec: false,
            marker: null);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.Contains("rules {\n    rule \"x\" {\n        match { process \"x\" }\n        do { tile }\n    }\n}\n", edit.Text, StringComparison.Ordinal);
        Assert.Equal(["x"], edit.Names);
    }

    [Fact]
    public void AnythingOtherThanRulesIsRefused()
    {
        // The block arrives over a pipe every process running as the user can open,
        // and `general { allow-shell-exec-over-ipc #true }` would take effect.
        ConfigEdit edit = ConfigEditor.PlanAddition(
            Existing, "general { allow-shell-exec-over-ipc #true }", allowShellExec: false, marker: null);

        Assert.False(edit.Accepted);
        Assert.Contains("Only rules", edit.Refusal, StringComparison.Ordinal);
        Assert.Contains("general", edit.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARulesBlockHoldingSomethingElseIsRefused()
    {
        ConfigEdit edit = ConfigEditor.PlanAddition(
            Existing, "rules { rule \"a\" { match { process \"x\" } do { float } }; general { } }",
            allowShellExec: false, marker: null);

        Assert.False(edit.Accepted);
        Assert.Contains("Only rules", edit.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleTheLoaderWouldDropIsRefused()
    {
        // An empty do block is dropped with a warning, not an error, so the file would
        // load and the rule would silently not exist.
        ConfigEdit edit = ConfigEditor.PlanAddition(
            Existing,
            "rules { rule \"empty\" { match { process \"x\" } do { } } }",
            allowShellExec: false,
            marker: null);

        Assert.False(edit.Accepted);
        Assert.Contains("drop", edit.Refusal, StringComparison.Ordinal);
        Assert.Contains(edit.Diagnostics, d => d.Code == "SHB0418");
    }

    [Fact]
    public void ARuleWithNoConditionsIsRefused()
    {
        ConfigEdit edit = ConfigEditor.PlanAddition(
            Existing, "rules { rule \"all\" { do { ignore } } }", allowShellExec: false, marker: null);

        Assert.False(edit.Accepted);
        Assert.Contains(edit.Diagnostics, d => d.Code == "SHB0417");
    }

    [Fact]
    public void ARuleThatDoesNotParseIsRefused()
    {
        ConfigEdit edit = ConfigEditor.PlanAddition(Existing, "rules { rule \"x\" {", allowShellExec: false, marker: null);

        Assert.False(edit.Accepted);
        Assert.Contains("parse", edit.Refusal, StringComparison.Ordinal);
        Assert.NotEmpty(edit.Diagnostics);
    }

    [Fact]
    public void AFileThatAlreadyHasErrorsIsLeftAlone()
    {
        // The reload would keep the old configuration whatever is added, and the
        // addition would look like it had silently failed.
        ConfigEdit edit = ConfigEditor.PlanAddition(
            "rules { rule \"bad\" { match { process regex=\"(\" } do { float } } }",
            Rule, allowShellExec: false, marker: null);

        Assert.False(edit.Accepted);
        Assert.Contains("already has errors", edit.Refusal, StringComparison.Ordinal);
        Assert.Contains(edit.Diagnostics, d => d.Code == "SHB0415");
    }

    [Fact]
    public void ShellExecIsRefusedUnlessPermitted()
    {
        const string launches = "rules { rule \"x\" { match { process \"x\" } do { shell-exec notepad } } }";

        ConfigEdit refused = ConfigEditor.PlanAddition(Existing, launches, allowShellExec: false, marker: null);
        ConfigEdit allowed = ConfigEditor.PlanAddition(Existing, launches, allowShellExec: true, marker: null);

        Assert.False(refused.Accepted);
        Assert.Contains("shell-exec", refused.Refusal, StringComparison.Ordinal);
        Assert.True(allowed.Accepted, allowed.Refusal);
    }

    [Fact]
    public void NothingToAddIsSaidPlainly()
    {
        Assert.False(ConfigEditor.PlanAddition(Existing, "   \n", allowShellExec: false, marker: null).Accepted);
        Assert.False(ConfigEditor.PlanAddition(Existing, "rules { }", allowShellExec: false, marker: null).Accepted);
    }

    // ---- removing -------------------------------------------------------------------

    [Fact]
    public void AddingAndRemovingLeavesTheFileAsItWasFound()
    {
        // The whole point of the marker and of taking an emptied block with the rule.
        //
        // Up to the last newline. A file that ended without one, or with several, gets
        // exactly one from the round trip: the addition cannot land without a line of
        // its own, and the removal cannot know how the file used to end.
        foreach (string original in new[] { Existing + "\n", Existing, Existing + "\n\n" })
        {
            ConfigEdit added = ConfigEditor.PlanAddition(
                original, Rule, allowShellExec: false, marker: ConfigEditor.Marker + " from a test");

            Assert.True(added.Accepted, added.Refusal);

            ConfigEdit removed = ConfigEditor.PlanRemoval(added.Text, "ignore teams", added.Line);

            Assert.True(removed.Accepted, removed.Refusal);
            Assert.Equal(Existing + "\n", removed.Text);
        }
    }

    [Fact]
    public void AddingAndRemovingRoundTripsThroughCrlf()
    {
        string crlf = Existing.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";

        ConfigEdit added = ConfigEditor.PlanAddition(crlf, Rule, allowShellExec: false, marker: ConfigEditor.Marker);
        ConfigEdit removed = ConfigEditor.PlanRemoval(added.Text, "ignore teams", added.Line);

        Assert.True(removed.Accepted, removed.Refusal);
        Assert.Equal(crlf, removed.Text);
    }

    [Fact]
    public void RemovingOneRuleFromABlockOfSeveralLeavesTheOthers()
    {
        const string file = """
            rules {
                rule "a" {
                    match { process "a" }
                    do { float }
                }
                rule "b" {
                    match { process "b" }
                    do { tile }
                }
                rule "c" {
                    match { process "c" }
                    do { ignore }
                }
            }

            """;

        ConfigEdit edit = ConfigEditor.PlanRemoval(file, "b", 6);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.Equal("""
            rules {
                rule "a" {
                    match { process "a" }
                    do { float }
                }
                rule "c" {
                    match { process "c" }
                    do { ignore }
                }
            }

            """, edit.Text);

        Assert.Equal("rule \"b\" {\n    match { process \"b\" }\n    do { tile }\n}", edit.RuleText);
        Assert.Equal(["b"], edit.Names);
    }

    [Fact]
    public void TheRemovedRuleIsHandedBackDedentedSoItCanBePutBack()
    {
        ConfigEdit added = ConfigEditor.PlanAddition(Existing + "\n", Rule, allowShellExec: false, marker: null);
        ConfigEdit removed = ConfigEditor.PlanRemoval(added.Text, "ignore teams", added.Line);

        Assert.StartsWith("rule \"ignore teams\" {\n    match {", removed.RuleText, StringComparison.Ordinal);

        ConfigEdit back = ConfigEditor.PlanAddition(removed.Text, removed.RuleText, allowShellExec: false, marker: null);

        Assert.True(back.Accepted, back.Refusal);
        Assert.Equal(["ignore teams"], back.Names);
    }

    [Fact]
    public void ABlockLeftHoldingACommentStays()
    {
        // The comment was written by somebody. The block stays to hold it.
        const string file = """
            rules {
                // rules for browsers
                rule "a" {
                    match { process "a" }
                    do { float }
                }
            }

            """;

        ConfigEdit edit = ConfigEditor.PlanRemoval(file, "a", 3);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.Equal("rules {\n    // rules for browsers\n}\n", edit.Text);
    }

    [Fact]
    public void ARuleInsideAContextCanBeRemoved()
    {
        const string file = """
            contexts {
                context "docked" {
                    rules {
                        rule "a" {
                            match { process "a" }
                            do { float }
                        }
                    }
                }
            }

            """;

        ConfigEdit edit = ConfigEditor.PlanRemoval(file, "a", 4);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.Equal("contexts {\n    context \"docked\" {\n    }\n}\n", edit.Text);
    }

    [Fact]
    public void AnUnnamedRuleIsFoundByTheNameTheLoaderGaveIt()
    {
        const string file = """
            rules { rule { match { process "a" } do { float } } }
            rules {
                rule {
                    match { process "b" }
                    do { tile }
                }
            }

            """;

        ConfigEdit edit = ConfigEditor.PlanRemoval(file, "rule #2", 3);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.Equal("rules { rule { match { process \"a\" } do { float } } }\n", edit.Text);
    }

    [Fact]
    public void ARuleSharingItsLineWithAnotherLosesOnlyItself()
    {
        const string file = "rules { rule \"a\" { match { process \"a\" } do { float } }; rule \"b\" { match { process \"b\" } do { tile } } }\n";

        ConfigEdit edit = ConfigEditor.PlanRemoval(file, "a", 1);

        Assert.True(edit.Accepted, edit.Refusal);
        Assert.Equal("rules { rule \"b\" { match { process \"b\" } do { tile } } }\n", edit.Text);
    }

    [Fact]
    public void TheWrongLineIsRefused()
    {
        ConfigEdit edit = ConfigEditor.PlanRemoval(Existing, "float calc", 2);

        Assert.False(edit.Accepted);
        Assert.Contains("No rule begins at line 2", edit.Refusal, StringComparison.Ordinal);
        Assert.Equal(Existing, Existing);
    }

    [Fact]
    public void TheWrongNameAtTheRightLineIsRefused()
    {
        // The report is a snapshot and the file may have moved on. Deleting whatever
        // now sits at that line is how a tool destroys a configuration.
        ConfigEdit edit = ConfigEditor.PlanRemoval(Existing, "ignore teams", 6);

        Assert.False(edit.Accepted);
        Assert.Contains("'float calc'", edit.Refusal, StringComparison.Ordinal);
        Assert.Contains("inspect the window again", edit.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatDoesNotParseIsNotEdited()
    {
        ConfigEdit edit = ConfigEditor.PlanRemoval("rules { rule \"a\" {", "a", 1);

        Assert.False(edit.Accepted);
        Assert.Contains("does not parse", edit.Refusal, StringComparison.Ordinal);
    }

    // ---- the file itself ------------------------------------------------------------

    [Fact]
    public void AByteOrderMarkIsKeptAcrossAnEdit()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"shubbak-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            string path = Path.Combine(directory, "shubbak.kdl");

            byte[] bom = [0xEF, 0xBB, 0xBF];
            File.WriteAllBytes(path, [.. bom, .. System.Text.Encoding.UTF8.GetBytes(Existing + "\n")]);

            ConfigText read = ConfigFile.Read(path);

            Assert.True(read.HasBom);
            Assert.Equal(Existing + "\n", read.Text);

            ConfigFile.Write(path, read.Text + "// more\n", read.HasBom);

            byte[] written = File.ReadAllBytes(path);

            Assert.Equal(bom, written[..3]);
            Assert.Equal(Existing + "\n// more\n", System.Text.Encoding.UTF8.GetString(written, 3, written.Length - 3));

            // And a file without one does not acquire one.
            File.WriteAllText(path, Existing);
            ConfigText plain = ConfigFile.Read(path);
            Assert.False(plain.HasBom);
            ConfigFile.Write(path, plain.Text, plain.HasBom);
            Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);

            // Nothing is left beside the file.
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AUtf16FileIsRefusedRatherThanDestroyed()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"shubbak-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            string path = Path.Combine(directory, "shubbak.kdl");
            File.WriteAllText(path, Existing, System.Text.Encoding.Unicode);

            Assert.Throws<InvalidDataException>(() => ConfigFile.Read(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheNewlineOfAFileIsReadOffItsFirstLineBreak()
    {
        Assert.Equal("\r\n", ConfigEditor.NewlineOf("a\r\nb\nc"));
        Assert.Equal("\n", ConfigEditor.NewlineOf("a\nb\r\n"));
        Assert.Equal("\n", ConfigEditor.NewlineOf("no breaks"));
    }
}
