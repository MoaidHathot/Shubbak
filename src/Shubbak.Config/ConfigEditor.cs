using System.Text;
using Shubbak.Config.Kdl;
using Shubbak.Core.Commands;

namespace Shubbak.Config;

/// <summary>What an edit would do to the file, before anything is written.</summary>
/// <param name="Text">
/// The file as it would read afterwards - or, for a refusal that carries diagnostics,
/// the text those diagnostics point into, so they can be rendered with their carets.
/// </param>
/// <param name="Line">Where the rule concerned begins in that text, 1-based.</param>
/// <param name="Names">The rules added or removed, as a report would name them.</param>
/// <param name="RuleText">
/// The rule as it stood in the file, without its surrounding indentation. For a
/// removal this is what a "put it back" needs; for an addition it is what was added.
/// </param>
/// <param name="Refusal">Why the edit will not be made, or null when it will.</param>
/// <param name="Diagnostics">
/// What the loader said about the text that decided the refusal, when a refusal has
/// something to show. Empty otherwise.
/// </param>
public sealed record ConfigEdit(
    string Text,
    int Line,
    IReadOnlyList<string> Names,
    string RuleText,
    string? Refusal,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Whether the edit can go ahead.</summary>
    public bool Accepted => Refusal is null;

    internal static ConfigEdit Refused(string reason, IReadOnlyList<Diagnostic>? diagnostics = null, string? about = null) =>
        new(about ?? string.Empty, 0, [], string.Empty, reason, diagnostics ?? []);
}

/// <summary>
/// Edits the text of a configuration file without rewriting the parts it is not
/// touching.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is a pure function of text. The file is somebody's hand-written
/// configuration, with their comments, their spacing and their line endings, and the
/// one thing an editing tool must not do to it is reformat it. So nothing is
/// re-serialised: a rule is added by appending lines and removed by cutting the lines
/// it occupies, and the rest of the file is the same bytes it was.
/// </para>
/// <para>
/// Every edit is validated by loading the result through the same loader the window
/// manager uses, and refused rather than written when the loader would reject it or
/// quietly drop the rule. A tool that left the file in a state the window manager
/// refuses to load would be worse than one that did nothing - the reload would keep
/// the old configuration and the edit would look like it had silently failed.
/// </para>
/// <para>
/// In this assembly rather than beside the window manager so the exact bytes can be
/// tested against a string, with no file, no daemon and no desktop.
/// </para>
/// </remarks>
public static class ConfigEditor
{
    /// <summary>
    /// The comment written above a block this editor added, so the block can be
    /// recognised as one of its own and taken out with the rule when the rule goes.
    /// </summary>
    public const string Marker = "// Added by Shubbak";

    /// <summary>
    /// Plans adding one or more rules to the end of the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Appended as a new <c>rules { }</c> block rather than inserted into an existing
    /// one. Inserting would mean finding the closing brace of a block somebody else
    /// wrote and deciding how they indent, and getting either slightly wrong in a file
    /// they maintain by hand; appending adds lines and touches nothing. The loader
    /// reads every <c>rules</c> block, so a second one is simply more rules.
    /// </para>
    /// <para>
    /// The block is what the caller read, byte for byte apart from line endings, so a
    /// rule that was shown before it was added is the rule that lands in the file.
    /// </para>
    /// </remarks>
    /// <param name="existing">The file as it is.</param>
    /// <param name="block">
    /// The rules to add: a <c>rules { }</c> block, or bare <c>rule</c> nodes, which are
    /// wrapped in one. Anything else is refused - this adds rules and nothing else.
    /// </param>
    /// <param name="allowShellExec">
    /// Whether a rule may run <c>shell-exec</c>. A rule is one more way to have the
    /// window manager start a program, so a caller that is refused the command directly
    /// must be refused it here too.
    /// </param>
    /// <param name="marker">
    /// A comment written above the block, or null for none. <see cref="Marker"/> and
    /// a note on who added it, usually.
    /// </param>
    public static ConfigEdit PlanAddition(string existing, string block, bool allowShellExec, string? marker)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(block);

        if (block.AsSpan().Trim().Length == 0) return ConfigEdit.Refused("There is no rule to add.");

        KdlParseResult parsed = KdlParser.Parse(block);

        if (parsed.HasErrors)
            return ConfigEdit.Refused("The rule does not parse.", parsed.Diagnostics, about: block);

        // Only rules. A block of anything else - `general { allow-shell-exec-over-ipc
        // #true }`, say - would be appended and would take effect, and the pipe this
        // arrives over is open to every process running as the user.
        bool bare = false;

