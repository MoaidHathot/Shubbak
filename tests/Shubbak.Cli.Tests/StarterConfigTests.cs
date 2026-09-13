using Shubbak.Cli;
using Shubbak.Config;

namespace Shubbak.Cli.Tests;

/// <summary>
/// The starter config written by <c>shubbak config init</c>.
/// </summary>
/// <remarks>
/// <para>
/// Loaded through the real parser rather than eyeballed. This is the first file a new
/// user ever sees, written by the command the loader itself recommends when no config
/// is found - so a syntax error or a renamed setting in it would greet somebody who
/// has just installed Shubbak and has no reason yet to suspect the tool rather than
/// themselves.
/// </para>
/// <para>
/// It is also exactly the kind of thing that rots silently: it is a string constant,
/// so renaming a setting elsewhere in the codebase cannot break the build here.
/// </para>
/// </remarks>
public class StarterConfigTests
{
    private static ConfigLoadResult Load() => ConfigLoader.Load(ConfigCommand.Starter);

    [Fact]
    public void ItLoadsWithoutErrors()
    {
        ConfigLoadResult result = Load();

        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// Warnings count too.
    /// </summary>
    /// <remarks>
    /// Shubbak warns about duplicate bindings, unknown commands and rules that would
    /// match every window. A shipped starter config that trips any of those is
    /// teaching the mistake it exists to prevent.
    /// </remarks>
    [Fact]
    public void ItLoadsWithoutWarnings()
    {
        ConfigLoadResult result = Load();

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The bar, the palette and the watcher read their own sections of the same file,
    /// with their own loaders. Each must be as clean as the window manager's part:
    /// the starter turns all three on, so a warning in any of them is the first thing
    /// a new user's log says.
    /// </summary>
    [Fact]
    public void TheBarSectionIsClean()
    {
        (Taj.Core.TajConfig bar, IReadOnlyList<Diagnostic> diagnostics) = Taj.Core.TajConfigLoader.Load(ConfigCommand.Starter);

        Assert.Empty(diagnostics);
        Assert.NotEmpty(bar.Profiles);
    }

    [Fact]
    public void ThePaletteSectionIsClean()
    {
        Dalil.Core.DalilConfigLoad palette = Dalil.Core.DalilConfigLoader.Validate(ConfigCommand.Starter);

        Assert.Empty(palette.Diagnostics);
    }

    [Fact]
    public void TheWatcherSectionIsClean()
    {
        Assert.Empty(Ayn.Core.AynConfigLoader.Validate(ConfigCommand.Starter).Diagnostics);
    }

    /// <summary>
    /// The three companions are started from the config, not by the daemon, so the
    /// starter has to say so or the desktop comes up with no bar and no palette.
    /// </summary>
    [Fact]
    public void ItStartsTheCompanions()
    {
        IReadOnlyList<string> startup = Load().Config.StartupCommands;

        Assert.Contains("taj", startup);
        Assert.Contains("dalil", startup);
        Assert.Contains("ayn", startup);
    }

    /// <summary>
    /// The palette is opened by a signal, so a starter that starts Dalil but binds no
    /// key to the signal it listens for has a palette nobody can reach.
    /// </summary>
    [Fact]
    public void ThePaletteHasAKey()
    {
        Dalil.Core.DalilConfigLoad palette = Dalil.Core.DalilConfigLoader.Validate(ConfigCommand.Starter);
        string signal = palette.Config.OpenOnSignal;

        Assert.Contains(
            Load().Config.Keybindings,
            binding => binding.Commands.Any(command =>
                command is Shubbak.Core.Commands.SignalCommand raised && raised.Signal == signal));
    }

    /// <summary>
    /// The ten declared workspaces survive parsing.
    /// </summary>
    [Fact]
    public void TheWorkspacesAreDeclared()
    {
        Assert.Equal(10, Load().Config.Workspaces.Count);
    }

    /// <summary>
    /// <c>for-each</c> generates a pair of bindings per workspace on top of the
    /// hand-written ones, so this is also a check that the generator ran at all.
    /// </summary>
    [Fact]
    public void TheGeneratedWorkspaceBindingsAreThere()
    {
        ConfigLoadResult result = Load();

        // Ten workspaces, two bindings each, plus the hand-written ones.
        Assert.True(
            result.Config.Keybindings.Count >= 20,
            $"expected at least the 20 generated bindings, found {result.Config.Keybindings.Count}");
    }

    /// <summary>
    /// The starter must not be empty of the thing it is for.
    /// </summary>
    [Fact]
    public void ItBindsSomethingUsable()
    {
        Assert.NotEmpty(Load().Config.Keybindings);
    }

    /// <summary>
    /// The palette opens on <c>alt+shift+space</c>. <c>alt+space</c> is the system menu
    /// and PowerToys Run, and the collision was the first thing every new user hit.
    /// </summary>
    [Fact]
    public void ThePaletteIsOnAltShiftSpace()
    {
        Assert.Contains(
            Load().Config.Keybindings,
            binding => string.Equals(binding.Key.Display, "alt+shift+space", StringComparison.OrdinalIgnoreCase) &&
                       binding.Commands.Any(command => command is Shubbak.Core.Commands.SignalCommand { Signal: "palette" }));
    }

    /// <summary>
    /// The escape hatches. A newcomer's first bad moment is a key that does nothing,
    /// and these are the three ways of making that deliberate - so the starter has to
    /// bind each and the bar has to show each, or the moment looks like a crash.
    /// </summary>
    [Fact]
    public void TheEscapeHatchesAreBound()
    {
        ConfigLoadResult result = Load();

        Assert.Contains(result.Config.BindingModes, mode => mode.Name == "pause" && mode.PassThrough);
        Assert.Contains(result.Config.BindingModes, mode => mode.Name == "resize");
        Assert.Contains(result.Config.Keybindings, b => b.Commands.Any(c => c is Shubbak.Core.Commands.TogglePauseCommand));
        Assert.Contains(result.Config.Keybindings, b => b.Commands.Any(c => c is Shubbak.Core.Commands.ToggleSuspendCommand));
    }

    /// <summary>
    /// Every action in the palette section is well-formed. A starter shipping an action
    /// the palette reports as a problem is teaching the mistake.
    /// </summary>
    [Fact]
    public void ThePaletteActionsAreUsable()
    {
        Dalil.Core.DalilConfigLoad palette = Dalil.Core.DalilConfigLoader.Validate(ConfigCommand.Starter);

        Assert.NotEmpty(palette.Config.Macros);
        Assert.All(palette.Config.Macros, macro => Assert.True(
            string.IsNullOrEmpty(macro.Problem),
            $"action \"{macro.Name}\": {macro.Problem}"));
    }

    /// <summary>
    /// Every action a keybinding runs by name exists in the palette section, or the key
    /// opens an empty commands list with a warning in the log.
    /// </summary>
    [Fact]
    public void EveryActionAKeyRunsExists()
    {
        Dalil.Core.DalilConfigLoad palette = Dalil.Core.DalilConfigLoader.Validate(ConfigCommand.Starter);
        HashSet<string> names = palette.Config.Macros.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (Shubbak.Core.Commands.SignalCommand raised in Load().Config.Keybindings
                     .SelectMany(b => b.Commands)
                     .OfType<Shubbak.Core.Commands.SignalCommand>()
                     .Where(s => s.Arguments.Count >= 2 && s.Arguments[0] == "run"))
        {
            string name = string.Join(' ', raised.Arguments.Skip(1));
            Assert.True(names.Contains(name), $"a key runs the action \"{name}\", which the dalil section does not define");
        }
    }
}
