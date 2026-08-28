using System.Text;
using Shubbak.Ipc;

namespace Shubbak.Cli;

/// <summary>
/// Prints a <see cref="WindowReport"/> the way <c>shubbak inspect</c> always has.
/// </summary>
/// <remarks>
/// <para>
/// The window manager sends the facts; this is the only place that decides what the
/// columns look like. It used to be decided in the daemon, which meant the palette -
/// the other client - had to take the padding apart again to find the labels, and the
/// width of a column in a window manager was quietly load-bearing for a different
/// process.
/// </para>
/// <para>
/// The layout itself is unchanged, deliberately. People have this output in issues and
/// in their scrollback, and reformatting it would have been a gratuitous break in the
/// middle of a change that is supposed to be invisible.
/// </para>
/// </remarks>
internal static class WindowReportText
{
    /// <summary>Renders a report, with as much of it as is known.</summary>
    /// <param name="report">What the window manager, or the local filter, worked out.</param>
    /// <param name="complete">
    /// Whether the tree and the configuration were available. False for the local path,
    /// which runs with no window manager and so cannot speak for either - and saying
    /// "managed no, rules (none configured)" there would be a confident lie rather
    /// than a gap.
    /// </param>
    public static string Format(WindowReport report, bool complete = true)
    {
        ArgumentNullException.ThrowIfNull(report);

        StringBuilder text = new();

        text.AppendLine($"handle       0x{report.Handle:X}");
        text.AppendLine($"title        {report.Title}");
        text.AppendLine($"class        {report.ClassName}");
        text.AppendLine($"process      {report.ProcessName}");
        text.AppendLine($"path         {report.ProcessPath ?? "(unreadable - elevated process?)"}");
        text.AppendLine($"rect         ({report.X},{report.Y} {report.Width}x{report.Height})");
        text.AppendLine($"style        0x{report.Style:X8}");
        text.AppendLine($"ex-style     0x{report.ExStyle:X8}");
        text.AppendLine($"visible      {report.Visible}");
        text.AppendLine($"cloaked      {report.Cloaked}");
        text.AppendLine($"minimised    {report.Minimised}");
        text.AppendLine();
        text.AppendLine($"manageable   {(report.Manageable ? "yes" : "no")} - {report.Verdict}");

        if (!complete) return text.ToString();

        if (report.Node is { } node)
        {
            text.AppendLine("managed      yes");
            text.AppendLine($"  node       #{node.Id}");
            text.AppendLine($"  state      {node.State}");
            text.AppendLine($"  workspace  {node.Workspace}");
            text.AppendLine($"  focused    {node.Focused}");
            text.AppendLine(
                $"  sticky     {(node.Sticky ? "yes - follows every workspace on its monitor" : "no")}");
            text.AppendLine($"  tags       {DescribeTags(node.Tags)}");

            if (node.Scratchpad is { Length: > 0 } slot)
                text.AppendLine($"  scratchpad {slot}");
        }
        else
        {
            text.AppendLine($"managed      no{(report.ExcludedByRule ? " (excluded by a rule)" : "")}");
        }

        text.AppendLine();
        text.AppendLine("rules");

        if (report.Rules.Count == 0)
        {
            text.AppendLine("  (none configured)");
        }
        else
        {
            foreach (RuleReport rule in report.Rules)
                text.AppendLine($"  [{(rule.Matched ? "x" : " ")}] {rule.Name} (line {rule.Line})");
        }

        // Listed separately because that is what turns "my rule does not fire" into a
        // one-glance diagnosis: the rule is usually fine and the app definition missed.
        if (report.Apps.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("apps");

            foreach (AppReport app in report.Apps)
            {
                text.AppendLine($"  [{(app.Matched ? "x" : " ")}] {app.Name}");

                foreach (string matcher in app.FailedMatchers)
                    text.AppendLine($"        failed: {matcher}");
            }
        }

        return text.ToString();
    }

    /// <summary>The workspaces a window will follow you to, or that it will not.</summary>
    /// <remarks>
    /// Worded as a consequence rather than as a list. A window that relocates itself
    /// whenever a workspace is activated reads as a fault, and "3, 7" alone does not
    /// say that is what is about to happen.
    /// </remarks>
    private static string DescribeTags(IReadOnlyList<string> tags) =>
        tags.Count == 0 ? "(none)" : $"{string.Join(", ", tags)} - it will follow you there";
}