        foreach (KdlNode node in parsed.Document.Nodes)
        {
            if (node.Name is "rule")
            {
                bare = true;
                continue;
            }

            if (node.Name is not "rules")
                return ConfigEdit.Refused($"Only rules can be added this way; the block contains '{node.Name}'.");

            foreach (KdlNode child in node.Children)
            {
                if (child.Name is not "rule")
                    return ConfigEdit.Refused($"Only rules can be added this way; the rules block contains '{child.Name}'.");
            }
        }

        if (!parsed.Document.Nodes.Any(n => n.Name is "rule" || n.Children.Count > 0))
            return ConfigEdit.Refused("There is no rule to add.");

        string newline = NewlineOf(existing);
        string body = Normalise(bare ? Wrap(block) : block, newline).TrimEnd();

        var text = new StringBuilder(existing.Length + body.Length + 64);
        text.Append(existing);

        // One blank line between what was there and what is added, and a newline to
        // end the file on - unless the file did not end on one, in which case the
        // addition still has to start on a line of its own.
        if (existing.Length > 0)
        {
            if (!existing.EndsWith('\n')) text.Append(newline);
            if (!EndsWithBlankLine(existing)) text.Append(newline);
        }

        if (marker is { Length: > 0 }) text.Append(marker).Append(newline);

        text.Append(body).Append(newline);

        string result = text.ToString();

        // Validated as the window manager would load it, before and after. A file
        // that already has errors would have the reload refused whatever is added, and
        // saying so is better than adding a rule that then appears to do nothing.
        ConfigLoadResult before = ConfigLoader.Load(existing);

        if (before.HasErrors)
        {
            return ConfigEdit.Refused(
                "The configuration already has errors, so a reload would be refused. Fix these first.",
                [.. before.Errors],
                about: existing);
        }

        ConfigLoadResult after = ConfigLoader.Load(result);

        if (after.HasErrors)
            return ConfigEdit.Refused("The rule would leave the configuration with errors.", [.. after.Errors], about: result);

        // The loader drops a rule it does not think worth keeping - one with no
        // conditions, one with an empty do block - with a warning. Every block is read
        // in file order and this one is last, so what was added is whatever follows
        // what was there.
        int had = before.Config.Rules.Count;

        if (after.Config.Rules.Count <= had)
        {
            return ConfigEdit.Refused(
                "The loader would drop the rule, so adding it would change nothing.",
                [.. after.Diagnostics.Where(d => d.Code is "SHB0417" or "SHB0418" or "SHB0452")],
                about: result);
        }

        List<WindowRule> added = [.. after.Config.Rules.Skip(had)];

        if (!allowShellExec && added.Any(r => r.Commands.Any(c => c is ShellExecCommand)))
        {
            return ConfigEdit.Refused(
                "The rule runs shell-exec, which is not accepted over the pipe. Set " +
                "general { allow-shell-exec-over-ipc #true } to permit it, or add the rule by hand.");
        }

