using Dalil.Core;
using Shubbak.Ipc;

namespace Dalil.Tests;

public sealed class ManagementCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagementFailureReachesThePaletteInsteadOfOnlyTheLog(bool accepted)
    {
        string pipe = $"shubbak-test-{Guid.NewGuid():N}";
        const string Command = "set-managed managed --handle 1773010";
        const string Refusal = "The selected window has closed or is no longer available.";
        await using var server = new IpcServer { PipeName = pipe };
        server.Start(request =>
        {
            Assert.Equal("command", request.Method);
            Assert.Equal(Command, request.Payload);
            return Task.FromResult(new IpcResponse(request.Id, accepted, null, accepted ? null : Refusal));
        });
        await using var connection = new WmConnection { PipeName = pipe };
        List<string> failures = [];

        await connection.SendAsync(Command, failures.Add).WaitAsync(TimeSpan.FromSeconds(5));

        if (accepted) Assert.Empty(failures);
        else Assert.Equal(Refusal, Assert.Single(failures));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InspectManagementRowsRunDirectlyRatherThanOpeningAnotherRulePicker(bool managed)
    {
        var report = new WindowReport(42, "Recap", "TeamsWebView", "ms-teams", null,
            0, 0, 800, 600, 0, 0, true, "None", false, true, "manageable", "manageable",
            managed, false, null, [], []);
        PaletteEntry[] choices = [.. PaletteEntries.ForReport(report).Where(e => e.Command.StartsWith("set-managed ", StringComparison.Ordinal))];

        Assert.Equal(3, choices.Length);
        Assert.All(choices, entry => Assert.Equal(PaletteChoice.Run,
            PaletteInput.Choose(entry, PaletteMode.Inspect, insideOverlay: true)));
    }
}
