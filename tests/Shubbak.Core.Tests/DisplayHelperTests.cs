using Shubbak.Core.Commands;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

/// <summary>
/// The small shared helpers the daemon builds its log lines and reports from.
/// </summary>
/// <remarks>
/// All three lived as private members of WmDaemon, where nothing could reach them.
/// Truncate in particular is used seventeen times across window adoption, the drag
/// path, the tree rendering and the diagnostic report - so it is worth knowing it
/// does what it says at the edges rather than only in the middle.
/// </remarks>
public sealed class DisplayHelperTests
{
    [Fact]
    public void ShortEnoughTextIsLeftAlone()
    {
        Assert.Equal("Notepad", "Notepad".Truncate(40));

        // Exactly at the limit is not truncated: the limit is a maximum, not a budget
        // that has to leave room for the mark.
        Assert.Equal("abcde", "abcde".Truncate(5));
        Assert.Equal("", "".Truncate(5));
    }

    [Fact]
    public void LongerTextEndsInAnEllipsisAndFitsTheLimit()
    {
        string result = "a very long window title indeed".Truncate(10);

        Assert.Equal(10, result.Length);
        Assert.EndsWith("\u2026", result, StringComparison.Ordinal);
        Assert.StartsWith("a very lo", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(40)]
    [InlineData(48)]
    public void TheResultNeverExceedsTheLimit(int max)
    {
        // The whole point of asking for a maximum. Counting the ellipsis is what makes
        // columns in the tree rendering line up.
        Assert.True("some window title that is definitely longer than any of these".Truncate(max).Length <= max);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void AbsurdLimitsDoNotThrow(int max)
    {
        // This runs while building a diagnostic report. A report that dies because a
        // width was miscalculated is worse than one with a blank in it.
        string result = "anything".Truncate(max);

        Assert.True(result.Length <= Math.Max(max, 0) || result == "\u2026");
    }

    [Fact]
    public void OneCommandIsNamedWithoutAJoiner()
    {
        Assert.Equal("close", new WmCommand[] { new CloseWindowCommand() }.Describe());
    }

    [Fact]
    public void SeveralCommandsAreSeparated()
    {
        string described = new WmCommand[]
        {
            new CloseWindowCommand(),
            new ToggleFloatingCommand(),
        }.Describe();

        Assert.Equal("close; toggle-floating", described);
    }

    [Fact]
    public void NoCommandsIsEmptyRatherThanNull()
    {
        Assert.Equal(string.Empty, Array.Empty<WmCommand>().Describe());
    }

    [Fact]
    public void AWindowOnTheShownWorkspaceIsDisplayed()
    {
        MonitorNode monitor = TreeBuilder.Monitor();
        WorkspaceNode workspace = TreeBuilder.Workspace("1");
        WindowNode window = TreeBuilder.Window();

        monitor.AddWorkspace(workspace);
        workspace.Add(window);

        Assert.True(window.IsOnADisplayedWorkspace);
    }

    [Fact]
    public void AWindowOnAWorkspaceTheMonitorIsNotShowingIsNot()
    {
        // The case this exists for. Adoption focuses every window it takes on, so at
        // startup the focused window is whichever the enumeration reached last -
        // frequently on a workspace nobody is looking at. Forcing that to the
        // foreground raised it over the workspace the user was actually on.
        MonitorNode monitor = TreeBuilder.Monitor();
        WorkspaceNode shown = TreeBuilder.Workspace("1");
        WorkspaceNode hidden = TreeBuilder.Workspace("2");
        WindowNode window = TreeBuilder.Window();

        monitor.AddWorkspace(shown);
        monitor.AddWorkspace(hidden);
        hidden.Add(window);

        Assert.True(ReferenceEquals(monitor.ActiveWorkspace, shown));
        Assert.False(window.IsOnADisplayedWorkspace);
    }

    [Fact]
    public void AWindowWithNoWorkspaceIsNotDisplayed()
    {
        // A detached node - mid-unmanage, or never placed - has no answer, and the
        // honest one is "no" rather than a null reference.
        Assert.False(TreeBuilder.Window().IsOnADisplayedWorkspace);
    }
}
