using Shubbak.Config;
using Shubbak.Core.Commands;

namespace Dalil.Core;

/// <summary>What a verb's arguments can be completed from.</summary>
/// <param name="Workspaces">Names, for the workspace arguments.</param>
/// <param name="Layouts">Names, for the layout arguments.</param>
/// <param name="BindingModes">Names declared in the configuration.</param>
/// <param name="ScratchpadSlots">Slots currently holding a window.</param>
public sealed record CompletionSources(
    IReadOnlyList<string> Workspaces,
    IReadOnlyList<string> Layouts,
    IReadOnlyList<string> BindingModes,
    IReadOnlyList<string> ScratchpadSlots)
{
    public static CompletionSources None { get; } = new([], [], [], []);
}

/// <summary>
/// Turns what has been typed in commands mode into something that can be run.
/// </summary>
/// <remarks>
/// <para>
/// Commands that take arguments were unreachable. Choosing <c>resize</c> put
/// <c>resize </c> in the box, the verb list then matched nothing - the term is longer
/// than any verb - and the list emptied, so Enter had nothing to act on. Twelve of the
/// thirty verbs were affected, which is every one that takes an argument.
/// </para>
/// <para>
/// The fix is not to special-case Enter but to make the typed text a row like any
/// other. It sits at the top, the verb list stays underneath so the verb can still be
/// changed mid-edit, and Enter acts on whatever is selected exactly as it always did.
/// </para>
/// <para>
/// The parsing is the real <see cref="CommandParser"/>, not a second implementation.
/// Dalil already depends on it for its own configuration, so a command that will be
/// refused can be refused here - with the same message and the same hint the config
/// file would give - before it is ever sent. That is the difference between a palette
/// that says "unknown direction 'lft'. Try left, right, up or down" as you type and
/// one that silently does nothing.
/// </para>
/// </remarks>
public static class CommandComposer
{
    /// <summary>Directions, which are the same four everywhere and need no source.</summary>
    private static readonly string[] Directions = ["left", "right", "up", "down"];

