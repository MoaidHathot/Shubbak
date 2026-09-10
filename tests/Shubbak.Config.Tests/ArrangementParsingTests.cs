using Shubbak.Core.Commands;

namespace Shubbak.Config.Tests;

/// <summary>What the arrangement command accepts, and what it refuses.</summary>
public sealed class ArrangementParsingTests
{
    private static ArrangementCommand Parse(string text)
    {
        Assert.True(
            CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error),
            error?.Message);

        return Assert.IsType<ArrangementCommand>(command);
    }

    private static Diagnostic Refuse(string text)
    {
        Assert.False(
            CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error),
            $"'{text}' parsed to {command?.GetType().Name} instead of being refused");

        return Assert.IsType<Diagnostic>(error);
    }

    [Theory]
    [InlineData("arrangement --save demo", ArrangementAction.Save, "demo")]
    [InlineData("arrangement --restore demo", ArrangementAction.Restore, "demo")]
    [InlineData("arrangement --delete demo", ArrangementAction.Delete, "demo")]
    [InlineData("arrangement demo --restore", ArrangementAction.Restore, "demo")]
    [InlineData("arrangement --save \"talk, part two\"", ArrangementAction.Save, "talk, part two")]
    public void TheActionAndTheNameAreRead(string text, ArrangementAction action, string name)
    {
        ArrangementCommand command = Parse(text);

        Assert.Equal(action, command.Action);
        Assert.Equal(name, command.Arrangement);
        Assert.Equal("arrangement", command.Name);
    }

    [Fact]
    public void ItDoesNotActOnTheFocusedWindowAndDoesNotRepeatOnHold()
    {
        ArrangementCommand command = Parse("arrangement --restore demo");

        Assert.False(command.TargetsFocusedWindow);
        Assert.False(command.RepeatsOnHold);
    }

    [Theory]
    [InlineData("arrangement demo")]
    [InlineData("arrangement")]
    public void SayingNothingAboutWhatToDoIsRefusedWithTheThreeChoices(string text)
    {
        Diagnostic error = Refuse(text);

        Assert.Equal("SHB0321", error.Code);
        Assert.Contains("--save", error.Hint!, StringComparison.Ordinal);
        Assert.Contains("--restore", error.Hint!, StringComparison.Ordinal);
        Assert.Contains("--delete", error.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void AskingForTwoThingsAtOnceIsRefused()
    {
        Diagnostic error = Refuse("arrangement --save demo --restore demo");

        Assert.Equal("SHB0321", error.Code);
        Assert.Contains("more than one", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("arrangement --save")]
    [InlineData("arrangement --restore --verbose")]
    public void ANamelessArrangementIsRefused(string text)
    {
        Diagnostic error = Refuse(text);

        Assert.Equal("SHB0322", error.Code);
    }
}
