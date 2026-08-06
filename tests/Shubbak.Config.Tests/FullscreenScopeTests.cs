using Shubbak.Core.Commands;

namespace Shubbak.Config.Tests;

/// <summary>
/// Fullscreen that covers the bar, and fullscreen that does not.
/// </summary>
/// <remarks>
/// <para>
/// Shubbak's fullscreen fills the work area, which is the monitor minus whatever the
/// bar and taskbar have reserved. That is the right default - a fullscreen video with
/// the clock still visible is usually what was wanted - but it is not what every
/// other tiling window manager means by the word, and there is no way to ask for the
/// other reading.
/// </para>
/// <para>
/// A flag rather than a second verb, because the two differ only in which rectangle
/// they use and naming them separately would suggest they were less related than they
/// are.
/// </para>
/// </remarks>
public sealed class FullscreenScopeTests
{
    private static ToggleFullscreenCommand Parse(string command)
    {
        ConfigLoadResult result = ConfigLoader.Load($$"""
            keybindings {
                bind "alt+x" { {{command}} }
            }
            """);

        Assert.DoesNotContain(
            result.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error);

        return Assert.IsType<ToggleFullscreenCommand>(
            Assert.Single(Assert.Single(result.Config.Keybindings).Commands));
    }

    [Fact]
    public void WithoutTheFlagFullscreenLeavesTheBarAlone()
    {
        Assert.False(Parse("toggle-fullscreen").WholeMonitor);
    }

    [Theory]
    [InlineData("toggle-fullscreen --monitor")]
    [InlineData("toggle-fullscreen --whole-monitor")]
    public void TheFlagAsksForTheWholeMonitor(string command)
    {
        // Both spellings, because "--monitor" is short enough to be ambiguous with
        // "which monitor" and someone will reach for the longer one.
        Assert.True(Parse(command).WholeMonitor);
    }

    [Fact]
    public void ItStillDoesNotRepeatWhenHeld()
    {
        // A toggle that repeats flips at the hardware repeat rate, which for this one
        // means a window flickering between two sizes.
        Assert.False(new ToggleFullscreenCommand(WholeMonitor: true).RepeatsOnHold);
        Assert.False(new ToggleFullscreenCommand().RepeatsOnHold);
    }

    [Fact]
    public void TheTwoAreNotTheSameCommand()
    {
        // Records compare by value, so this is what stops a binding table keyed on
        // the command from treating alt+x and alt+shift+x as duplicates.
        Assert.NotEqual(
            new ToggleFullscreenCommand(),
            new ToggleFullscreenCommand(WholeMonitor: true));
    }
}
