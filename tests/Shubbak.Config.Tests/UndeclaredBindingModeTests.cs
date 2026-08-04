namespace Shubbak.Config.Tests;

/// <summary>
/// Catching a binding mode nobody declared, at load rather than at the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// <c>wm-enable-binding-mode --name typo</c> used to set the state machine's idea of
/// the mode, log it as active and report success, while every keystroke went on
/// resolving against the default bindings. That is refused out loud now - but by
/// then the user has pressed a key and watched nothing happen.
/// </para>
/// <para>
/// This is the moment it can be pointed at instead, with a line and a caret, before
/// it has cost anybody anything.
/// </para>
/// </remarks>
public sealed class UndeclaredBindingModeTests
{
    private static IReadOnlyList<Diagnostic> Diagnose(string source) =>
        ConfigLoader.Load(source).Diagnostics;

    [Fact]
    public void EnteringAModeThatIsNotDeclaredIsReported()
    {
        Diagnostic diagnostic = Assert.Single(
            Diagnose("""
                keybindings {
                    bind "alt+p" { wm-enable-binding-mode --name "pasue" }
                }

                binding-modes {
                    mode "pause" {
                        bind "alt+p" { wm-disable-binding-mode }
                    }
                }
                """),
            d => d.Code == "SHB0434");

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("pasue", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("pause", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredModeIsAccepted()
    {
        Assert.DoesNotContain(
            Diagnose("""
                keybindings {
                    bind "alt+p" { wm-enable-binding-mode --name "pause" }
                }

                binding-modes {
                    mode "pause" {
                        bind "alt+p" { wm-disable-binding-mode }
                    }
                }
                """),
            d => d.Code == "SHB0434");
    }

    [Fact]
    public void TheNameIsMatchedWithoutRegardToCase()
    {
        Assert.DoesNotContain(
            Diagnose("""
                keybindings {
                    bind "alt+p" { wm-enable-binding-mode --name "PAUSE" }
                }

                binding-modes {
                    mode "pause" {
                        bind "alt+p" { wm-disable-binding-mode }
                    }
                }
                """),
            d => d.Code == "SHB0434");
    }

    [Fact]
    public void ABindingInsideAModeIsCheckedToo()
    {
        // Harder to notice than the others, because reaching it means being in the
        // first mode already - so the keyboard is in an unusual state before the
        // mistake even has a chance to show itself.
        Diagnostic diagnostic = Assert.Single(
            Diagnose("""
                binding-modes {
                    mode "pause" {
                        bind "alt+p" { wm-disable-binding-mode }
                        bind "alt+r" { wm-enable-binding-mode --name "reszie" }
                    }

                    mode "resize" {
                        bind "escape" { wm-disable-binding-mode }
                    }
                }
                """),
            d => d.Code == "SHB0434");

        Assert.Contains("resize", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoModesDeclaredAtAllTheHintSaysSo()
    {
        // Suggesting the closest of nothing would be unhelpful; saying none exist and
        // how to declare one is the useful answer.
        Diagnostic diagnostic = Assert.Single(
            Diagnose("""
                keybindings {
                    bind "alt+p" { wm-enable-binding-mode --name "pause" }
                }
                """),
            d => d.Code == "SHB0434");

        Assert.Contains("binding-modes", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsAWarningSoTheRestOfTheConfigStillLoads()
    {
        // A mistyped mode must not cost the user every other keybinding they have.
        ConfigLoadResult result = ConfigLoader.Load("""
            keybindings {
                bind "alt+p" { wm-enable-binding-mode --name "nope" }
                bind "alt+h" { focus --direction left }
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Config.Keybindings.Count);
    }

    [Fact]
    public void LeavingAModeNeedsNoDeclaration()
    {
        // disable-binding-mode names nothing, so there is nothing to get wrong.
        Assert.DoesNotContain(
            Diagnose("""
                keybindings {
                    bind "alt+p" { wm-disable-binding-mode }
                }
                """),
            d => d.Code == "SHB0434");
    }

    [Fact]
    public void TheRealExampleConfigIsSilent()
    {
        const string Path = @"W:\Github\Shubbak\docs\shubbak.example.kdl";

        if (!File.Exists(Path)) return;

        Assert.DoesNotContain(
            Diagnose(File.ReadAllText(Path)),
            d => d.Code == "SHB0434");
    }
}
