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
/// So the palette writes it. Everything here is text. Nothing here applies anything or
/// touches the config file; what the text is for - reading, copying, or handing to the
/// window manager to add - is decided by whoever asked for it, and the window manager
/// adds it only when asked in so many words.
/// </para>
/// <para>
/// Pure and in Core, so the exact bytes are testable without a window, a pipe or a
/// desktop.
/// </para>
/// </remarks>
public static class RuleComposer
{
    /// <summary>The line the <c>do</c> block holds when nobody has chosen a verb yet.</summary>
    public const string Undecided = "// float, ignore, manage, move --workspace \"2\", ...";

    /// <summary>
    /// The rule that would match one window.
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
    /// With no verbs given, the <c>do</c> block holds a comment rather than a guess.
    /// The same window that somebody wants floated is one somebody else wants ignored,
    /// moved, or merely managed, and a generated rule that quietly did the wrong thing
    /// would be worse than no rule at all - it would look right. With verbs given, the
    /// user has chosen, and the rule is complete.
    /// </para>
    /// </remarks>
    /// <param name="name">What to call the rule, or null to name it for what it does.</param>
    /// <param name="className">The window class, or null to leave it out.</param>
    /// <param name="processName">The process, or null to leave it out.</param>
    /// <param name="title">The title, offered commented-out as a third matcher.</param>
    /// <param name="does">The commands the rule runs, one per line, or none to leave the choice open.</param>
    /// <param name="processPath">
    /// The executable's path, offered commented-out beside the process. The matcher to
    /// reach for when two applications share a process name, as every Electron
    /// application on the machine does.
    /// </param>
    public static string Rule(
        string? name,
        string? className,
        string? processName,
        string? title = null,
        IReadOnlyList<string>? does = null,
        string? processPath = null)
    {
        var text = new StringBuilder();

        text.Append("rules {\n");
        text.Append("    rule \"").Append(Escape(Label(name, does, processName, className))).Append("\" {\n");
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

            // Beside the process matcher it would replace. Two matchers for the same
            // idea, both live, is a rule that is stricter than anybody meant - and
            // silently dropping the one that is nearly always right would be worse.
            if (processPath is { Length: > 0 })
                text.Append("            // path \"").Append(Escape(processPath)).Append("\"\n");
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

        if (does is { Count: > 0 })
        {
            foreach (string line in does)
                text.Append("            ").Append(line).Append('\n');
        }
        else
        {
            text.Append("            ").Append(Undecided).Append('\n');
        }

        text.Append("        }\n");
        text.Append("    }\n");
        text.Append('}');

        return text.ToString();
    }

    /// <summary>The same rule, written from a full report.</summary>
    /// <remarks>
    /// Separate because a report knows things a list row does not - the executable's
    /// path above all. Kept for callers that have a report and nothing to decide yet.
    /// </remarks>
    public static string RuleFromReport(
        string? className,
        string? processName,
        string? processPath,
        string? title) =>
        Rule(null, className, processName, title, does: null, processPath);

    /// <summary>
    /// A readable name for the rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Named for what it does to what: <c>rule "ignore msedge"</c>, <c>rule "manage
    /// WhatsApp"</c>, <c>rule "msedge on 2"</c>. A configuration with a dozen rules is
    /// read by their names, and a name that says only which application leaves the
    /// reader opening each one to find out which does what. With nothing decided yet
    /// the name is the application alone.
    /// </para>
    /// <para>
    /// The process without its extension, because <c>rule "msedge"</c> reads better
    /// than <c>rule "msedge.exe"</c> and is what somebody would have typed. Falls back
    /// through the class to a placeholder rather than producing <c>rule ""</c>, which
    /// the config loader would reject.
    /// </para>
    /// </remarks>
    private static string Label(string? name, IReadOnlyList<string>? does, string? processName, string? className)
    {
        if (name is { Length: > 0 }) return name;

        string subject = Subject(processName, className);

        if (does is not { Count: > 0 }) return subject;

        // The first verb names the rule; anything after it is detail. "manage" and
        // "float" together is a rule about managing, with a floating opinion.
        string first = does[0];

        if (first.StartsWith("move ", StringComparison.Ordinal))
        {
            // `move --workspace "2"` reads as `msedge on 2`.
            int quote = first.IndexOf('"');
            int close = quote >= 0 ? first.IndexOf('"', quote + 1) : -1;

            if (quote >= 0 && close > quote) return $"{subject} on {first[(quote + 1)..close]}";
        }

        int space = first.IndexOf(' ');
        string verb = space > 0 ? first[..space] : first;

        return $"{verb} {subject}";
    }

    /// <summary>The application, as a name reads it.</summary>
    private static string Subject(string? processName, string? className)
    {
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
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
