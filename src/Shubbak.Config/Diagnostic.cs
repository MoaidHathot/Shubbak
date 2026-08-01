using System.Globalization;

namespace Shubbak.Config;

/// <summary>
/// A position in a config file, for diagnostics.
/// </summary>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
/// <param name="Offset">0-based character offset.</param>
public readonly record struct TextPosition(int Line, int Column, int Offset)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Line}:{Column}");
}

/// <summary>A half-open range of characters in a config file.</summary>
public readonly record struct TextSpan(TextPosition Start, int Length)
{
    public int End => Start.Offset + Length;

    public override string ToString() => $"{Start}+{Length}";
}

/// <summary>How seriously to take a diagnostic.</summary>
public enum DiagnosticSeverity
{
    /// <summary>The config cannot be used.</summary>
    Error,

    /// <summary>
    /// The config loads, but something almost certainly does not do what was meant.
    /// </summary>
    Warning,
}

/// <summary>
/// A problem found while reading config.
/// </summary>
/// <remarks>
/// <para>
/// Diagnostics carry a span, so the CLI can render a caret under the exact
/// offending text. This is a direct response to how GlazeWM handles config errors:
/// the author's own file contains
/// <c>window_title: { regex: "/[Pp]ower[Pp]oint [Ss]lide [Ss]how.*/" }</c>, where
/// the slashes are literal characters rather than regex delimiters. That rule has
/// almost certainly never matched anything, and nothing reported it.
/// </para>
/// <para>
/// <see cref="Hint"/> exists for exactly that case: detecting a likely mistake is
/// only half the job, and telling the user what to write instead is the half that
/// saves them an hour.
/// </para>
/// </remarks>
/// <param name="Severity">Error or warning.</param>
/// <param name="Code">Stable identifier, e.g. <c>SHB0104</c>.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Span">Where it is wrong.</param>
/// <param name="Hint">What to do about it, when we can guess.</param>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    TextSpan Span,
    string? Hint = null)
{
    public static Diagnostic Error(string code, string message, TextSpan span, string? hint = null) =>
        new(DiagnosticSeverity.Error, code, message, span, hint);

    public static Diagnostic Warning(string code, string message, TextSpan span, string? hint = null) =>
        new(DiagnosticSeverity.Warning, code, message, span, hint);

    public override string ToString() =>
        $"{Span.Start}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";

    /// <summary>
    /// Renders the diagnostic with the offending line and a caret underneath.
    /// </summary>
    /// <param name="source">The full config text.</param>
    /// <param name="path">File path, for the header line.</param>
    public string Render(string source, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var output = new System.Text.StringBuilder();

        string location = path is null
            ? Span.Start.ToString()
            : $"{path}:{Span.Start}";

        output.Append(location).Append(": ")
              .Append(Severity.ToString().ToLowerInvariant()).Append(' ')
              .Append(Code).Append(": ").AppendLine(Message);

        string line = ExtractLine(source, Span.Start.Offset);
        if (line.Length > 0)
        {
            string gutter = Span.Start.Line.ToString(CultureInfo.InvariantCulture);
            output.Append("  ").Append(gutter).Append(" | ").AppendLine(line);

            // Tabs must be preserved in the caret row, or the marker drifts away
            // from what it is pointing at in any file that indents with tabs.
            var caret = new System.Text.StringBuilder();
            for (int i = 0; i < Span.Start.Column - 1 && i < line.Length; i++)
                caret.Append(line[i] == '\t' ? '\t' : ' ');

            caret.Append('^', Math.Max(1, Math.Min(Span.Length, Math.Max(1, line.Length - Span.Start.Column + 1))));

            output.Append("  ").Append(new string(' ', gutter.Length)).Append(" | ").AppendLine(caret.ToString());
        }

        if (Hint is not null) output.Append("  hint: ").AppendLine(Hint);

        return output.ToString();
    }

    private static string ExtractLine(string source, int offset)
    {
        if (offset < 0 || offset >= source.Length) return string.Empty;

        int start = source.LastIndexOf('\n', Math.Min(offset, source.Length - 1)) + 1;
        int end = source.IndexOf('\n', start);
        if (end < 0) end = source.Length;

        return source[start..end].TrimEnd('\r');
    }
}
