namespace Shubbak.Config.Tests;

/// <summary>
/// Whether a window joining the layout is kept out of sight until it has been seen in
/// its tile.
/// </summary>
/// <remarks>
/// An application shows its window where it chooses and Shubbak is told only once it
/// is on screen, so the move into the tile lands a moment after the window appears -
/// 86 to 150 ms for a File Explorer window still building its view, measured. The
/// setting cloaks the window for that moment. Off by default, because it leans on the
/// shell's cloak and is a new behaviour; the timeout bounds how long a window that
/// never applies its rectangle stays hidden.
/// </remarks>
public sealed class ConcealUntilPlacedTests
{
    [Fact]
    public void OffByDefaultWithAThreeHundredMillisecondTimeout()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { }");

        Assert.False(result.Config.ConcealUntilPlaced);
        Assert.Equal(300, result.Config.ConcealUntilPlacedTimeoutMs);
    }

    [Fact]
    public void CanBeTurnedOn()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { conceal-until-placed #true }");

        Assert.False(result.HasErrors);
        Assert.True(result.Config.ConcealUntilPlaced);
        Assert.Equal(300, result.Config.ConcealUntilPlacedTimeoutMs);
    }

    [Fact]
    public void TheTimeoutIsAPropertyOfTheSetting()
    {
        // `conceal-until-placed #true timeout=250`, the way `window-open duration=120`
        // qualifies its node, rather than a second setting that means nothing alone.
        ConfigLoadResult result = ConfigLoader.Load("general { conceal-until-placed #true timeout=250 }");

        Assert.False(result.HasErrors);
        Assert.True(result.Config.ConcealUntilPlaced);
        Assert.Equal(250, result.Config.ConcealUntilPlacedTimeoutMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void ATimeoutThatWouldNeverShowTheWindowIsRefused(int ms)
    {
        ConfigLoadResult result = ConfigLoader.Load($"general {{ conceal-until-placed #true timeout={ms} }}");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0456");

        // The switch still takes effect with the default timeout: a bad number is not
        // a reason to leave the user with a setting they did not write.
        Assert.True(result.Config.ConcealUntilPlaced);
        Assert.Equal(300, result.Config.ConcealUntilPlacedTimeoutMs);
    }

    [Fact]
    public void ATimeoutThatIsNotANumberIsReported()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { conceal-until-placed #true timeout=\"soon\" }");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0420");
        Assert.Equal(300, result.Config.ConcealUntilPlacedTimeoutMs);
    }

    [Fact]
    public void ItIsAKnownSetting()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { conceal-until-placed #true }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SHB0428");
    }
}
