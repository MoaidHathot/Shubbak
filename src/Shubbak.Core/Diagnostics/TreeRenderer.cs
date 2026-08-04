using System.Globalization;
using System.Text;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Diagnostics;

/// <summary>
/// Renders the window tree as indented text.
/// </summary>
/// <remarks>
/// <para>
/// A drawing of the tree is worth far more than the same facts as JSON when the
/// question is "why is this window the wrong size?" - the nesting is the answer, and
/// nesting is what an indented rendering shows at a glance.
/// </para>
/// <para>
/// It lives here rather than in the daemon because it is the one part of the
/// diagnostic report with no platform in it at all: monitors, workspaces, containers
/// and windows are all core types, and what it produces is the section of
/// <c>shubbak diagnose</c> that people actually paste into a bug report.
/// </para>
/// <para>
/// Formatted with the invariant culture throughout. A size ratio rendered as
/// <c>0,750</c> because the reporter's machine is German is not a difference anyone
/// reading the report wants to have to notice.
/// </para>
/// </remarks>
public static class TreeRenderer
{
    /// <summary>
    /// Draws every monitor, its workspaces, and the nodes within them.
    /// </summary>
    /// <param name="root">The tree to draw.</param>
    /// <param name="focused">
    /// The focused window, marked in the output. Passed in rather than read from the
    /// tree, because focus belongs to the window manager and the tree does not record
    /// it.
    /// </param>
    public static string Render(RootNode root, WindowNode? focused)
    {
        ArgumentNullException.ThrowIfNull(root);

        var output = new StringBuilder();

        foreach (MonitorNode monitor in root.Monitors)
        {
            output.AppendLine(
                CultureInfo.InvariantCulture,
                $"monitor {monitor.DeviceId}{(monitor.IsPrimary ? " (primary)" : "")} " +
                $"{monitor.Bounds} work={monitor.WorkArea} dpi={monitor.Dpi}");

            foreach (WorkspaceNode workspace in monitor.Workspaces)
            {
                output.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  workspace \"{workspace.Name}\"{(workspace.IsActive ? " [active]" : "")} " +
                    $"layout={workspace.Layout.Name} {workspace.Rect}");

                foreach (Node child in workspace.Children) Draw(child, output, focused, depth: 2);
            }
        }

        // Distinguishable from a failure. An empty tree is what a daemon that has
        // adopted nothing looks like, and a blank section reads as the report being
        // broken rather than the desktop being empty.
        return output.Length == 0 ? "(empty)" : output.ToString();
    }

    private static void Draw(Node node, StringBuilder output, WindowNode? focused, int depth)
    {
        string indent = new(' ', depth * 2);

        switch (node)
        {
            case WindowNode window:
                output.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{indent}window 0x{window.Handle:X} \"{window.Identity.Title.Truncate(40)}\" " +
                    $"({window.Identity.ProcessName}) {window.State} " +
                    $"ratio={window.SizeRatio:F3} {window.Rect}" +
                    $"{(ReferenceEquals(window, focused) ? " [focused]" : "")}");
                break;

            case ContainerNode container:
                output.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{indent}container layout={container.Layout.Name} " +
                    $"ratio={container.SizeRatio:F3} {container.Rect}");

                foreach (Node child in container.Children) Draw(child, output, focused, depth + 1);
                break;

            default:
                break;
        }
    }
}
