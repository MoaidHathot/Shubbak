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
    /// The five declared workspaces survive parsing.
    /// </summary>
    [Fact]
    public void TheWorkspacesAreDeclared()
    {
        Assert.Equal(5, Load().Config.Workspaces.Count);
    }

    /// <summary>
    /// <c>for-each</c> generates a pair of bindings per workspace on top of the
    /// hand-written ones, so this is also a check that the generator ran at all.
    /// </summary>
    [Fact]
    public void TheGeneratedWorkspaceBindingsAreThere()
    {
        ConfigLoadResult result = Load();

        // Five workspaces, two bindings each.
        Assert.True(
            result.Config.Keybindings.Count >= 10,
            $"expected at least the 10 generated bindings, found {result.Config.Keybindings.Count}");
    }

    /// <summary>
    /// The starter must not be empty of the thing it is for.
    /// </summary>
    [Fact]
    public void ItBindsSomethingUsable()
    {
        Assert.NotEmpty(Load().Config.Keybindings);
    }
}
