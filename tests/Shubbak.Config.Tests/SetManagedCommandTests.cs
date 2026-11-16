using Shubbak.Core.Commands;
using Shubbak.Core.Wm;

namespace Shubbak.Config.Tests;

public sealed class SetManagedCommandTests
{
    [Theory]
    [InlineData("managed", WindowManagementMode.Managed)]
    [InlineData("unmanaged", WindowManagementMode.Unmanaged)]
    [InlineData("default", WindowManagementMode.Default)]
    public void ChoicesAndExplicitTargetsReachTheHostWithoutDependingOnManagedFocus(string mode, WindowManagementMode expected)
    {
        foreach (string suffix in new[] { "", " --handle 1773010", " --handle 0x1B0DD2" })
        {
            Assert.True(CommandParser.TryParse($"set-managed {mode}{suffix}", default, out WmCommand? command, out Diagnostic? error), error?.Message);
            SetManagedCommand parsed = Assert.IsType<SetManagedCommand>(command);
            Assert.Equal(expected, parsed.Mode);
            Assert.Equal(suffix.Length == 0 ? null : (long?)1773010, parsed.Handle);
            Assert.False(parsed.TargetsFocusedWindow);
            Assert.False(parsed.RepeatsOnHold);

            CommandOutcome outcome = new CommandExecutor(new WindowManager()).Execute(parsed);
            Assert.True(outcome.Succeeded);
            Assert.Equal(HostAction.SetManaged, outcome.Action);
            Assert.Equal(parsed, CommandExecutor.DecodeManagement(outcome.Payload!));
        }
    }

    [Theory]
    [InlineData("set-managed")]
    [InlineData("set-managed maybe")]
    [InlineData("set-managed 1")]
    [InlineData("set-managed managed --handle")]
    [InlineData("set-managed managed --handle nope")]
    [InlineData("set-managed unmanaged --handle 0")]
    [InlineData("set-managed default --handle -1")]
    [InlineData("set-managed default --handle 0xFFFFFFFFFFFFFFFF")]
    [InlineData("set-managed managed --handle 42 --handle 43")]
    [InlineData("set-managed managed unmanaged")]
    [InlineData("set-managed managed --other 42")]
    public void MalformedTargetsCannotFallBackToTheForeground(string text)
    {
        Assert.False(CommandParser.TryParse(text, default, out WmCommand? command, out Diagnostic? error));
        Assert.Null(command);
        Assert.Equal("SHB0326", error?.Code);
        Assert.Contains("--handle", error!.Hint, StringComparison.Ordinal);
    }
}