        return new ConfigEdit(
            result,
            added[0].Span.Start.Line,
            [.. added.Select(r => r.Name)],
            body,
            Refusal: null,
            Diagnostics: []);
    }

    /// <summary>
    /// Plans removing one rule, named and placed as a report described it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both the name and the line have to agree. A report is a snapshot, and the file
    /// may have been edited since; deleting whatever now sits at that line, or the
    /// first rule that happens to share the name, is how a tool destroys a
    /// configuration. When the two disagree, nothing is removed and the answer says to
    /// look again.
    /// </para>
    /// <para>
    /// The rule's lines go, and so does the block around it when the rule was all the
    /// block held, and so does the marker this editor wrote above such a block - so
    /// that adding a rule and removing it again leaves the file as it was found.
    /// </para>
    /// </remarks>
    /// <param name="existing">The file as it is.</param>
    /// <param name="name">The rule's name, as a report gave it.</param>
    /// <param name="line">The line the rule begins on, as a report gave it.</param>
    public static ConfigEdit PlanRemoval(string existing, string name, int line)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(name);

        KdlParseResult parsed = KdlParser.Parse(existing);

        if (parsed.HasErrors)
            return ConfigEdit.Refused("The configuration does not parse, so nothing in it can be removed safely.", parsed.Diagnostics, about: existing);

        (KdlNode Rule, KdlNode Block, string Name)? found = null;

        foreach ((KdlNode rule, KdlNode block, string actual) in Rules(parsed.Document))
        {
            if (rule.Span.Start.Line != line) continue;

            found = (rule, block, actual);
            break;
        }

        if (found is null)
            return ConfigEdit.Refused($"No rule begins at line {line}. The file may have changed since the report - inspect the window again.");

        (KdlNode target, KdlNode parent, string actualName) = found.Value;

        if (!string.Equals(actualName, name, StringComparison.Ordinal))
        {
            return ConfigEdit.Refused(
                $"The rule at line {line} is called '{actualName}', not '{name}'. " +
                "The file may have changed since the report - inspect the window again.");
        }

        string ruleText = Dedent(existing.AsSpan(target.Span.Start.Offset, target.Span.Length).ToString());

        // The rule's own lines, or the whole block's when the rule was all it held.
        (int start, int end) = LinesAround(existing, target.Span.Start.Offset, target.Span.End);

        bool onlyChild = parent.Children.Count == 1;

        if (onlyChild && InteriorIsBlank(existing, parent, target))
        {
            (start, end) = LinesAround(existing, parent.Span.Start.Offset, parent.Span.End);

            // The marker sits on the line above a block this editor added.
            int markerStart = StartOfPreviousLine(existing, start);

            if (markerStart >= 0 && existing.AsSpan(markerStart, start - markerStart).Trim().StartsWith(Marker, StringComparison.Ordinal))
                start = markerStart;
        }

        string result = Cut(existing, start, end);

        ConfigLoadResult after = ConfigLoader.Load(result);

        if (after.HasErrors)
        {
            return ConfigEdit.Refused(
                "The configuration has errors, so a reload would be refused. Fix these first.",
                [.. after.Errors],
                about: result);
        }

        return new ConfigEdit(result, line, [actualName], ruleText, Refusal: null, Diagnostics: []);
    }

    /// <summary>
    /// Every rule in the document, with the block it sits in and the name a report
    /// would give it.
    /// </summary>
    /// <remarks>
    /// Mirrors the loader's numbering exactly: unnamed rules count across every
    /// top-level block, and start again inside each context. The name a report shows
    /// for an unnamed rule is the one the loader made up, and a removal by that name
    /// has to make up the same one.
    /// </remarks>
    private static IEnumerable<(KdlNode Rule, KdlNode Block, string Name)> Rules(KdlDocument document)
    {
        int ordinal = 0;

        foreach (KdlNode block in document.NodesNamed("rules"))
        {
            foreach (KdlNode rule in block.ChildrenNamed("rule"))
            {
                ordinal++;
                yield return (rule, block, rule.Argument(0)?.AsString() ?? ConfigLoader.DefaultRuleName(ordinal));
            }
        }

        if (document.Node("contexts") is not { } contexts) yield break;

        foreach (KdlNode context in contexts.ChildrenNamed("context"))
        {
            int inContext = 0;

            foreach (KdlNode block in context.ChildrenNamed("rules"))
            {
                foreach (KdlNode rule in block.ChildrenNamed("rule"))
                {
                    inContext++;
                    yield return (rule, block, rule.Argument(0)?.AsString() ?? ConfigLoader.DefaultRuleName(inContext));
                }
            }
        }
    }

    /// <summary>The line ending the file uses, read off its first line break.</summary>
    public static string NewlineOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int index = text.IndexOf('\n');

        return index > 0 && text[index - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>Rewrites a block's line endings to the file's.</summary>
    private static string Normalise(string block, string newline)
    {
        string unix = block.Replace("\r\n", "\n", StringComparison.Ordinal);

        return newline == "\n" ? unix : unix.Replace("\n", newline, StringComparison.Ordinal);
    }

    /// <summary>Puts bare <c>rule</c> nodes inside a <c>rules { }</c> block.</summary>
    private static string Wrap(string rules)
    {
        var text = new StringBuilder(rules.Length + 32);
        text.Append("rules {\n");

        foreach (string line in rules.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0)
            {
                text.Append('\n');
                continue;
            }

            text.Append("    ").Append(line).Append('\n');
        }

        text.Append('}');

        return text.ToString();
    }

    private static bool EndsWithBlankLine(string text)
    {
        // The file ends "...\n\n" or "...\r\n\r\n": a line with nothing on it.
        if (text.EndsWith("\n\n", StringComparison.Ordinal)) return true;

        return text.EndsWith("\r\n\r\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// The range of whole lines a span occupies: from the start of its first line to
    /// just past the end of its last, terminator included.
    /// </summary>
    /// <remarks>
    /// Whole lines only when the span is alone on them. A rule written on one line
    /// beside another node - <c>rule "a" { ... }; rule "b" { ... }</c> - loses just its
    /// own characters and the separator after it, and the rest of the line stays.
    /// </remarks>
    private static (int Start, int End) LinesAround(string text, int spanStart, int spanEnd)
    {
        int lineStart = spanStart;
        while (lineStart > 0 && text[lineStart - 1] != '\n') lineStart--;

        int lineEnd = spanEnd;
        while (lineEnd < text.Length && text[lineEnd] != '\n') lineEnd++;

        // Past the terminator, so the line goes with its contents.
        int afterLine = lineEnd < text.Length ? lineEnd + 1 : lineEnd;

        bool aloneBefore = text.AsSpan(lineStart, spanStart - lineStart).Trim().Length == 0;
        bool aloneAfter = text.AsSpan(spanEnd, lineEnd - spanEnd).Trim(" \t\r;".AsSpan()).Length == 0;

        if (aloneBefore && aloneAfter) return (lineStart, afterLine);

        // Shared line: just the node, and a separator directly after it.
        int end = spanEnd;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        if (end < text.Length && text[end] == ';') end++;

        return (spanStart, end);
    }

    /// <summary>Whether a block holds nothing but whitespace once one child is taken out.</summary>
    /// <remarks>
    /// A comment left inside the block is content: it was written by somebody and the
    /// block stays to hold it.
    /// </remarks>
    private static bool InteriorIsBlank(string text, KdlNode block, KdlNode except)
    {
        int open = text.IndexOf('{', block.NameSpan.End);
        int close = text.LastIndexOf('}', block.Span.End - 1);

        if (open < 0 || close <= open) return false;

        ReadOnlySpan<char> before = text.AsSpan(open + 1, except.Span.Start.Offset - open - 1);
        ReadOnlySpan<char> after = text.AsSpan(except.Span.End, close - except.Span.End);

        return before.Trim().Length == 0 && after.Trim(" \t\r\n;".AsSpan()).Length == 0;
    }

    /// <summary>The offset the previous line begins at, or -1 at the top of the file.</summary>
    private static int StartOfPreviousLine(string text, int lineStart)
    {
        if (lineStart == 0) return -1;

        int end = lineStart - 1;
        if (end > 0 && text[end] == '\n') end--;

        // end now sits on the last character of the previous line, or its '\r'.
        int start = end;
        while (start > 0 && text[start - 1] != '\n') start--;

        return start;
    }

    /// <summary>
    /// Removes a range of whole lines, and the blank line that would otherwise be left
    /// twice.
    /// </summary>
    /// <remarks>
    /// A block was written with a blank line above it. Cutting the block leaves that
    /// blank line and whatever blank line followed the block sitting together, so two
    /// become one. At the end of the file the trailing newline stays exactly as it
    /// was: one if there was one, none if there was none.
    /// </remarks>
    private static string Cut(string text, int start, int end)
    {
        string newline = NewlineOf(text);
        bool hadTrailingNewline = text.EndsWith('\n');

        string head = text[..start];
        string tail = text[end..];

        bool blankAbove = head.Length == 0 || EndsWithBlankLine(head) || head == newline;
        bool blankBelow = tail.StartsWith(newline, StringComparison.Ordinal);

        if (blankAbove && blankBelow) tail = tail[newline.Length..];

        string result = head + tail;

        if (tail.AsSpan().Trim().Length == 0)
        {
            // Removed from the end: whatever blank lines were around the block are
            // gone with it, and the file ends the way it did.
            result = result.TrimEnd('\r', '\n');
            if (hadTrailingNewline && result.Length > 0) result += newline;
        }

        return result;
    }

    /// <summary>Strips the common leading whitespace from every line.</summary>
    private static string Dedent(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        int common = int.MaxValue;

        // The first line starts at the node, so its indentation is whatever preceded it
        // on the line and is not part of the text. Measured from the second line on.
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.AsSpan().Trim().Length == 0) continue;

            int indent = 0;
            while (indent < line.Length && line[indent] is ' ' or '\t') indent++;

            common = Math.Min(common, indent);
        }

        if (common is 0 or int.MaxValue) return string.Join('\n', lines);

        for (int i = 1; i < lines.Length; i++)
            lines[i] = lines[i].Length >= common ? lines[i][common..] : lines[i].TrimStart();

        return string.Join('\n', lines);
    }
}
