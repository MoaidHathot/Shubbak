using Shubbak.Core.Wm;

namespace Shubbak.Config.Tests;

/// <summary>
/// Tests for <c>hide-method</c>.
/// </summary>
/// <remarks>
/// Worth its own file because the setting decides whether a concealed window can ever
/// come back. Getting it wrong strands windows off screen with their processes still
/// running - the failure that prompted the whole concealment rewrite.
/// </remarks>
public sealed class HideMethodConfigTests
{
    private static ShubbakConfig LoadOk(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(
            result.HasErrors,
            "Unexpected errors:\n" + string.Join("\n", result.Errors.Select(d => d.ToString())));

        return result.Config;
    }

    [Fact]
    public void DefaultsToCloaking()
    {
        // The default matters more than most: it is the only method that survives
        // Shubbak being killed.
        Assert.Equal(WindowHideMethod.Cloak, LoadOk("general { }").HideMethod);
    }

    [Theory]
    [InlineData("cloak", WindowHideMethod.Cloak)]
    [InlineData("minimize", WindowHideMethod.Minimise)]
    [InlineData("minimise", WindowHideMethod.Minimise)]
    [InlineData("hide", WindowHideMethod.Hide)]
    public void ReadsEachMethod(string text, WindowHideMethod expected)
    {
        ShubbakConfig config = LoadOk($$"""
            general {
                hide-method "{{text}}"
            }
            """);

        Assert.Equal(expected, config.HideMethod);
    }

    [Fact]
    public void BothSpellingsOfMinimiseAreAccepted()
    {
        // The codebase is written in British English but the config is not the place
        // to be precious about it.
        Assert.Equal(
            LoadOk("general { hide-method \"minimise\" }").HideMethod,
            LoadOk("general { hide-method \"minimize\" }").HideMethod);
    }

    [Fact]
    public void RejectsAnUnknownMethodAndSaysWhatIsValid()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            general {
                hide-method "banish"
            }
            """);

        Assert.True(result.HasErrors);

        Diagnostic error = Assert.Single(result.Errors);
        Assert.Equal("SHB0423", error.Code);

        // The hint has to name the alternatives, or the error only tells the user
        // that they are wrong and not what to do about it.
        Assert.Contains("cloak", error.Hint, StringComparison.Ordinal);
        Assert.Contains("minimize", error.Hint, StringComparison.Ordinal);
        Assert.Contains("hide", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownMethodKeepsTheSafeDefault()
    {
        // Falling back to hiding on a typo would strand windows. It has to stay on
        // the recoverable method.
        ConfigLoadResult result = ConfigLoader.Load("""
            general {
                hide-method "banish"
            }
            """);

        Assert.Equal(WindowHideMethod.Cloak, result.Config.HideMethod);
    }

    [Fact]
    public void ConcealedWindowsKeepTheirTaskbarButtonByDefault()
    {
        // The taskbar stays a complete list of what is open, so a window on another
        // workspace is one click away rather than hidden until you remember where you
        // left it.
        Assert.True(LoadOk("general { }").KeepInTaskbar);
    }

    [Fact]
    public void TheTaskbarButtonCanBeTurnedOff()
    {
        ShubbakConfig config = LoadOk("""
            general {
                keep-in-taskbar #false
            }
            """);

        Assert.False(config.KeepInTaskbar);
    }
}
