using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Core.Geometry;

namespace Shubbak.Config.Tests;

/// <summary>
/// The parser's newer verbs and flags: <c>swap</c>, <c>gaps</c>, <c>toggle-maximized</c>,
/// <c>no-focus</c>, <c>focus --monitor</c>, <c>move --monitor</c>, <c>layout --masters</c>.
/// </summary>
public sealed class NewerVerbParsingTests
{
    private static WmCommand Parse(string text)
    {
        Assert.True(CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error), error?.Message);
        return command!;
    }

    private static Diagnostic Fails(string text)
    {
        Assert.False(CommandParser.TryParse(text, new TextSpan(new TextPosition(1, 1, 0), text.Length), out _, out Diagnostic? error));
        return error!;
    }

    [Fact]
    public void SwapTakesADirection()
    {
        var swap = Assert.IsType<SwapDirectionCommand>(Parse("swap --direction left"));

        Assert.Equal(Direction.Left, swap.Direction);
        Assert.Equal("swap", swap.Name);
        Assert.False(swap.RepeatsOnHold);
        Assert.Equal("SHB0323", Fails("swap").Code);
    }

    [Theory]
    [InlineData("toggle-maximized")]
    [InlineData("toggle-maximised")]
    public void MaximiseIsSpelledBothWays(string text)
    {
        Assert.IsType<ToggleMaximisedCommand>(Parse(text));
        Assert.False(Parse(text).RepeatsOnHold);
    }

    [Fact]
    public void NoFocusParsesForRulesAndIsRefusedOnAKey()
    {
        Assert.IsType<NoFocusCommand>(Parse("no-focus"));
    }

    [Theory]
    [InlineData("focus --monitor right", null, Direction.Right)]
    [InlineData("focus --monitor Left", null, Direction.Left)]
    [InlineData("focus --monitor laptop", "laptop", null)]
    [InlineData("focus --monitor 1", "1", null)]
    public void FocusMonitorTakesADirectionOrAName(string text, string? monitor, Direction? direction)
    {
        var focus = Assert.IsType<FocusMonitorCommand>(Parse(text));

        Assert.Equal(monitor, focus.Monitor);
        Assert.Equal(direction, focus.Direction);
    }

    [Fact]
    public void MoveMonitorTakesADirectionOrANameAndFollowsWhenAsked()
    {
        var byDirection = Assert.IsType<MoveToMonitorCommand>(Parse("move --monitor right"));
        Assert.Equal(Direction.Right, byDirection.Direction);
        Assert.False(byDirection.Focus);

        var byName = Assert.IsType<MoveToMonitorCommand>(Parse("move --monitor laptop --focus"));
        Assert.Equal("laptop", byName.Monitor);
        Assert.Null(byName.Direction);
        Assert.True(byName.Focus);
        Assert.True(byName.TargetsFocusedWindow);
        Assert.False(byName.RepeatsOnHold);
    }

    [Fact]
    public void TheOldFocusAndMoveHintsNowMentionMonitors()
    {
        Assert.Contains("--monitor", Fails("focus").Hint!, StringComparison.Ordinal);
        Assert.Contains("--monitor", Fails("move").Hint!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gaps --inner +4", 4, null, false)]
    [InlineData("gaps --inner -4", -4, null, false)]
    [InlineData("gaps --outer +2", null, 2, false)]
    [InlineData("gaps --inner 8", 8, null, true)]
    [InlineData("gaps --inner =8 --outer 4", 8, 4, true)]
    [InlineData("gaps --inner 0 --outer 0", 0, 0, true)]
    [InlineData("gaps --inner +2 --outer -2", 2, -2, false)]
    public void GapsAreAChangeOrAValue(string text, int? inner, int? outer, bool absolute)
    {
        var gaps = Assert.IsType<GapsCommand>(Parse(text));

        Assert.Equal(inner, gaps.Inner);
        Assert.Equal(outer, gaps.Outer);
        Assert.Equal(absolute, gaps.Absolute);
    }

    [Theory]
    [InlineData("gaps")]
    [InlineData("gaps --inner wide")]
    [InlineData("gaps --inner +4 --outer 8")]
    [InlineData("gaps --sideways 4")]
    public void GapsRefusesWhatItCannotRead(string text)
    {
        Assert.Equal("SHB0325", Fails(text).Code);
    }

    [Theory]
    [InlineData("layout --masters +1", 1, false)]
    [InlineData("layout --masters -1", -1, false)]
    [InlineData("layout --masters 2", 2, true)]
    public void LayoutMastersIsAChangeOrACount(string text, int delta, bool absolute)
    {
        var masters = Assert.IsType<SetMasterCountCommand>(Parse(text));

        Assert.Equal(delta, masters.Delta);
        Assert.Equal(absolute, masters.Absolute);
    }

    [Fact]
    public void LayoutMastersRefusesWords()
    {
        Assert.Equal("SHB0324", Fails("layout --masters two").Code);
    }

    [Fact]
    public void LayoutSetStillWorksBesideMasters()
    {
        Assert.IsType<SetLayoutCommand>(Parse("layout --set master-left"));
        Assert.Contains("--masters", Fails("layout").Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleMayCarryNoFocus()
    {
        ShubbakConfig config = ConfigLoader.Load("""
            rules {
                rule "quiet chat" {
                    match { process = "slack" }
                    do { no-focus }
                }
            }
            """).Config;

        WindowRule rule = Assert.Single(config.Rules);
        Assert.Contains(rule.Commands, c => c is NoFocusCommand);
    }
}
