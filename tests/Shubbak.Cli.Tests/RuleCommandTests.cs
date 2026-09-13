using Shubbak.Ipc;

namespace Shubbak.Cli.Tests;

/// <summary>
/// <c>shubbak rule</c>: what a person can type after it, and what it prints back.
/// </summary>
/// <remarks>
/// The pipe half is the window manager's and is tested there. What is tested here is
/// the part that is this program's alone: reading the arguments, and saying what
/// happened in words.
/// </remarks>
public sealed class RuleCommandTests
{
    private static RuleAddArguments Parse(params string[] args)
    {
        RuleAddArguments? parsed = RuleAddArguments.Parse(args, out string? problem);
        Assert.True(parsed is not null, problem);
        return parsed!;
    }

    private static string Problem(params string[] args)
    {
        RuleAddArguments? parsed = RuleAddArguments.Parse(args, out string? problem);
        Assert.Null(parsed);
        return problem!;
    }

    [Fact]
    public void AVerbAndAHandleMakeARule()
    {
        RuleAddArguments parsed = Parse("0x2A", "--ignore");

        Assert.Equal((nint)0x2A, parsed.Handle);
        Assert.Equal(["ignore"], parsed.Does);
        Assert.Null(parsed.File);
        Assert.False(parsed.Print);
    }

    [Fact]
    public void TheHandleMayComeAfterTheVerbAndInDecimal()
    {
        RuleAddArguments parsed = Parse("--manage", "--float", "42");

        Assert.Equal((nint)42, parsed.Handle);
        Assert.Equal(["manage", "float"], parsed.Does);
    }

    [Fact]
    public void NoHandleMeansTheForegroundWindowInAMoment()
    {
        Assert.Equal((nint)0, Parse("--ignore").Handle);
    }

    [Fact]
    public void AWorkspaceBecomesAMove()
    {
        // Quoted and escaped the way the composer writes every value, so a workspace
        // called `"` cannot break the rule it lands in.
        Assert.Equal(["move --workspace \"2\""], Parse("--workspace", "2").Does);
        Assert.Equal(["move --workspace \"\\\"\""], Parse("--workspace", "\"").Does);
        Assert.Equal(["manage", "move --workspace \"code\""], Parse("--manage", "--move", "code").Does);
    }

    [Fact]
    public void PrintIsRecognisedUnderBothNames()
    {
        Assert.True(Parse("--ignore", "--print").Print);
        Assert.True(Parse("--ignore", "--dry-run").Print);
    }

    [Fact]
    public void AFileTakesNoWindowAndNoVerbs()
    {
        Assert.Equal("rules.kdl", Parse("--file", "rules.kdl").File);
        Assert.Equal("-", Parse("--file", "-").File);

        Assert.Contains("takes no window and no verbs", Problem("--file", "rules.kdl", "--ignore"), StringComparison.Ordinal);
        Assert.Contains("takes no window and no verbs", Problem("--file", "rules.kdl", "0x2A"), StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleThatDoesNothingIsRefusedBeforeItIsComposed()
    {
        // The loader would drop it and the window manager would refuse it; better to
        // say so before a window has been waited for.
        Assert.Contains("say what the rule should do", Problem("0x2A"), StringComparison.Ordinal);
        Assert.Contains("say what the rule should do", Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public void ContradictionsAndNonsenseAreNamed()
    {
        Assert.Contains("contradict", Problem("--ignore", "--manage"), StringComparison.Ordinal);
        Assert.Contains("unknown option '--flaot'", Problem("--flaot"), StringComparison.Ordinal);
        Assert.Contains("not a window handle", Problem("--ignore", "teams"), StringComparison.Ordinal);
        Assert.Contains("second handle", Problem("--ignore", "1", "2"), StringComparison.Ordinal);
        Assert.Contains("needs a workspace name", Problem("--workspace"), StringComparison.Ordinal);
        Assert.Contains("needs a path", Problem("--file"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRuleListLinesUpItsColumns()
    {
        string text = RuleCommand.Format(
        [
            new RuleInfo("ignore teams", 412, "manage", ["ignore"], null),
            new RuleInfo("pip", 7, "title-change", ["float", "sticky"], "docked"),
        ]);

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("line  name          does", lines[0]);
        Assert.Equal(" 412  ignore teams  ignore", lines[1]);
        Assert.Equal("   7  pip           float, sticky  (on title-change)  [context docked]", lines[2]);
    }

    [Fact]
    public void AnEmptyRuleListSaysSo()
    {
        Assert.Equal("(no rules configured)\n", RuleCommand.Format([]));
    }

    [Fact]
    public void AnAdditionIsDescribedWithItsOutcome()
    {
        var change = new RuleChange(@"C:\me\shubbak.kdl", 412, ["ignore teams"], "rule \"ignore teams\" { }", Reloaded: true, "\"Teams\" was released.");

        Assert.Equal(
            "Added \"ignore teams\" at line 412 of C:\\me\\shubbak.kdl and reloaded.\n\"Teams\" was released.\n",
            RuleCommand.Describe(change, added: true));
    }

    [Fact]
    public void ARemovalPrintsTheRuleSoItCanBePutBack()
    {
        var change = new RuleChange(@"C:\me\shubbak.kdl", 412, ["ignore teams"], "rule \"ignore teams\" {\n    do { ignore }\n}", Reloaded: true, null);

        string text = RuleCommand.Describe(change, added: false);

        Assert.StartsWith("Removed \"ignore teams\" from line 412 of C:\\me\\shubbak.kdl and reloaded.\n", text, StringComparison.Ordinal);
        Assert.Contains("To put it back:\n\n    rule \"ignore teams\" {\n        do { ignore }\n    }\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedReloadIsSaidPlainly()
    {
        var change = new RuleChange(@"C:\me\shubbak.kdl", 412, ["x"], "rule \"x\" { }", Reloaded: false, null);

        Assert.Contains("The reload was refused", RuleCommand.Describe(change, added: true), StringComparison.Ordinal);
    }
}
