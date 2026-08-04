namespace Shubbak.Config.Tests;

/// <summary>
/// Whether a window joining the layout for the first time animates into its tile.
/// </summary>
/// <remarks>
/// It was turned off outright while the message loop was delivering half the frames
/// it was supposed to, because the stutter was worst on exactly this case - a window
/// that relays out its contents on every resize does so once per frame. With the loop
/// fixed it is a preference rather than a decision to make for everyone, so it is a
/// setting, and it stays off unless asked for.
/// </remarks>
public sealed class AnimateNewWindowsTests
{
    [Fact]
    public void ItIsOffByDefault()
    {
        ConfigLoadResult result = ConfigLoader.Load("animation { }");

        Assert.False(result.Config.Animation.AnimateNewWindows);
    }

    [Fact]
    public void ItIsOffWhenTheSectionIsAbsentEntirely()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { }");

        Assert.False(result.Config.Animation.AnimateNewWindows);
    }

    [Theory]
    [InlineData("animation { animate-new-windows #true }")]
    [InlineData("animation animate-new-windows=#true { }")]
    public void ItCanBeTurnedOn(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(result.HasErrors);
        Assert.True(result.Config.Animation.AnimateNewWindows);
    }

    [Fact]
    public void TurningItOffExplicitlyIsHonoured()
    {
        ConfigLoadResult result = ConfigLoader.Load("animation { animate-new-windows #false }");

        Assert.False(result.Config.Animation.AnimateNewWindows);
    }

    [Fact]
    public void ItIsIndependentOfAnimationBeingEnabledAtAll()
    {
        // Asking for it while animation is off should not quietly turn animation on.
        ConfigLoadResult result = ConfigLoader.Load("""
            animation {
                enabled #false
                animate-new-windows #true
            }
            """);

        Assert.False(result.Config.Animation.Enabled);
        Assert.True(result.Config.Animation.AnimateNewWindows);
    }

    [Fact]
    public void TheOpenProfileIsSeparatelyTunable()
    {
        // The point of using window-open rather than window-move for it: a shorter
        // open than move is usually what stops it feeling sluggish.
        ConfigLoadResult result = ConfigLoader.Load("""
            animation {
                animate-new-windows #true
                window-open duration=90 curve="ease-out"
                window-move duration=140 curve="ease-out"
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Equal(90, result.Config.Animation.WindowOpen.Duration.TotalMilliseconds);
        Assert.Equal(140, result.Config.Animation.WindowMove.Duration.TotalMilliseconds);
    }
}
