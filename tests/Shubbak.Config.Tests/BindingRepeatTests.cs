using Shubbak.Core.Commands;

namespace Shubbak.Config.Tests;

/// <summary>
/// Which bindings keep running while the key is held.
/// </summary>
/// <remarks>
/// <para>
/// Windows delivers auto-repeat as repeated key-downs with no release between them,
/// and every one used to be executed. There was no repeat flag, no debounce, no
/// coalescing and no rate limit anywhere in the path.
/// </para>
/// <para>
/// For focus and resize that is exactly right and is why holding the key feels good.
/// For the rest it is not: holding the close key closed everything on the workspace,
/// and holding a shell-exec key started a process per repeat at the hardware rate.
/// </para>
/// </remarks>
public sealed class BindingRepeatTests
{
    private static Keybinding Bind(params WmCommand[] commands) =>
        new(new KeyBinding(0, 0x48, "alt+h"), commands, default);

    [Fact]
    public void MovingAroundRepeats()
    {
        // The reason the default is to repeat: these are the bindings people hold.
        Assert.True(Bind(new FocusDirectionCommand(Core.Geometry.Direction.Left)).RepeatsOnHold);
        Assert.True(Bind(new MoveDirectionCommand(Core.Geometry.Direction.Left)).RepeatsOnHold);
        Assert.True(Bind(new ResizeCommand(Core.Geometry.Axis.Horizontal, 0.05)).RepeatsOnHold);
        Assert.True(Bind(new CycleFocusCommand(Forward: true)).RepeatsOnHold);
    }

    [Fact]
    public void ClosingAndLaunchingDoNot()
    {
        // The two that cost something real. A second of either is unrecoverable in
        // the first case and thirty processes in the second.
        Assert.False(Bind(new CloseWindowCommand()).RepeatsOnHold);
        Assert.False(Bind(new ShellExecCommand("wt.exe")).RepeatsOnHold);
        Assert.False(Bind(new ExitCommand()).RepeatsOnHold);
    }

    [Fact]
    public void TogglesDoNot()
    {
        // A toggle at the hardware repeat rate leaves the final state decided by
        // exactly when the key came up, which is not a decision the user made.
        Assert.False(Bind(new ToggleFloatingCommand()).RepeatsOnHold);
        Assert.False(Bind(new ToggleFullscreenCommand()).RepeatsOnHold);
        Assert.False(Bind(new ToggleMinimisedCommand()).RepeatsOnHold);
        Assert.False(Bind(new ToggleManagedCommand()).RepeatsOnHold);
        Assert.False(Bind(new TogglePauseCommand()).RepeatsOnHold);
        Assert.False(Bind(new CycleLayoutCommand(Forward: true)).RepeatsOnHold);
    }

    [Fact]
    public void EnteringAndLeavingAModeDoesNot()
    {
        Assert.False(Bind(new EnableBindingModeCommand("pause")).RepeatsOnHold);
        Assert.False(Bind(new DisableBindingModeCommand()).RepeatsOnHold);
    }

    [Fact]
    public void SendingTheFocusedWindowAwayDoesNot()
    {
        // Reported from use, and the reason this list needed a rule rather than a
        // list of things that felt dangerous.
        //
        // Two chat windows sat side by side on one workspace. Holding the move key
        // half a second too long sent both away, and it looked as though the two
        // windows were being treated as a pair. They were not: the first repeat moved
        // the focused window, focus fell to its neighbour, and the second repeat moved
        // that. The neighbour of a window is very often another window of the same
        // application, which is what made it look like a relationship.
        //
        // It is the same trap as close - already excluded because holding it "closed
        // everything on the workspace" - and it was missed because the consequence is
        // recoverable rather than final.
        Assert.False(Bind(new MoveToWorkspaceCommand("3")).RepeatsOnHold);
        Assert.False(Bind(new TagCommand("3", Core.Wm.TagMode.Add)).RepeatsOnHold);
        Assert.False(Bind(new ClearTagsCommand()).RepeatsOnHold);
        Assert.False(Bind(new MoveWorkspaceToMonitorCommand(Core.Geometry.Direction.Left)).RepeatsOnHold);
    }

    [Fact]
    public void TheTestIsWhetherARepeatActsOnTheSameThing()
    {
        // The distinction the first version of this list missed. Moving a window
        // within its layout keeps it focused, so holding the key pushes one window
        // further and further - which is what the user is asking for. Moving it to
        // another workspace does not, so holding the key sends a procession of
        // windows after it.
        //
        // Both are called "move". Only one of them can be held.
        Assert.True(Bind(new MoveDirectionCommand(Core.Geometry.Direction.Left)).RepeatsOnHold);
        Assert.False(Bind(new MoveToWorkspaceCommand("3")).RepeatsOnHold);
    }

    [Fact]
    public void TheDangerousCommandInAListDecidesForTheWholeBinding()
    {
        // A binding runs its commands together, so it can only be as repeatable as its
        // least repeatable part.
        Keybinding mixed = Bind(
            new FocusDirectionCommand(Core.Geometry.Direction.Left),
            new CloseWindowCommand());

        Assert.False(mixed.RepeatsOnHold);
    }

    [Fact]
    public void TheConfigOverridesTheDefaultInBothDirections()
    {
        var repeating = new Keybinding(
            new KeyBinding(0, 0x51, "alt+q"), [new CloseWindowCommand()], default, Repeat: true);

        var once = new Keybinding(
            new KeyBinding(0, 0x48, "alt+h"),
            [new FocusDirectionCommand(Core.Geometry.Direction.Left)],
            default,
            Repeat: false);

        Assert.True(repeating.RepeatsOnHold);
        Assert.False(once.RepeatsOnHold);
    }

    // ---- reaching it from a config file ------------------------------------

    private static Keybinding Only(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(
            result.HasErrors,
            "Unexpected errors:\n" + string.Join("\n", result.Errors.Select(d => d.ToString())));

        return Assert.Single(result.Config.Keybindings);
    }

    [Theory]
    [InlineData("#false", false)]
    [InlineData("#true", true)]
    public void RepeatCanBeWrittenInTheConfig(string written, bool expected)
    {
        // This is the shape a user would naturally reach for, and it parsed cleanly,
        // produced no diagnostic, and was ignored - bind nodes read no properties at
        // all.
        Keybinding binding = Only($$"""
            keybindings {
                bind "alt+q" repeat={{written}} { focus --direction left }
            }
            """);

        Assert.Equal(expected, binding.RepeatsOnHold);
    }

    [Fact]
    public void WithoutThePropertyTheCommandsStillDecide()
    {
        Assert.False(Only("""
            keybindings {
                bind "alt+q" { close }
            }
            """).RepeatsOnHold);

        Assert.True(Only("""
            keybindings {
                bind "alt+h" { focus --direction left }
            }
            """).RepeatsOnHold);
    }

    [Fact]
    public void AnUnknownPropertyOnABindingIsReported()
    {
        // Same class of silence the loader already warns about for sections and
        // settings, in the one place it had never been applied.
        ConfigLoadResult result = ConfigLoader.Load("""
            keybindings {
                bind "alt+q" reapeat=#false { close }
            }
            """);

        Assert.False(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0433");
    }

    [Fact]
    public void ARepeatThatIsNotABooleanIsReported()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            keybindings {
                bind "alt+q" repeat="no" { close }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0432");
    }
}
