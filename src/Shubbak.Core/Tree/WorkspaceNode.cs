using Shubbak.Core.Layouts;

namespace Shubbak.Core.Tree;

/// <summary>
/// A workspace: a named, switchable surface belonging to exactly one monitor.
/// </summary>
/// <remarks>
/// <para>
/// A workspace <i>is</i> a <see cref="ContainerNode"/>, exactly as in i3/sway.
/// Consequences worth stating, because they remove code that other window
/// managers have to write:
/// </para>
/// <list type="bullet">
///   <item>"change the layout of this workspace" is
///   <c>workspace.Layout = ...</c> - no separate per-workspace layout table;</item>
///   <item>the layout engine recurses uniformly from the root without ever
///   special-casing the workspace level.</item>
/// </list>
/// <para>
/// <see cref="Name"/> is the identity used by keybindings and config
/// (matching GlazeWM's <c>focus --workspace 3</c>); <see cref="DisplayName"/> is
/// what the bar shows. Keeping them separate is what lets a workspace be bound to
/// <c>alt+1</c> while displaying "Firefox".
/// </para>
/// </remarks>
public sealed class WorkspaceNode : ContainerNode
{
    public WorkspaceNode(string name, ILayout? layout = null)
        : base(layout)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>Stable identifier used by config and keybindings, e.g. "3" or "-".</summary>
    public string Name { get; }

    /// <summary>Human-facing label for the bar; falls back to <see cref="Name"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>What the bar should render.</summary>
    public string Label => DisplayName ?? Name;

    /// <summary>
    /// The monitor index this workspace prefers, from config
    /// (GlazeWM's <c>bind_to_monitor</c>). Null means "any".
    /// </summary>
    public int? PreferredMonitorIndex { get; init; }

    /// <summary>
    /// True when this workspace exists only because a window was placed on it, and
    /// so should be discarded once empty.
    /// </summary>
    /// <remarks>
    /// Workspaces declared in config are not transient: an empty
    /// declared workspace must survive so that its keybinding keeps working.
    /// </remarks>
    public bool IsTransient { get; init; }

    /// <summary>The window that had focus when this workspace was last active.</summary>
    public WindowNode? LastFocused { get; set; }

    /// <summary>True when this is the workspace currently displayed on its monitor.</summary>
    public bool IsActive => Monitor?.ActiveWorkspace == this;

    /// <summary>True when no windows are present, transitively.</summary>
    public bool HasNoWindows => !DescendantWindows().Any();

    /// <summary>True when this workspace should be reaped once it loses its windows.</summary>
    public bool ShouldReap => IsTransient && HasNoWindows && !IsActive;

    public override string ToString() =>
        $"Workspace#{Id}[{Name}{(DisplayName is null ? "" : $" \"{DisplayName}\"")}, {Count} children]";
}
