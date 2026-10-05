using Shubbak.Ipc;

namespace Shubbak.Wm.Tests;

/// <summary>
/// The reload notice, which both ends of the pipe read the same way or not at all.
/// </summary>
/// <remarks>
/// The palette and the watcher decide from this one payload whether to follow a
/// reload. Both mistakes are visible: following a refused one re-reads a file that
/// does not parse - which for the watcher meant running on defaults and letting go of
/// every context it held - and not following an accepted one leaves them on settings
/// the file no longer contains, with nothing to say so.
/// </remarks>
public sealed class ConfigReloadNoticeTests
{
    [Fact]
    public void ALandedReloadCarriesItsPath()
    {
        ConfigReloadNotice notice = ConfigReloadNotice.Parse(ConfigReloadNotice.Payload(@"C:\x\shubbak.kdl", accepted: true));

        Assert.Equal(@"C:\x\shubbak.kdl", notice.Path);
        Assert.True(notice.Accepted);
    }

    [Fact]
    public void ARefusedReloadSaysSo()
    {
        ConfigReloadNotice notice = ConfigReloadNotice.Parse(ConfigReloadNotice.Payload(@"C:\x\shubbak.kdl", accepted: false));

        Assert.Equal(@"C:\x\shubbak.kdl", notice.Path);
        Assert.False(notice.Accepted);
    }

    [Fact]
    public void RunningOnDefaultsHasNoPathAndNothingToRefuse()
    {
        Assert.Equal("{\"path\":null,\"accepted\":true}", ConfigReloadNotice.Payload(null, accepted: true));
        Assert.Same(ConfigReloadNotice.Empty, ConfigReloadNotice.Parse(ConfigReloadNotice.Payload(null, accepted: true)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("{\"accepted\":true}")]
    [InlineData("{\"accepted\":\"false\"}")]
    [InlineData("{\"accepted\":0}")]
    [InlineData("{\"something\":false}")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("not json")]
    [InlineData("{\"accepted\":")]
    public void AnythingLessThanAPlainNoMeansFollow(string? payload)
    {
        // Every release before this one published {} and every client followed it,
        // so following is what an unreadable or older payload must mean: nothing
        // short of the exact form is a refusal. The path is simply absent.
        ConfigReloadNotice notice = ConfigReloadNotice.Parse(payload);

        Assert.True(notice.Accepted);
        Assert.Null(notice.Path);
    }

    [Fact]
    public void AFieldAddedBesideThemIsNotInTheWay()
    {
        ConfigReloadNotice notice = ConfigReloadNotice.Parse(
            "{\"reason\":\"save\",\"detail\":{\"a\":[1,{\"accepted\":false}]},\"path\":\"x.kdl\",\"accepted\":false}");

        Assert.Equal("x.kdl", notice.Path);
        Assert.False(notice.Accepted);
    }

    [Fact]
    public void APathThatIsNotAStringIsNoPath()
    {
        Assert.Null(ConfigReloadNotice.Parse("{\"path\":42,\"accepted\":false}").Path);
        Assert.Null(ConfigReloadNotice.Parse("{\"path\":{\"a\":1},\"accepted\":false}").Path);
        Assert.False(ConfigReloadNotice.Parse("{\"path\":{\"a\":1},\"accepted\":false}").Accepted);
    }
}
