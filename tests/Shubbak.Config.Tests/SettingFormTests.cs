namespace Shubbak.Config.Tests;

/// <summary>
/// Reading a setting written either way round.
/// </summary>
/// <remarks>
/// KDL allows a value to be a child node or a property, and a real config uses both
/// constantly - <c>border #true</c> beside <c>monitor=0</c>. Which form a given
/// setting wanted was not discoverable, and the wrong guess was ignored in silence
/// rather than rejected.
/// </remarks>
public sealed class SettingFormTests
{
    [Fact]
    public void PassThroughIsReadAsAProperty()
    {
        // The one that mattered. Written as a property it did nothing, so a mode meant
        // to leave the keyboard usable swallowed every key instead - and the config
        // said plainly that it should not.
        ConfigLoadResult result = ConfigLoader.Load("""
            binding-modes {
                mode "resize" pass-through=#true {
                    bind "h" { resize --width -2% }
                }
            }
            """);

        Assert.False(result.HasErrors);

        BindingMode mode = Assert.Single(result.Config.BindingModes);
        Assert.True(mode.PassThrough);
    }

    [Fact]
    public void PassThroughIsStillReadAsAChildNode()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            binding-modes {
                mode "resize" {
                    pass-through #true
                    bind "h" { resize --width -2% }
                }
            }
            """);

        Assert.True(Assert.Single(result.Config.BindingModes).PassThrough);
    }

    [Fact]
    public void ASwallowingModeWrittenAsAPropertyIsNotFlaggedAsATrap()
    {
        // Because pass-through was ignored, such a mode was reported as having no way
        // out - an error about the wrong thing entirely.
        ConfigLoadResult result = ConfigLoader.Load("""
            binding-modes {
                mode "resize" pass-through=#true { }
            }
            """);

        Assert.DoesNotContain(result.Errors, d => d.Code == "SHB0425");
    }

    [Theory]
    [InlineData("general toggle-workspace-on-refocus=#true { }")]
    [InlineData("general { toggle-workspace-on-refocus #true }")]
    public void ABooleanIsReadEitherWay(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(result.HasErrors);
        Assert.True(result.Config.ToggleWorkspaceOnRefocus);
    }

    [Theory]
    [InlineData("gaps inner=8 { }")]
    [InlineData("gaps { inner 8 }")]
    public void ANumberIsReadEitherWay(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(result.HasErrors);
        Assert.Equal(8, result.Config.InnerGap);
    }

    [Theory]
    [InlineData("general default-layout=\"grid\" { }")]
    [InlineData("general { default-layout \"grid\" }")]
    public void TextIsReadEitherWay(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(result.HasErrors);
        Assert.Equal("grid", result.Config.DefaultLayout);
    }
}
