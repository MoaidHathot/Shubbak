using System.Globalization;
using System.Text;
using Shubbak.Ipc;

namespace Shubbak.Cli;

/// <summary>
/// Printing why each context is the way it is.
/// </summary>
/// <remarks>
/// The first question anybody asks a context is "why is it on" or "why is it not", and
/// the answer is in the conditions: which block holds, which does not, and what each
/// condition saw when it was asked. So every condition is printed with a mark and its
/// detail, the way <c>inspect</c> prints every rule with whether it matched.
/// </remarks>
internal static class ContextReportText
{
    public static string Format(IReadOnlyList<ContextReport> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        if (contexts.Count == 0)
        {
            return "no contexts declared" + Environment.NewLine +
                   "hint: contexts { context \"presenting\" { when { window app=\"slides\" } } }" + Environment.NewLine;
        }

        var text = new StringBuilder();

        for (int i = 0; i < contexts.Count; i++)
        {
            if (i > 0) text.AppendLine();
            Describe(text, contexts[i]);
        }

        return text.ToString();
    }

    private static void Describe(StringBuilder text, ContextReport context)
    {
        text.Append(context.Active ? "* " : "  ")
            .Append(context.Name)
            .Append(context.Active ? "  active" : "  inactive");

        if (context.External) text.Append(", external");

        text.Append(CultureInfo.InvariantCulture, $"  ({context.Source}: {context.Reason})");
        text.AppendLine();

        if (context.Pin is { } pin)
        {
            text.Append(CultureInfo.InvariantCulture, $"     pinned {pin} by {context.SetBy ?? "someone"}");
            if (context.SetAgoMs is { } ago) text.Append(CultureInfo.InvariantCulture, $" {Seconds(ago)} ago");
            if (context.ExpiresInMs is { } left) text.Append(CultureInfo.InvariantCulture, $", expires in {Seconds(left)}");
            if (context.Leased) text.Append(", leased to that connection");
            text.AppendLine();
        }

        if (context.LingerRemainingMs is { } linger)
            text.Append(CultureInfo.InvariantCulture, $"     lingering, lets go in {linger} ms").AppendLine();

        for (int b = 0; b < context.When.Count; b++)
        {
            WhenReport block = context.When[b];

            text.Append(CultureInfo.InvariantCulture, $"     when {(block.Holds ? "holds" : "does not hold")}")
                .Append(context.When.Count > 1 ? $" ({b + 1} of {context.When.Count})" : "")
                .AppendLine();

            foreach (ConditionReport condition in block.Conditions)
            {
                text.Append(condition.Holds ? "       [x] " : "       [ ] ")
                    .Append(condition.Text);

                if (condition.Detail.Length > 0)
                    text.Append("  - ").Append(condition.Detail);

                text.AppendLine();
            }
        }

        if (context.Effects.Count > 0)
            text.Append("     changes: ").AppendLine(string.Join(", ", context.Effects));
        else if (!context.External)
            text.AppendLine("     changes nothing; a flag for the bar, the palette and scripts");
    }

    private static string Seconds(long milliseconds) =>
        milliseconds < 1000
            ? $"{milliseconds} ms"
            : $"{milliseconds / 1000.0:0.#} s";
}
