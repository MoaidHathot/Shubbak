using Shubbak.Ipc;

namespace Shubbak.Wm.Tests;

/// <summary>
/// The shutdown notice, which both ends of the pipe read the same way or not at all.
/// </summary>
/// <remarks>
/// The palette and the watcher decide from this one payload whether to stay for the
/// window manager's return or to leave with it. Both mistakes are visible: a palette
/// that left when asked to stay is a desktop with nothing to say why, and one that
/// stayed when asked to leave is the stray process the tray's Exit used to leave
/// behind.
/// </remarks>
public sealed class ShutdownNoticeTests
{
    [Fact]
    public void APlainExitLeavesEveryoneWhereTheyAre()
    {
        Assert.False(ShutdownNotice.IsForEveryone(ShutdownNotice.Payload(everything: false)));
    }

    [Fact]
    public void ExitAllAsksEveryoneToGo()
    {
        Assert.True(ShutdownNotice.IsForEveryone(ShutdownNotice.Payload(everything: true)));
    }

    [Fact]
    public void ThePlainPayloadIsWhatEveryEarlierWindowManagerSent()
    {
        // Every release before this one published {} and nothing else, and a client
        // from this release against an older window manager must read that as it
        // always did. The constant is the contract; this is what holds it still.
        Assert.Equal("{}", ShutdownNotice.Payload(everything: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("{\"everything\":false}")]
    [InlineData("{\"everything\":\"true\"}")]
    [InlineData("{\"everything\":1}")]
    [InlineData("{\"something\":true}")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("not json")]
    [InlineData("{\"everything\":")]
    public void AnythingLessThanAPlainYesMeansStay(string? payload)
    {
        // Staying is the safe misreading, so nothing short of the exact form counts:
        // not a string that says true, not a number, not a payload that failed to
        // parse. A future window manager that adds a field beside it is still read.
        Assert.False(ShutdownNotice.IsForEveryone(payload));
    }

    [Fact]
    public void AFieldAddedBesideItIsNotInTheWay()
    {
        Assert.True(ShutdownNotice.IsForEveryone("{\"reason\":\"tray\",\"everything\":true}"));
    }
}
