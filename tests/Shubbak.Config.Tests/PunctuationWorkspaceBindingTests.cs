using Shubbak.Core.Commands;

namespace Shubbak.Config.Tests;

/// <summary>
/// Per-workspace bindings generated for workspaces named after punctuation.
/// </summary>
/// <remarks>
/// The author's config names workspaces <c>;</c> <c>\</c> <c>/</c> <c>'</c> <c>-</c> and
/// others, and generates their bindings with a <c>for-each</c> block whose command
/// separator is itself a semicolon. Bindings for two of those workspaces were reported
/// as activating the wrong workspace, so every step of that expansion is pinned here.
/// </remarks>
public sealed class PunctuationWorkspaceBindingTests
{
    // Reproduces the shape of the author's config, punctuation workspaces included.
    private const string Config = """
        workspaces {
            workspace "1"  display-name="Firefox"      monitor=0
            workspace "2"  display-name="Edge"         monitor=0
            workspace "3"  display-name="Code"         monitor=0
            workspace "0"  display-name="Docs"         monitor=0
            workspace "-"  display-name="Chat"
            workspace "\\" display-name="Presentation" monitor=0
            workspace "="  display-name="Notes"
            workspace "]"  display-name="Mail"         monitor=0
            workspace "/"  display-name="Second Monitor" monitor=1
            workspace "`"  display-name="System"       monitor=1
            workspace "'"  display-name="AI"           monitor=0
            workspace ";"  display-name="Slides"       monitor=0
            workspace "["  display-name="Recording"    monitor=2
        }

        keybindings {
            for-each "workspace" {
                bind "alt+{name}"       { focus --workspace "{name}" }
                bind "alt+shift+{name}" { move --workspace "{name}"; focus --workspace "{name}" }
            }
        }
        """;

    private static ShubbakConfig Load()
    {
        ConfigLoadResult result = ConfigLoader.Load(Config);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return result.Config;
    }

    [Theory]
    [InlineData(";")]
    [InlineData("\\")]
    [InlineData("/")]
    [InlineData("'")]
    [InlineData("-")]
    [InlineData("=")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("`")]
    public void FocusTargetsTheWorkspaceTheBindingWasGeneratedFor(string name)
    {
        ShubbakConfig config = Load();

        Keybinding binding = Assert.Single(
            config.Keybindings, b =>
                b.Commands is [FocusWorkspaceCommand focus] && focus.Workspace == name);

        Assert.Equal(name, ((FocusWorkspaceCommand)binding.Commands[0]).Workspace);
    }

    [Theory]
    [InlineData(";")]
    [InlineData("\\")]
    [InlineData("/")]
    [InlineData("'")]
    public void TheSemicolonSeparatedMoveAndFocusBothSurvive(string name)
    {
        // The command separator is a semicolon and one workspace is *named* one, so
        // this is where a naive split would lose or mangle a command.
        ShubbakConfig config = Load();

        Keybinding binding = Assert.Single(
            config.Keybindings, b =>
                b.Commands is [MoveToWorkspaceCommand move, _] && move.Workspace == name);

        MoveToWorkspaceCommand moved = Assert.IsType<MoveToWorkspaceCommand>(binding.Commands[0]);
        FocusWorkspaceCommand focused = Assert.IsType<FocusWorkspaceCommand>(binding.Commands[1]);

        Assert.Equal(name, moved.Workspace);
        Assert.Equal(name, focused.Workspace);
    }

    [Fact]
    public void NoTwoBindingsShareAKeystroke()
    {
        // The failure the user sees - pressing one workspace's key and landing on
        // another - is exactly what a duplicate keystroke produces.
        ShubbakConfig config = Load();

        string[] duplicates = [.. config.Keybindings
            .GroupBy(b => (b.Key.Modifiers, b.Key.VirtualKey))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.First().Key.Display} (mods {g.Key.Modifiers}, vk 0x{g.Key.VirtualKey:X2}) " +
                         $"is bound by: {string.Join(" and ", g.Select(Describe))}")];

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryWorkspaceKeystrokeIsDistinctFromEveryOther()
    {
        // Stated over the whole set rather than pairwise, so a collision shows up no
        // matter which two workspaces cause it.
        ShubbakConfig config = Load();

        int distinct = config.Keybindings
            .Select(b => (b.Key.Modifiers, b.Key.VirtualKey))
            .Distinct()
            .Count();

        Assert.Equal(config.Keybindings.Count, distinct);
    }

    [Fact]
    public void EveryWorkspaceGetsBothOfItsBindings()
    {
        ShubbakConfig config = Load();

        Assert.Equal(config.Workspaces.Count * 2, config.Keybindings.Count);
    }

    private static string Describe(Keybinding binding) =>
        "[" + string.Join(" ; ", binding.Commands.Select(c => c switch
        {
            FocusWorkspaceCommand f => $"focus {f.Workspace}",
            MoveToWorkspaceCommand m => $"move {m.Workspace}",
            _ => c.GetType().Name,
        })) + "]";
}
