namespace Shubbak.Config.Tests;

/// <summary>
/// Whether the IPC pipe may be used to start processes.
/// </summary>
/// <remarks>
/// <para>
/// The pipe is scoped to the account and not to the integrity level, so any process
/// running as the user can open the pipe of an elevated daemon. Shubbak tells users
/// to run elevated in order to manage elevated windows, which makes that a realistic
/// path rather than a theoretical one - and <c>shell-exec</c> on the other side
/// starts a process with <c>UseShellExecute</c>.
/// </para>
/// <para>
/// The command exists so a keybinding or a startup command can launch a terminal.
/// That is a decision made deliberately in a config file, and nothing about it
/// requires the same capability to be reachable at runtime by arbitrary local
/// processes.
/// </para>
/// </remarks>
public sealed class ShellExecOverIpcTests
{
    [Fact]
    public void ItIsRefusedByDefault()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { }");

        Assert.False(
            result.Config.AllowShellExecOverIpc,
            "a window manager is not an execution service, so this must be opt-in");
    }

    [Theory]
    [InlineData("general { allow-shell-exec-over-ipc #true }")]
    [InlineData("general allow-shell-exec-over-ipc=#true { }")]
    public void ItCanBeTurnedOnDeliberately(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(result.HasErrors);
        Assert.True(result.Config.AllowShellExecOverIpc);
    }

    [Fact]
    public void TurningItOffIsHonoured()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { allow-shell-exec-over-ipc #false }");

        Assert.False(result.Config.AllowShellExecOverIpc);
    }

    [Fact]
    public void TheCommandItselfStillParses()
    {
        // The gate is on one path, not on the command. Keybindings, rules and startup
        // commands are unaffected - those are config-time decisions the user made.
        Assert.True(CommandParser.TryParse(
            "shell-exec pwsh -NoProfile", default, out Core.Commands.WmCommand? command, out _));

        Assert.IsType<Core.Commands.ShellExecCommand>(command);
    }
}
