using Shubbak.Core.Commands;
using Shubbak.Core.Wm;

namespace Shubbak.Config.Tests;

/// <summary>
/// Which commands act on the focused window, and what happens when that window is
/// not one Shubbak manages.
/// </summary>
/// <remarks>
/// Focus is frequently on something outside Shubbak's care - a dialog, a tray popup,
/// an application the filter passed over - and nothing updates the focused window for
/// those, so it keeps naming whatever was focused before. Commands that ran anyway
/// acted on that earlier window: the float key untiled a window elsewhere on screen,
/// and the close key would have closed one.
/// </remarks>
public sealed class WindowTargetedCommandTests
{
    [Theory]
    [InlineData("close")]
    [InlineData("toggle-floating")]
    [InlineData("float")]
    [InlineData("tile")]
    [InlineData("toggle-fullscreen")]
    [InlineData("toggle-minimized")]
    [InlineData("resize --width +2%")]
    [InlineData("move --direction left")]
    [InlineData("move --workspace 3")]
    [InlineData("sticky")]
    [InlineData("tag --workspace 3")]
    [InlineData("scratchpad --name notes")]
    public void ACommandThatActsOnAWindowSaysSo(string text)
    {
        // The classification decides whether a command may run against a window the
        // user is not looking at. Getting it wrong for close is the worst case in the
        // whole program.
        Assert.True(
            CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error),
            error?.Message);

        Assert.True(
            command!.TargetsFocusedWindow,
            $"'{text}' acts on the focused window and must declare it");
    }

    [Theory]
    [InlineData("focus --direction left")]
    [InlineData("focus --workspace 3")]
    [InlineData("focus --recent-workspace")]
    [InlineData("focus --next")]
    [InlineData("layout --set grid")]
    [InlineData("layout --cycle")]
    [InlineData("equalise")]
    [InlineData("toggle-tiling-direction")]
    [InlineData("wm-reload-config")]
    [InlineData("wm-redraw")]
    [InlineData("wm-exit")]
    [InlineData("move-workspace --direction left")]

    // Suspending must work from an unmanaged window above all others. Somebody
    // reaching for it is looking at a game, and a game is very often exactly the
    // sort of window Shubbak passed over - so a suspend that refused because the
    // foreground window is unmanaged would refuse precisely when it was wanted.
    [InlineData("wm-suspend")]
    [InlineData("wm-resume")]
    [InlineData("wm-toggle-suspend")]
    [InlineData("wm-toggle-pause")]
    public void ACommandThatDoesNotActOnAWindowSaysThatToo(string text)
    {
        // These stay useful from an unmanaged window. Moving focus out of one is
        // exactly how it is left, and refusing workspace switching would break
        // clicking a workspace on the bar - whose own window is not managed either.
        Assert.True(
            CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error),
            error?.Message);

        Assert.False(
            command!.TargetsFocusedWindow,
            $"'{text}' does not act on the focused window and must not claim to");
    }

    [Fact]
    public void ToggleManagedDoesNotClaimToTargetTheFocusedWindow()
    {
        // It reads the foreground window itself, and acting on a window Shubbak does
        // not manage is the entire purpose of it. Declaring otherwise would have it
        // refused in exactly the situation it exists for.
        Assert.True(CommandParser.TryParse("toggle-managed", default, out WmCommand? command, out _));

        Assert.False(command!.TargetsFocusedWindow);
    }

    [Theory]
    [InlineData("refuse", UnmanagedWindowCommands.Refuse)]
    [InlineData("reject", UnmanagedWindowCommands.Refuse)]
    [InlineData("adopt", UnmanagedWindowCommands.Adopt)]
    [InlineData("manage", UnmanagedWindowCommands.Adopt)]
    public void ThePolicyIsConfigurable(string text, UnmanagedWindowCommands expected)
    {
        ConfigLoadResult result = ConfigLoader.Load($$"""
            general {
                unmanaged-window-commands "{{text}}"
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Equal(expected, result.Config.UnmanagedWindowCommands);
    }

    [Fact]
    public void RefusingIsTheDefault()
    {
        // A command that does nothing and says why is recoverable. One that acts on
        // the wrong window may not be.
        ConfigLoadResult result = ConfigLoader.Load("general { }");

        Assert.Equal(UnmanagedWindowCommands.Refuse, result.Config.UnmanagedWindowCommands);
    }

    [Fact]
    public void AnUnknownPolicyIsReported()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            general {
                unmanaged-window-commands "sometimes"
            }
            """);

        Assert.Contains(result.Errors, d => d.Code == "SHB0424");
    }
}
