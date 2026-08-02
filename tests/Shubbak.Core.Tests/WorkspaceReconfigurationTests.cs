using Shubbak.Core.Tree;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests that a workspace can be reconfigured without being recreated.
/// </summary>
/// <remarks>
/// Reloading used to skip any workspace that already existed by name, so the settings
/// people change most often - a display name, which monitor it prefers, where it sits
/// in the bar - appeared to do nothing until the window manager was restarted. The
/// name is the identity; everything else is settings.
/// </remarks>
public sealed class WorkspaceReconfigurationTests
{
    [Fact]
    public void SettingsCanBeChangedOnAnExistingWorkspace()
    {
        var workspace = new WorkspaceNode("1")
        {
            DisplayName = "Old",
            SortIndex = 5,
            PreferredMonitorIndex = 0,
            IsTransient = true,
        };

        workspace.DisplayName = "New";
        workspace.SortIndex = 2;
        workspace.PreferredMonitorIndex = 1;
        workspace.IsTransient = false;

        Assert.Equal("New", workspace.DisplayName);
        Assert.Equal(2, workspace.SortIndex);
        Assert.Equal(1, workspace.PreferredMonitorIndex);
        Assert.False(workspace.IsTransient);
    }

    [Fact]
    public void TheLabelFollowsTheDisplayName()
    {
        // What the bar renders, so renaming has to reach it.
        var workspace = new WorkspaceNode("1") { DisplayName = "Firefox" };

        Assert.Equal("Firefox", workspace.Label);

        workspace.DisplayName = "Browser";

        Assert.Equal("Browser", workspace.Label);
    }

    [Fact]
    public void ClearingTheDisplayNameFallsBackToTheName()
    {
        var workspace = new WorkspaceNode("1") { DisplayName = "Firefox" };

        workspace.DisplayName = null;

        Assert.Equal("1", workspace.Label);
    }
}