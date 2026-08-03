using Shubbak.Core.Commands;

namespace Shubbak.Config.Tests;

/// <summary>
/// Rules that overturn the built-in window filter, and the commands that state a
/// window's state rather than flipping it.
/// </summary>
/// <remarks>
/// The filter's exclusions are heuristics. Some of them are wrong for some
/// applications, and until now there was no way to say so from configuration - the
/// only remedy was editing the source, which is not a remedy.
/// </remarks>
public sealed class ManageRuleTests
{
    private static ShubbakConfig LoadOk(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(
            result.HasErrors,
            "Unexpected errors:\n" + string.Join("\n", result.Errors.Select(d => d.ToString())));

        return result.Config;
    }

    [Fact]
    public void ARuleCanAskForAWindowTheFilterWouldHavePassedOver()
    {
        ShubbakConfig config = LoadOk("""
            rules {
                rule "whatsapp" {
                    match { process = "WhatsApp" }
                    do { manage }
                }
            }
            """);

        WindowRule rule = Assert.Single(config.Rules);

        Assert.Contains(rule.Commands, c => c is ManageCommand);
    }

    [Fact]
    public void ARuleCanStillExcludeAWindow()
    {
        ShubbakConfig config = LoadOk("""
            rules {
                rule "ditto" {
                    match { process = "Ditto" }
                    do { ignore }
                }
            }
            """);

        WindowRule rule = Assert.Single(config.Rules);

        Assert.Contains(rule.Commands, c => c is IgnoreCommand);
    }

    [Theory]
    [InlineData("float", typeof(FloatCommand))]
    [InlineData("tile", typeof(TileCommand))]
    public void StateCanBeStatedRatherThanToggled(string verb, Type expected)
    {
        // toggle-floating is wrong in a rule: it says "change", and a rule wants to
        // say "is". A dialog the built-in rule has already floated would be toggled
        // straight back into the tiling flow.
        ShubbakConfig config = LoadOk($$"""
            rules {
                rule "r" {
                    match { class = "SomeClass" }
                    do { {{verb}} }
                }
            }
            """);

        WindowRule rule = Assert.Single(config.Rules);

        Assert.IsType(expected, Assert.Single(rule.Commands));
    }

    [Fact]
    public void ManageAndIgnoreCanBeMatchedOnAnyAttribute()
    {
        // The four things a window can be identified by. A window with no title is
        // one of the cases that needs overriding, so matching cannot depend on one.
        ShubbakConfig config = LoadOk("""
            rules {
                rule "by class" {
                    match { class = "TurboPhraseWindow" }
                    do { ignore }
                }
                rule "by path" {
                    match { path $= "WhatsApp.exe" }
                    do { manage }
                }
                rule "by title" {
                    match { title *= "Call" }
                    do { manage }
                }
            }
            """);

        Assert.Equal(3, config.Rules.Count);
    }

    [Fact]
    public void ManageIsRejectedWhenBoundToAKey()
    {
        // It is consumed by the rule engine before execution, so reaching the
        // executor means it was written in a keybinding by mistake. Saying so beats
        // doing nothing.
        var executor = new CommandExecutor(new Core.Wm.WindowManager());

        CommandOutcome outcome = executor.Execute(new ManageCommand());

        Assert.False(outcome.Result.Succeeded);
    }

    [Fact]
    public void ToggleManagedIsAHostAction()
    {
        // It deals in window handles, and an unmanaged window has no node to name it
        // by, so the state machine cannot carry it.
        var executor = new CommandExecutor(new Core.Wm.WindowManager());

        CommandOutcome outcome = executor.Execute(new ToggleManagedCommand());

        Assert.Equal(HostAction.ToggleManaged, outcome.Action);
    }

    [Theory]
    [InlineData("manage")]
    [InlineData("toggle-managed")]
    [InlineData("float")]
    [InlineData("tile")]
    public void TheNewVerbsParse(string verb)
    {
        Assert.True(
            CommandParser.TryParse(verb, default, out WmCommand? command, out Diagnostic? error),
            error?.Message);

        Assert.NotNull(command);
    }
}