    /// <summary>
    /// Rows to show above the verb list for the current term.
    /// </summary>
    /// <param name="term">What has been typed, with the mode prefix removed.</param>
    /// <param name="sources">Values the arguments can be completed from.</param>
    /// <remarks>
    /// Empty until a verb has been followed by something. A term that is still just a
    /// prefix of a verb is the user narrowing the list, and offering to run
    /// <c>resi</c> while they are halfway through typing <c>resize</c> would be noise
    /// at best and, if they pressed Enter, wrong.
    /// </remarks>
    public static IReadOnlyList<PaletteEntry> Compose(string term, CompletionSources? sources = null)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];

        // A bare word is a search, not a command. The verb list is already showing
        // exactly what it matches - but a trailing space means the user has finished
        // naming a verb and started on its argument, which is precisely the case a
        // single-argument verb like "scratchpad " arrives in.
        if (!term.Contains(' ', StringComparison.Ordinal)) return [];

        string text = term.Trim();
        var span = new TextSpan(new TextPosition(1, 1, 0), text.Length);

        List<PaletteEntry> rows = [];

        if (CommandParser.TryParse(text, span, out WmCommand? parsed, out Diagnostic? error))
        {
            rows.Add(new PaletteEntry(
                text,
                $"run {parsed!.Name}",
                ["\u21B5 run"],
                text,

                // Above everything. A window focused seconds ago carries a high
                // recency and would otherwise outrank the thing the user is in the
                // middle of typing.
                Rank: long.MaxValue));
        }
        else
        {
            string message = error!.Message;
            string? hint = error.Hint;

            rows.Add(new PaletteEntry(
                text,

                // The parser's own words. Writing a friendlier message here would mean
                // two vocabularies for the same mistake - one when it is typed and a
                // different one when the same text is put in a config file.
                hint is null ? message : $"{message}  {hint}",
                ["cannot run"],

                // Nothing to run. Enter on this row does nothing rather than sending
                // something the window manager will only reject again.
                string.Empty,
                Rank: long.MaxValue));
        }

        rows.AddRange(Complete(term, sources ?? CompletionSources.None));

        return rows;
    }

    /// <summary>
    /// Offers values for the argument being typed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven by the argument kinds in <see cref="CommandCatalogue"/>, which existed
    /// for exactly this and until now were read by nothing at all.
    /// </para>
    /// <para>
    /// Only ever appended to what has already been typed, never rewritten. The user
    /// is mid-sentence, and a completion that reorders or reformats the rest of the
    /// line is one nobody can predict.
    /// </para>
    /// </remarks>
    private static IEnumerable<PaletteEntry> Complete(string term, CompletionSources sources)
    {
        string[] tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) yield break;

        if (CommandCatalogue.Find(tokens[0]) is not { Arguments.Count: > 0 } spec) yield break;

        // A trailing space means the argument has not been started; otherwise the last
        // token is a partial value to filter by.
        bool started = !term.EndsWith(' ');
        string partial = started ? tokens[^1] : string.Empty;

        // Flags are not values. "focus --" is choosing which argument to give, not
        // typing one, and completing workspace names into it would be nonsense.
        if (partial.StartsWith('-')) yield break;

        ReadOnlySpan<string> before = tokens.AsSpan(1, Math.Max(0, tokens.Length - 1 - (started ? 1 : 0)));

        // The flag decides, when there is one. The catalogue lists the argument kinds
        // a verb takes, but several verbs accept alternatives the list cannot express:
        // "move" is catalogued as taking a direction and the parser also accepts
        // "move --workspace 3". Going by position alone offered directions for a
        // workspace argument, which is confidently wrong rather than merely unhelpful.
        CommandArgument? kind = FlagKind(before, spec);

        if (kind is null)
        {
            int supplied = 0;
            foreach (string token in before)
                if (!token.StartsWith('-'))
                    supplied++;

            if (supplied >= spec.Arguments.Count) yield break;
            kind = spec.Arguments[supplied];
        }

        IReadOnlyList<string> values = Values(kind.Value, sources);
        if (values.Count == 0) yield break;

        string head = started ? term[..^partial.Length] : term;

        foreach (string value in values)
        {
            if (partial.Length > 0 &&
                !value.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string composed = (head + value).Trim();

            // A completion that spells exactly what is already typed is a duplicate of
            // the run row directly above it.
            if (string.Equals(composed, term.Trim(), StringComparison.Ordinal)) continue;

            yield return new PaletteEntry(
                composed,
                $"complete with {value}",
                ["\u21B5 run"],
                composed,

                // Below the run row and above everything filtered, so the thing being
                // typed always stays first.
                Rank: long.MaxValue - 1);
        }
    }

    /// <summary>The argument kind the most recent flag asks for, if it names one.</summary>
    /// <remarks>
    /// Only the flags that select between value kinds. <c>--width</c> and
    /// <c>--height</c> pick an axis but their value is a number, which has nothing
    /// finite to offer, so they are left to fall through.
    /// </remarks>
    private static CommandArgument? FlagKind(ReadOnlySpan<string> before, CommandSpec spec)
    {
        for (int i = before.Length - 1; i >= 0; i--)
        {
            if (!before[i].StartsWith("--", StringComparison.Ordinal)) continue;

            return before[i].ToLowerInvariant() switch
            {
                "--workspace" => CommandArgument.WorkspaceName,
                "--direction" => CommandArgument.Direction,
                "--name" => CommandArgument.BindingMode,

                // Whatever this verb's own argument is: --set means "the value" for
                // layout and for split alike.
                "--set" => spec.Arguments[0],
                _ => null,
            };
        }

        return null;
    }

    private static IReadOnlyList<string> Values(CommandArgument argument, CompletionSources sources) =>
        argument switch
        {
            CommandArgument.Direction => Directions,
            CommandArgument.WorkspaceName => sources.Workspaces,
            CommandArgument.LayoutName => sources.Layouts,
            CommandArgument.BindingMode => sources.BindingModes,
            CommandArgument.ScratchpadSlot => sources.ScratchpadSlots,

            // An axis, an amount, a window handle, a signal name, a command line:
            // nothing finite to offer, and a guess would be worse than silence.
            _ => [],
        };
}
