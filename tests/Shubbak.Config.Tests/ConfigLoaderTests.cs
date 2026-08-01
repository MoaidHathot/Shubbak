using Shubbak.Core.Commands;
using Shubbak.Core.Geometry;

namespace Shubbak.Config.Tests;

/// <summary>Tests for <see cref="ConfigLoader"/>.</summary>
public sealed class ConfigLoaderTests
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
    public void ReadsGeneralSettings()
    {
        ShubbakConfig config = LoadOk("""
            general {
                toggle-workspace-on-refocus #true
                focus-follows-cursor #false
                initial-window-state "floating"
                startup-command "shell-exec pwsh -Command Restart-Taj"
            }
            """);

        Assert.True(config.ToggleWorkspaceOnRefocus);
        Assert.False(config.FocusFollowsCursor);
        Assert.Equal(Core.Tree.WindowState.Floating, config.InitialWindowState);
        Assert.Single(config.StartupCommands);
    }

    [Fact]
    public void ReadsGaps()
    {
        ShubbakConfig config = LoadOk("""
            gaps {
                inner 6
                outer { top 26; right 4; bottom 4; left 4 }
            }
            """);

        Assert.Equal(6, config.InnerGap);
        Assert.Equal(new Gaps(4, 26, 4, 4), config.OuterGap);
    }

    [Fact]
    public void OuterGapAcceptsASingleUniformValue()
    {
        ShubbakConfig config = LoadOk("gaps { inner 4; outer 8 }");
        Assert.Equal(Gaps.All(8), config.OuterGap);
    }

    [Fact]
    public void ReadsWorkspacesIncludingPunctuationNames()
    {
        ShubbakConfig config = LoadOk("""
            workspaces {
                workspace "1" display-name="Firefox" monitor=0
                workspace "-" display-name="Chat"
                workspace "\\" display-name="Presentation"
            }
            """);

        Assert.Equal(3, config.Workspaces.Count);
        Assert.Equal("Firefox", config.Workspaces[0].DisplayName);
        Assert.Equal(0, config.Workspaces[0].BindToMonitor);
        Assert.Equal("-", config.Workspaces[1].Name);
        Assert.Equal("\\", config.Workspaces[2].Name);
    }

    [Fact]
    public void ParsesKeybindingsIntoCommands()
    {
        ShubbakConfig config = LoadOk("""
            keybindings {
                bind "alt+h" { focus --direction left }
                bind "alt+shift+3" { move --workspace 3; focus --workspace 3 }
            }
            """);

        Assert.Equal(2, config.Keybindings.Count);

        Assert.IsType<FocusDirectionCommand>(config.Keybindings[0].Commands[0]);
        Assert.Equal(Direction.Left, ((FocusDirectionCommand)config.Keybindings[0].Commands[0]).Direction);

        // The two-command sequence the author's config relies on.
        Assert.Equal(2, config.Keybindings[1].Commands.Count);
        Assert.IsType<MoveToWorkspaceCommand>(config.Keybindings[1].Commands[0]);
        Assert.IsType<FocusWorkspaceCommand>(config.Keybindings[1].Commands[1]);
    }

    [Fact]
    public void ForEachGeneratesPerWorkspaceBindings()
    {
        // The feature that replaces 40 hand-written lines with six, and that can
        // never drift out of sync with the workspace list.
        ShubbakConfig config = LoadOk("""
            workspaces {
                workspace "1"
                workspace "2"
                workspace "-"
            }

            keybindings {
                for-each "workspace" {
                    bind "alt+{name}"       { focus --workspace "{name}" }
                    bind "alt+shift+{name}" { move --workspace "{name}"; focus --workspace "{name}" }
                }
            }
            """);

        Assert.Equal(6, config.Keybindings.Count);

        string[] workspacesFocused = [.. config.Keybindings
            .SelectMany(b => b.Commands)
            .OfType<FocusWorkspaceCommand>()
            .Select(c => c.Workspace)
            .Distinct()];

        Assert.Equal(["1", "2", "-"], workspacesFocused);
    }

    [Fact]
    public void ForEachWithNoWorkspacesWarnsRatherThanSilentlyProducingNothing()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            keybindings {
                for-each "workspace" {
                    bind "alt+{name}" { focus --workspace "{name}" }
                }
            }
            """);

        Assert.Contains(result.Warnings, d => d.Code == "SHB0406");
        Assert.NotNull(result.Warnings.First(d => d.Code == "SHB0406").Hint);
    }

    [Fact]
    public void SlashDelimitedRegexIsWarnedAbout()
    {
        // The exact bug in the author's GlazeWM config: the slashes are matched
        // literally, so the rule never fires and nothing says so.
        ConfigLoadResult result = ConfigLoader.Load("""
            app "powerpoint" {
                title regex="/[Pp]ower[Pp]oint [Ss]lide [Ss]how.*/"
            }
            """);

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0414");

        Assert.Contains("matched literally", warning.Message, StringComparison.Ordinal);

        // And it says what to write instead, which is the half that saves an hour.
        Assert.NotNull(warning.Hint);
        Assert.Contains("[Pp]ower[Pp]oint", warning.Hint, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/", warning.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidRegexIsAnError()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            app "broken" {
                title regex="[unclosed"
            }
            """);

        Assert.Contains(result.Errors, d => d.Code == "SHB0415");
    }

    [Fact]
    public void RulesReferenceNamedApps()
    {
        ShubbakConfig config = LoadOk("""
            app "taj" { process = "taj" }

            rules {
                rule "ignore the bar" {
                    match { app "taj" }
                    do { ignore }
                }
            }
            """);

        WindowRule rule = Assert.Single(config.Rules);
        Assert.Equal("ignore the bar", rule.Name);
        Assert.IsType<IgnoreCommand>(rule.Commands[0]);

        var window = new WindowAttributes("Taj", "TajWindow", "taj", @"C:\taj.exe");
        Assert.True(rule.Matches(window, config.Apps));

        var other = new WindowAttributes("Firefox", "MozillaWindowClass", "firefox", null);
        Assert.False(rule.Matches(other, config.Apps));
    }

    [Fact]
    public void ReferencingAnUndefinedAppIsAnErrorWithAFix()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            rules {
                rule "oops" {
                    match { app "does-not-exist" }
                    do { ignore }
                }
            }
            """);

        Diagnostic error = Assert.Single(result.Errors, d => d.Code == "SHB0416");
        Assert.Contains("does-not-exist", error.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void RuleWithNoConditionsIsRejected()
    {
        // Such a rule would silently apply to every window, which is almost never
        // what anyone means and is painful to diagnose after the fact.
        ConfigLoadResult result = ConfigLoader.Load("""
            rules {
                rule "everything" {
                    do { ignore }
                }
            }
            """);

        Assert.Contains(result.Errors, d => d.Code == "SHB0417");
    }

    [Fact]
    public void UnknownCommandSuggestsTheClosestMatch()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            keybindings {
                bind "alt+h" { focuss --direction left }
            }
            """);

        Diagnostic error = Assert.Single(result.Errors, d => d.Code == "SHB0305");
        Assert.Contains("focus", error.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateBindingIsWarnedAboutWithTheEarlierLine()
    {
        // Silent shadowing is genuinely hard to debug: the binding is right there
        // in the file, looks correct, and never fires.
        ConfigLoadResult result = ConfigLoader.Load("""
            keybindings {
                bind "alt+h" { focus --direction left }
                bind "alt+h" { focus --direction right }
            }
            """);

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0409");
        Assert.Contains("line 2", warning.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ResizePercentagesBecomeRatios()
    {
        ShubbakConfig config = LoadOk("""
            keybindings {
                bind "alt+p" { resize --width +2% }
                bind "alt+u" { resize --width -5% }
            }
            """);

        Assert.Equal(0.02, ((ResizeCommand)config.Keybindings[0].Commands[0]).Delta, 1e-9);
        Assert.Equal(-0.05, ((ResizeCommand)config.Keybindings[1].Commands[0]).Delta, 1e-9);
    }

    [Fact]
    public void BindingModesAreParsed()
    {
        ShubbakConfig config = LoadOk("""
            binding-modes {
                mode "resize" {
                    bind "h" { resize --width -2% }
                    bind "escape" { wm-disable-binding-mode }
                }
                mode "pause" {
                    bind "alt+shift+p" { wm-disable-binding-mode }
                }
            }
            """);

        Assert.Equal(2, config.BindingModes.Count);
        Assert.Equal("resize", config.BindingModes[0].Name);
        Assert.Equal(2, config.BindingModes[0].Keybindings.Count);
    }

    [Fact]
    public void OneBadSectionDoesNotPreventTheRestFromLoading()
    {
        // A single typo must never leave the user with no window manager.
        ConfigLoadResult result = ConfigLoader.Load("""
            gaps { inner 6 }

            keybindings {
                bind "alt+h" { nonsense-command }
                bind "alt+l" { focus --direction right }
            }
            """);

        Assert.True(result.HasErrors);
        Assert.Equal(6, result.Config.InnerGap);
        Assert.Single(result.Config.Keybindings);
    }

    [Fact]
    public void TheShippedExampleConfigLoadsCleanly()
    {
        // The example is the author's GlazeWM config translated to KDL. If it ever
        // stops loading without warnings, the translation has regressed.
        string path = FindExampleConfig();
        ConfigLoadResult result = ConfigLoader.LoadFile(path);

        Assert.False(
            result.HasErrors,
            "Errors:\n" + string.Join("\n", result.Errors.Select(d => d.ToString())));

        Assert.False(
            result.Warnings.Any(),
            "Warnings:\n" + string.Join("\n", result.Warnings.Select(d => d.ToString())));

        // 19 workspaces, each generating two bindings, plus the fixed ones.
        Assert.Equal(19, result.Config.Workspaces.Count);
        Assert.True(result.Config.Keybindings.Count >= 38);
        Assert.Equal(2, result.Config.BindingModes.Count);
        Assert.Single(result.Config.Rules);
    }

    private static string FindExampleConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "docs", "shubbak.example.kdl");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate docs/shubbak.example.kdl.");
    }
}
