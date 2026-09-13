namespace Shubbak.Config.Tests;

/// <summary>
/// Every <c>rules { }</c> block counts, and a rule is told when its verb cannot act.
/// </summary>
/// <remarks>
/// <para>
/// The loader used to read the first <c>rules</c> block and drop every other one in
/// perfect silence - no diagnostic, nothing in the log. The starter config ships with a
/// block, so "paste this at the end of your file" was wrong advice for every file the
/// starter produced, and a tool appending a block would have appended one that did
/// nothing.
/// </para>
/// <para>
/// And <c>ignore</c> and <c>manage</c> decide whether a window is taken on at all,
/// which happens once, on the manage trigger. Written under <c>title-change</c> or
/// <c>focus</c> they parsed, loaded, and were stripped at the moment the rule fired.
/// </para>
/// </remarks>
public sealed class MergedRulesBlocksTests
{
    [Fact]
    public void EveryRulesBlockIsRead()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            rules {
                rule "first" { match { process "a" } do { float } }
            }

            rules {
                rule "second" { match { process "b" } do { ignore } }
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Equal(["first", "second"], result.Config.Rules.Select(r => r.Name));
    }

    [Fact]
    public void RulesKeepFileOrderAcrossBlocks()
    {
        // Order is what decides which of two matching rules wins an argument, so a
        // block appended later has to sort after the ones already there.
        ConfigLoadResult result = ConfigLoader.Load("""
            rules { rule "a" { match { process "x" } do { float } } }
            rules { rule "b" { match { process "x" } do { tile } } }
            rules { rule "c" { match { process "x" } do { ignore } } }
            """);

        Assert.Equal(["a", "b", "c"], result.Config.Rules.Select(r => r.Name));
    }

    [Fact]
    public void UnnamedRulesAreNumberedAcrossBlocks()
    {
        // Two blocks each holding an unnamed rule must not both produce "rule #1": the
        // name is how a report identifies the rule and how a removal finds it.
        ConfigLoadResult result = ConfigLoader.Load("""
            rules { rule { match { process "x" } do { float } } }
            rules { rule { match { process "y" } do { tile } } }
            """);

        Assert.Equal(["rule #1", "rule #2"], result.Config.Rules.Select(r => r.Name));
        Assert.Equal("rule #2", ConfigLoader.DefaultRuleName(2));
    }

    [Fact]
    public void AContextReadsEveryRulesBlockToo()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            contexts {
                context "docked" {
                    rules { rule "a" { match { process "x" } do { float } } }
                    rules { rule "b" { match { process "y" } do { tile } } }
                }
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Config.Contexts[0].Effects.Rules.Count);
    }

    [Theory]
    [InlineData("ignore", "title-change")]
    [InlineData("manage", "title-change")]
    [InlineData("ignore", "focus")]
    [InlineData("manage", "focus")]
    public void AnAdoptionVerbOnTheWrongTriggerIsReported(string verb, string trigger)
    {
        ConfigLoadResult result = ConfigLoader.Load($$"""
            rules {
                rule "late" on="{{trigger}}" {
                    match { process "x" }
                    do { {{verb}} }
                }
            }
            """);

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0452");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains(verb, warning.Message, StringComparison.Ordinal);
        Assert.Contains(trigger, warning.Message, StringComparison.Ordinal);

        // A warning, not a refusal: the rule is kept for whatever else it does.
        Assert.Single(result.Config.Rules);
    }

    [Fact]
    public void AnAdoptionVerbOnTheManageTriggerIsFine()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            rules {
                rule "early" { match { process "x" } do { ignore } }
                rule "explicit" on="manage" { match { process "y" } do { manage } }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SHB0452");
    }

    [Fact]
    public void AnUnreadableFileIsADiagnosticNotAnException()
    {
        // The reload paths run while an editor may still hold the file. An exception
        // from here used to travel into the message loop.
        string directory = Path.Combine(Path.GetTempPath(), $"shubbak-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            string path = Path.Combine(directory, "shubbak.kdl");
            File.WriteAllText(path, "general { }");

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ConfigLoadResult result = ConfigLoader.LoadFile(path);

                Assert.True(result.HasErrors);
                Diagnostic error = Assert.Single(result.Diagnostics);
                Assert.Equal("SHB0400", error.Code);
                Assert.Contains("could not be read", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
