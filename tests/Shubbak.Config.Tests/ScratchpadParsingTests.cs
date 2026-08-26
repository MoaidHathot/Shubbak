using Shubbak.Core.Commands;

namespace Shubbak.Config.Tests;

/// <summary>
/// What the scratchpad command accepts, and what it refuses.
/// </summary>
/// <remarks>
/// <para>
/// The verb used to be incapable of failing. Its case in the parser ended in an
/// unconditional <c>return true</c>: an unrecognised option was skipped, no positional
/// remained, and the slot silently became <c>"default"</c>.
/// </para>
/// <para>
/// That is worse than it sounds, because the scratchpad is invisible by definition. A
/// key bound to <c>scratchpad --hide notes</c> stashed a window into <c>default</c>
/// and summoned it back from <c>default</c>, so it appeared to work - right up until
/// somebody used two slots and found that one of them swallowed the other. Nothing
/// anywhere reported a problem, because as far as the parser was concerned there was
/// not one.
/// </para>
/// </remarks>
public sealed class ScratchpadParsingTests
{
    private static ScratchpadCommand Parse(string text)
    {
        Assert.True(
            CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error),
            error?.Message);

        return Assert.IsType<ScratchpadCommand>(command);
    }

    private static Diagnostic Refuse(string text)
    {
        Assert.False(
            CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error),
            $"'{text}' parsed to {command?.GetType().Name} instead of being refused");

        return Assert.IsType<Diagnostic>(error);
    }

    [Theory]
    [InlineData("scratchpad --name notes", "notes")]
    [InlineData("scratchpad notes", "notes")]
    [InlineData("scratchpad --name \"my notes\"", "my notes")]
    public void ASlotCanBeNamed(string text, string slot)
    {
        Assert.Equal(slot, Parse(text).Slot);
    }

    /// <summary>
    /// The single-scratchpad case needs no argument, which is why the default exists.
    /// </summary>
    [Fact]
    public void TheDefaultSlotNeedsNoArgument()
    {
        Assert.Equal("default", Parse("scratchpad").Slot);
    }

    /// <summary>
    /// <c>--name</c> is the only option, and the toggles people reach for do not exist.
    /// </summary>
    /// <remarks>
    /// <c>--show</c>, <c>--hide</c> and <c>--toggle</c> are the obvious guesses, and
    /// all three are wrong for the same reason: the command is already a toggle. The
    /// message says so rather than only listing what is allowed.
    /// </remarks>
    [Theory]
    [InlineData("scratchpad --hide")]
    [InlineData("scratchpad --show notes")]
    [InlineData("scratchpad --toggle notes")]
    [InlineData("scratchpad --slot notes")]
    public void AnOptionThatDoesNotExistIsRefused(string text)
    {
        Diagnostic error = Refuse(text);

        Assert.Equal("SHB0312", error.Code);
        Assert.Contains("--name", error.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trailing <c>--name</c> named nothing, and quietly meant <c>default</c>.
    /// </summary>
    [Fact]
    public void ANameWithNoValueIsRefused()
    {
        Diagnostic error = Refuse("scratchpad --name");

        Assert.Equal("SHB0313", error.Code);
    }

    /// <summary>
    /// A single dash is a workspace name, not a malformed option.
    /// </summary>
    /// <remarks>
    /// The shipped example config uses <c>-</c> as a workspace, so anything that
    /// treats a lone dash as an option would reject a perfectly good slot name.
    /// </remarks>
    [Fact]
    public void ASingleDashIsASlotName()
    {
        Assert.Equal("-", Parse("scratchpad -").Slot);
    }

    /// <summary>
    /// A slot may be named after an option if that is genuinely what was asked for.
    /// </summary>
    /// <remarks>
    /// Odd, but it is what the user wrote, and the value of a recognised flag is an
    /// argument whatever it looks like. Inventing an error here would be guessing.
    /// </remarks>
    [Fact]
    public void TheValueOfNameIsTakenLiterally()
    {
        Assert.Equal("--hide", Parse("scratchpad --name --hide").Slot);
    }

    /// <summary>
    /// The shipped example must keep parsing, since it is what people copy.
    /// </summary>
    [Theory]
    [InlineData("scratchpad --name notes")]
    [InlineData("scratchpad --name terminal")]
    [InlineData("scratchpad --name chat")]
    public void TheShippedExamplesStillParse(string text)
    {
        Assert.NotNull(Parse(text));
    }
}
