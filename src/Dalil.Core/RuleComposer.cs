using System.Text;

namespace Dalil.Core;

/// <summary>
/// Writes the configuration a window's own attributes imply.
/// </summary>
/// <remarks>
/// <para>
/// The last step of the feature the window manager is proudest of. <c>shubbak
/// inspect</c> tells you exactly why a window is not being tiled and hands you every
/// attribute you would need to change that - and then stops, leaving the user to
/// transcribe a class name out of a report and hand-write KDL around it. The README
/// says "copy the attributes straight into a rule and you're done", which is true and
/// is still several minutes of somebody's afternoon, most of it spent checking whether
/// they typed <c>Chrome_WidgetWin_1</c> correctly.
/// </para>
/// <para>
/// So the palette writes it. Everything here is text - nothing is applied, nothing
/// touches the config file - because a window manager that edits your configuration
/// behind you is a worse idea than a little typing. It is shown, it is copyable, and
/// what happens to it next is the user's business.
/// </para>
/// <para>
/// Pure and in Core, so the exact bytes are testable without a window, a pipe or a
/// desktop.
/// </para>
/// </remarks>
public static class RuleComposer
{
    /// <summary>
    /// The rule that would match one window, and do nothing to it yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched on class and process rather than on title. A title is the most
    /// inviting attribute and the worst one to match on: it changes as the document
    /// changes, it is localised, and it frequently contains the very thing that made
    /// the window interesting for five seconds. Class and process are what the window
    /// actually is.
    /// </para>
    /// <para>
    /// The <c>do</c> block is deliberately left holding a comment rather than a verb.
    /// Guessing is the one thing this must not do: the same window that somebody wants
    /// floated is one somebody else wants ignored, moved, or merely managed, and a
    /// generated rule that quietly did the wrong thing would be worse than no rule at
    /// all - it would look right.
    /// </para>
    /// </remarks>
    /// <param name="name">What to call the rule, usually the window's application.</param>
    /// <param name="className">The window class, or null to leave it out.</param>
    /// <param name="processName">The process, or null to leave it out.</param>
    /// <param name="title">The title, offered commented-out as a third matcher.</param>
    public static string Rule(
        string? name,
        string? className,
        string? processName,
        string? title = null)
    {
        var text = new StringBuilder();

        text.Append("rules {\n");
        text.Append("    rule \"").Append(Escape(Label(name, processName, className))).Append("\" {\n");
        text.Append("        match {\n");

        bool matched = false;

        if (className is { Length: > 0 })
        {
            text.Append("            class \"").Append(Escape(className)).Append("\"\n");
            matched = true;
        }

        if (processName is { Length: > 0 })
        {
            text.Append("            process \"").Append(Escape(processName)).Append("\"\n");
            matched = true;
        }

        // A rule matching nothing at all would match every window on the desktop,
        // which the window manager warns about at load time and which is a
        // spectacularly bad thing to have generated for somebody.
        if (!matched)
            text.Append("            // Nothing identifying was readable - add a matcher here.\n");

        // Offered rather than applied. A title matcher is occasionally exactly right -
        // the picture-in-picture window is only findable that way - and is wrong often
        // enough that it must not be switched on by somebody who did not choose it.
        if (title is { Length: > 0 })
            text.Append("            // title \"").Append(Escape(title)).Append("\"\n");

        text.Append("        }\n");
        text.Append("        do {\n");
        text.Append("            // float, ignore, manage, move --workspace \"2\", ...\n");
        text.Append("        }\n");
        text.Append("    }\n");
        text.Append('}');

        return text.ToString();
    }

    /// <summary>The same rule, written from a full report.</summary>
    /// <remarks>
    /// Separate because a report knows things a list row does not - the executable's
    /// path above all, which is the matcher to reach for when two applications share
    /// a process name, as every Electron application on the machine does.
    /// </remarks>
    public static string RuleFromReport(
        string? className,
        string? processName,
        string? processPath,
        string? title)
    {
        string rule = Rule(null, className, processName, title);

        if (processPath is not { Length: > 0 }) return rule;

        // Inserted as a comment beside the process matcher it would replace. Two
        // matchers for the same idea, both live, is a rule that is stricter than
        // anybody meant - and silently dropping the one that is nearly always right
        // would be worse.
        return rule.Replace(
            $"            process \"{Escape(processName ?? string.Empty)}\"\n",
            $"            process \"{Escape(processName ?? string.Empty)}\"\n" +
            $"            // path \"{Escape(processPath)}\"\n",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A readable name for the rule.
    /// </summary>
    /// <remarks>
    /// The process without its extension, because <c>rule "msedge"</c> reads better
    /// than <c>rule "msedge.exe"</c> and is what somebody would have typed. Falls back
    /// through the class to a placeholder rather than producing <c>rule ""</c>, which
    /// the config loader would reject.
    /// </remarks>
    private static string Label(string? name, string? processName, string? className)
    {
        if (name is { Length: > 0 }) return name;

        if (processName is { Length: > 0 })
        {
            return processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;
        }

        return className is { Length: > 0 } ? className : "new rule";
    }

    /// <summary>
    /// Makes a value safe to sit inside a KDL string.
    /// </summary>
    /// <remarks>
    /// Backslashes first, or escaping the quotes would then have their own backslashes
    /// escaped a second time. Window titles contain both - a file path in a title is
    /// full of backslashes, and a quoted document name is not unusual.
    /// </remarks>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
