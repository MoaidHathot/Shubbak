using System.Text;
using Shubbak.Config;

namespace Dalil.Core;

/// <summary>
/// How a macro's commands are written down and filled in, so that what reaches the
/// window manager's tokeniser is what the file meant.
/// </summary>
/// <remarks>
/// <para>
/// A macro is read from KDL as tokens - the node name and its arguments, quotes
/// already stripped - and sent over the pipe as text the window manager tokenises
/// again. Joining the tokens with spaces, which is what happened, destroyed exactly
/// the arguments the KDL quotes had protected: a workspace called <c>Second Monitor</c>
/// became two tokens, and one called <c>'</c> opened a quotation that never closed.
/// The validation ran on the tokens and passed; the send ran on the string and
/// failed. <see cref="Wire"/> writes each token the way the tokeniser reads it back.
/// </para>
/// <para>
/// A placeholder - <c>{ws}</c> - is answered when the row is chosen, and the answer
/// has the same problem. <see cref="Fill"/> quotes it unless the placeholder already
/// sits inside quotes, which it does when the file wrote <c>shell-exec "code {path}"</c>,
/// where a quoted answer would close and reopen the quotation around itself.
/// </para>
/// </remarks>
public static class MacroText
{
    /// <summary>
    /// The command as it goes over the pipe: each token spelled so the tokeniser reads
    /// it back whole, placeholders left bare so <see cref="Fill"/> can spell their
    /// answers.
    /// </summary>
    public static string Wire(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var wire = new StringBuilder();

        foreach (string token in tokens)
        {
            if (wire.Length > 0) wire.Append(' ');

            // A token that is nothing but a placeholder is spelled by its answer. One
            // that mixes a placeholder with text is quoted whole if it needs to be, and
            // Fill sees the quotes and answers inside them.
            if (IsPlaceholder(token) || !CommandParser.CanQuote(token))
                wire.Append(token);
            else
                wire.Append(CommandParser.Quote(token));
        }

        return wire.ToString();
    }

    /// <summary>
    /// Puts the answers into a command's placeholders.
    /// </summary>
    /// <param name="command">The command as <see cref="Wire"/> wrote it.</param>
    /// <param name="answers">What was chosen for each placeholder.</param>
    /// <param name="quoted">
    /// Whether the value is spelled for the window manager's tokeniser - quoted where
    /// it needs to be, and left bare where the placeholder is already inside quotes.
    /// Off for the text a row shows, where the quotes would only be noise.
    /// </param>
    public static string Fill(string command, IReadOnlyDictionary<string, string> answers, bool quoted)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(answers);

        if (answers.Count == 0 || !command.Contains('{')) return command;

        var filled = new StringBuilder(command.Length + 16);
        char quote = '\0';

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];

            if (c == '{' && command.IndexOf('}', i + 1) is var close and > 0 &&
                answers.TryGetValue(command[(i + 1)..close], out string? value))
            {
                filled.Append(quoted && quote == '\0' && CommandParser.CanQuote(value) ? CommandParser.Quote(value) : value);
                i = close;
                continue;
            }

            // The same quote rule as the tokeniser: a quote character opens a run in
            // which nothing else is special, and the same character closes it.
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }

            filled.Append(c);
        }

        return filled.ToString();
    }

    /// <summary>Whether a token is exactly one placeholder: <c>{name}</c> and nothing else.</summary>
    private static bool IsPlaceholder(string token) =>
        token.Length > 2 && token[0] == '{' && token[^1] == '}' && token.IndexOf('}') == token.Length - 1;
}
