using Shubbak.Core.Layouts;

namespace Shubbak.Config.Tests;

/// <summary>
/// Reading and validating <c>default-layout</c>.
/// </summary>
/// <remarks>
/// The key was parsed into the configuration record and then read by nothing, so
/// every workspace was a horizontal split regardless of what the file said. A setting
/// that is accepted, validated without complaint, and then ignored is worse than one
/// that is rejected.
/// </remarks>
public sealed class DefaultLayoutConfigTests
{
    [Theory]
    [InlineData("grid")]
    [InlineData("monocle")]
    [InlineData("fibonacci")]
    [InlineData("splitv")]
    public void AKnownLayoutIsAcceptedAndReachesTheWindowManager(string name)
    {
        ConfigLoadResult result = ConfigLoader.Load($$"""
            general {
                default-layout "{{name}}"
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Equal(name, result.Config.DefaultLayout);

        // The half that was missing: the option has to arrive where it is used.
        Core.Wm.WmOptions options = result.Config.ToWmOptions();

        Assert.NotNull(options.DefaultLayout);
        Assert.Equal(name, options.DefaultLayout!.Name);
    }

    [Fact]
    public void AnUnknownLayoutIsReported()
    {
        // A typo here used to be silent, and looked exactly like the feature not
        // working - which, at the time, it also was not.
        ConfigLoadResult result = ConfigLoader.Load("""
            general {
                default-layout "fibonaci"
            }
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, d => d.Code == "SHB0113");
    }

    [Fact]
    public void TheHintListsTheLayoutsThatExist()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            general {
                default-layout "nonsense"
            }
            """);

        Diagnostic error = Assert.Single(result.Errors, d => d.Code == "SHB0113");

        foreach (string name in LayoutRegistry.CanonicalNames)
            Assert.Contains(name, error.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void SayingNothingLeavesTheRegistryDefault()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { }");

        Assert.False(result.HasErrors);
        Assert.Null(result.Config.ToWmOptions().DefaultLayout);
    }
}
