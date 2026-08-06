using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Wm;

/// <summary>
/// Decides where focus goes when the focused window disappears.
/// </summary>
/// <remarks>
/// <para>
/// This is small but disproportionately important to how the window manager feels.
/// Closing a window is one of the most frequent operations there is, and if focus
/// lands somewhere arbitrary the user has to stop and look for it every single
/// time. Getting it wrong is the difference between a window manager that
/// disappears into muscle memory and one that constantly interrupts.
/// </para>
/// <para>
/// The rule is <b>spatial locality</b>: prefer the nearest sibling, then widen the
/// search outwards through ancestors. That keeps focus physically close to where
/// the user was looking. i3 behaves this way, and it is why closing a window in a
/// column leaves you in the same column rather than jumping across the screen.
/// </para>
/// </remarks>
public static class FocusPolicy
{
    /// <summary>
    /// The window that should receive focus once <paramref name="leaving"/> is gone.
    /// </summary>
    /// <param name="leaving">
    /// The window about to be removed, hidden, or floated. Must still be attached:
    /// the answer depends on its position among its siblings.
    /// </param>
    /// <returns>
    /// The successor, or <see langword="null"/> when nothing focusable remains on
    /// the workspace.
    /// </returns>
    public static WindowNode? SuccessorFor(WindowNode leaving)
    {
        ArgumentNullException.ThrowIfNull(leaving);

        Node current = leaving;

        while (current.ParentContainer is { } parent)
        {
            int index = parent.IndexOf(current);

            if (index >= 0)
            {
                // Forwards first: closing a window in a row should leave focus on the
                // one that slides into its place, which is the one that was to its
                // right.
                for (int i = index + 1; i < parent.Children.Count; i++)
                    if (Candidate(parent.Children[i], leaving) is { } after) return after;

                for (int i = index - 1; i >= 0; i--)
                    if (Candidate(parent.Children[i], leaving) is { } before) return before;
            }

            // Nothing at this level; widen to the enclosing container. Stop at the
            // workspace, since focus never silently crosses to another workspace.
            if (parent is WorkspaceNode) break;

            current = parent;
        }

        // Last resort: anything still on the workspace. Reached only when the tree
        // shape is unusual, e.g. the window was inside a container whose other
        // children are all floating.
        //
        // Tiled first, then anything not minimised - the same two tiers as
        // OnWorkspaceActivated, and for the same reason. Filtering this on IsTiled
        // alone meant that closing the last tiled window beside a fullscreen or
        // floating one returned nothing, focus was cleared, and the keyboard had no
        // way to get it back.
        List<WindowNode> remaining = [.. leaving.Workspace?
            .DescendantWindows()
            .Where(w => !ReferenceEquals(w, leaving) && w.State != WindowState.Minimised)
            ?? []];

        return remaining.FirstOrDefault(w => w.IsTiled) ?? remaining.FirstOrDefault();
    }

    /// <summary>
    /// The window to focus when a workspace becomes active.
    /// </summary>
    /// <remarks>
    /// Prefers the workspace's own last-focused window, so switching away and back
    /// is lossless. Falls back to the first tiled window, then - only if the
    /// workspace has nothing tiled at all - to a floating one, so that a workspace
    /// containing only floating windows is not left unfocusable.
    /// </remarks>
    public static WindowNode? OnWorkspaceActivated(WorkspaceNode workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (workspace.LastFocused is { } last &&
            last.Workspace == workspace &&
            last.State != WindowState.Minimised)
        {
            return last;
        }

        List<WindowNode> windows = [.. workspace.DescendantWindows()
            .Where(w => w.State != WindowState.Minimised)];

        return windows.FirstOrDefault(w => w.IsTiled) ?? windows.FirstOrDefault();
    }

    /// <summary>
    /// The window nearest <paramref name="origin"/> within
    /// <paramref name="workspace"/>, by centre distance.
    /// </summary>
    /// <remarks>
    /// Used when focus crosses to another monitor: the user's attention is at the
    /// edge they moved through, so the window closest to that point is the least
    /// surprising landing place.
    /// </remarks>
    public static WindowNode? NearestTo(WorkspaceNode workspace, Rect origin)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        WindowNode? best = null;
        long bestDistance = long.MaxValue;

        foreach (WindowNode window in workspace.DescendantWindows())
        {
            if (!window.IsTiled) continue;

            long dx = window.Rect.CenterX - origin.CenterX;
            long dy = window.Rect.CenterY - origin.CenterY;
            long distance = (dx * dx) + (dy * dy);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = window;
            }
        }

        return best ?? OnWorkspaceActivated(workspace);
    }

    private static WindowNode? Candidate(Node node, WindowNode excluding) =>
        node.DescendantWindows().FirstOrDefault(w => w.IsTiled && !ReferenceEquals(w, excluding));
}
