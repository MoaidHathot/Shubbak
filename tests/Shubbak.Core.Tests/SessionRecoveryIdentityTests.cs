using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

public sealed class SessionRecoveryIdentityTests
{
    private static readonly WindowIdentity Chat = new()
    {
        ProcessName = "ms-teams", ClassName = "TeamsWebView", Title = "Microsoft Teams", ProcessId = 123,
    };

    private static Session Remember(long handle = 0x30532, int processId = 123) =>
        new(1, DateTimeOffset.UtcNow,
            [new RememberedWindow(Chat.ProcessName, Chat.ClassName, SessionStore.HashTitle(Chat.Title),
                "chat", [], false, "Tiling", handle, processId)]);

    [Theory]
    [InlineData(0)]
    [InlineData(0x10870)]
    public void NeitherALegacyEntryNorAMatchingTitleClaimsADifferentHiddenWindow(long recordedHandle)
    {
        HashSet<int> claimed = [];
        Assert.Null(SessionStore.Match(Remember(recordedHandle), Chat, 0x30532, claimed, requireHandleMatch: true));
        Assert.Empty(claimed);
    }

    [Fact]
    public void AHelperCannotConsumeTheEntryNeededByTheRealConcealedWindow()
    {
        Session session = Remember();
        HashSet<int> claimed = [];
        Assert.Null(SessionStore.Match(session, Chat, 0x10870, claimed, requireHandleMatch: true));

        RememberedWindow? real = SessionStore.Match(session,
            Chat with { Title = "A different conversation" }, 0x30532, claimed, requireHandleMatch: true);
        Assert.NotNull(real);
        Assert.Equal("chat", real.Workspace);
        Assert.Null(SessionStore.Match(session, Chat, 0x30532, claimed, requireHandleMatch: true));
    }

    [Fact]
    public void ARecycledHandleFromAnotherProcessInstanceIsNotRecoveryEvidence()
    {
        Assert.Null(SessionStore.Match(Remember(), Chat with { ProcessId = 456 }, 0x30532, [], requireHandleMatch: true));
    }

    [Fact]
    public void AVisibleRestartedApplicationStillUsesItsPlacementHint()
    {
        RememberedWindow? matched = SessionStore.Match(Remember(), Chat with { ProcessId = 456 }, 0x7777, []);
        Assert.NotNull(matched);
        Assert.Equal("chat", matched.Workspace);
    }

    [Fact]
    public void AnOlderSessionWithAHandleButNoProcessIdStillRecoversThatWindow()
    {
        Assert.NotNull(SessionStore.Match(Remember(processId: 0), Chat, 0x30532, [], requireHandleMatch: true));
    }

    [Fact]
    public void CaptureAndJsonRoundTripKeepTheOwningProcessId()
    {
        WindowManager wm = WmFixture.Create();
        wm.ManageWindow(new WindowNode(0x30532, Chat));
        Session captured = SessionStore.Capture(wm.Root);
        string json = System.Text.Json.JsonSerializer.Serialize(captured, SessionJsonContext.Default.Session);
        Session loaded = System.Text.Json.JsonSerializer.Deserialize(json, SessionJsonContext.Default.Session)!;
        Assert.Equal(Chat.ProcessId, Assert.Single(loaded.Windows).ProcessId);
    }
}
