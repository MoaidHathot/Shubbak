using Shubbak.Core.Commands;

namespace Shubbak.Config.Tests;

/// <summary>
/// <c>move --workspace N --focus</c>: one command for one intention.
/// </summary>
/// <remarks>
/// <para>
/// "Send it there" and "send it there and go with it" used to be one command and two
/// commands on the same key. That reads well and is wrong, because the second half -
/// a plain <c>focus --workspace N</c> - is indistinguishable from the bare workspace
/// switch bound to the same key without shift. Press it for the workspace the window
/// is already on and the move does nothing, leaving <c>toggle-workspace-on-refocus</c>
/// to answer an apparent re-focus by bouncing: the window stayed put and the screen
/// jumped to the previous workspace.
/// </para>
/// <para>
/// Nothing downstream could tell the two presses apart, because the daemon runs each
/// command singly and no layer ever sees the pair. So the intention is said here
/// instead, which is what i3, GlazeWM and komorebi all do - none of them binds
/// <c>mod+shift+N</c> to a sequence.
/// </para>
/// </remarks>
public sealed class MoveFocusFlagTests
{
    private static WmCommand Parse(string text)
    {
        Assert.True(
            CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error),
            error?.Message);

        return command!;
    }

    private static Diagnostic Refuse(string text)
    {
        Assert.False(
            CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error),
            $"'{text}' parsed to {command?.GetType().Name} instead of being refused");

        return Assert.IsType<Diagnostic>(error);
    }

    [Fact]
    public void MovingWithoutTheFlagLeavesFocusBehind()
    {
        MoveToWorkspaceCommand move = Assert.IsType<MoveToWorkspaceCommand>(
            Parse("move --workspace 3"));

        Assert.Equal("3", move.Workspace);
        Assert.False(move.Focus);
    }

    [Theory]
    [InlineData("move --workspace 3 --focus")]
    [InlineData("move --focus --workspace 3")]
    public void TheFlagIsReadWhicheverSideOfTheWorkspaceItIsWritten(string text)
    {
        MoveToWorkspaceCommand move = Assert.IsType<MoveToWorkspaceCommand>(Parse(text));

        Assert.Equal("3", move.Workspace);
        Assert.True(move.Focus);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("\\")]
    [InlineData("'")]
    [InlineData(";")]
    public void APunctuationWorkspaceStillTakesTheFlag(string name)
    {
        // The author's workspaces are named after punctuation, and one of them is the
        // command separator. A flag after the name must not be mistaken for one.
        MoveToWorkspaceCommand move = Assert.IsType<MoveToWorkspaceCommand>(
            Parse($"move --workspace \"{name}\" --focus"));

        Assert.Equal(name, move.Workspace);
        Assert.True(move.Focus);
    }

    [Fact]
    public void ADirectionalMoveRefusesTheFlag()
    {
        // Not accepted and ignored. A directional move already carries focus with the
        // window when it crosses to another monitor, and within a workspace focus never
        // leaves it, so --focus has nothing to add - and a flag that is read, validated
        // and does nothing is worse than one that is refused.
        Diagnostic error = Refuse("move --direction right --focus");

        Assert.Equal("SHB0314", error.Code);
        Assert.Contains("--focus", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectionalMoveIsStillFineWithoutIt()
    {
        Assert.IsType<MoveDirectionCommand>(Parse("move --direction right"));
    }
}
