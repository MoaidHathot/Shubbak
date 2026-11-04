namespace Shubbak.Config.Tests;

/// <summary>
/// Binding modes that cannot be left.
/// </summary>
/// <remarks>
/// A mode that swallows every key and binds nothing that returns to the default set
/// is a trap: once entered, no keystroke can undo it, on a machine whose window
/// manager is the thing that has stopped listening. Caught at load, where it is a
/// typo being pointed at rather than a keyboard that has stopped working.
/// </remarks>
public sealed class BindingModeEscapeTests
{
    [Theory]
    [InlineData("bind \"escape\" { signal \"stays\"; }; bind \"esc\" { wm-disable-binding-mode; };")]
    [InlineData("bind \"escape\" { wm-enable-binding-mode --name trap; };")]
    [InlineData("bind \"escape\" { wm-disable-binding-mode; wm-enable-binding-mode --name trap; };")]
    [InlineData("bind \"escape\" { wm-enable-binding-mode --name missing; };")]
    public void AnUnusableExitDoesNotMakeASwallowingModeSafe(string bindings)
    {
        ConfigLoadResult result = ConfigLoader.Load($$"""
            binding-modes { mode "trap" { {{bindings}} }; }
            """);

        Assert.Contains(result.Errors, d => d.Code == "SHB0425");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AModeCycleNeedsAReachableExit(bool withExit)
    {
        string exit = withExit ? "bind \"enter\" { wm-disable-binding-mode; };" : string.Empty;
        ConfigLoadResult result = ConfigLoader.Load($$"""
            binding-modes {
                mode "one" { bind "escape" { wm-enable-binding-mode --name TWO; }; }
                mode "two" { bind "escape" { wm-enable-binding-mode --name one; }; {{exit}} }
            }
            """);

        Assert.Equal(withExit ? 0 : 2, result.Errors.Count(d => d.Code == "SHB0425"));
    }

    [Fact]
    public void OnlyTheLastDefinitionOfAModeDeterminesItsReachability()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            binding-modes {
                mode "one" { bind "escape" { wm-enable-binding-mode --name two; }; }
                mode "two" pass-through=#true { }
                mode "TWO" { bind "escape" { wm-enable-binding-mode --name one; }; }
            }
            """);

        Assert.Equal(2, result.Errors.Count(d => d.Code == "SHB0425"));
    }

    [Fact]
    public void ASwallowingModeWithNoWayOutIsRejected()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            binding-modes {
                mode "pause" {
                    bind "alt+x" { toggle-fullscreen }
                }
            }
            """);

        Assert.Contains(result.Errors, d => d.Code == "SHB0425");
    }

    [Fact]
    public void ASwallowingModeWithAnEmptyBodyIsRejected()
    {
        // The most direct way to write the trap.
        ConfigLoadResult result = ConfigLoader.Load("""
            binding-modes {
                mode "pause" { }
            }
            """);

        Assert.Contains(result.Errors, d => d.Code == "SHB0425");
    }

    [Fact]
    public void ADisableBindingIsAWayOut()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            binding-modes {
                mode "pause" {
                    bind "alt+shift+p" { wm-disable-binding-mode }
                }
            }
            """);

        Assert.DoesNotContain(result.Errors, d => d.Code == "SHB0425");
    }

    [Fact]
    public void SwitchingToAnotherModeCountsAsAWayOut()
    {
        // Not back to the default set, but no longer stuck in this one - and the mode
        // it moves to is checked on its own terms.
        ConfigLoadResult result = ConfigLoader.Load("""
            binding-modes {
                mode "pause" {
                    bind "escape" { wm-enable-binding-mode --name resize }
                }
                mode "resize" pass-through=#true {
                    bind "escape" { wm-disable-binding-mode }
                }
            }
            """);

        Assert.DoesNotContain(result.Errors, d => d.Code == "SHB0425");
    }

    [Fact]
    public void APassThroughModeNeedsNoWayOut()
    {
        // Unbound keys still reach applications, so the keyboard is never inert -
        // the mode may be odd to leave, but it cannot trap anyone.
        ConfigLoadResult result = ConfigLoader.Load("""
            binding-modes {
                mode "resize" pass-through=#true {
                    bind "h" { resize --width -2% }
                }
            }
            """);

        Assert.DoesNotContain(result.Errors, d => d.Code == "SHB0425");
    }

    [Fact]
    public void TheHintSaysBothWaysToFixIt()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            binding-modes {
                mode "pause" { }
            }
            """);

        Diagnostic error = Assert.Single(result.Errors, d => d.Code == "SHB0425");

        Assert.Contains("wm-disable-binding-mode", error.Hint ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("pass-through", error.Hint ?? string.Empty, StringComparison.Ordinal);
    }
}
