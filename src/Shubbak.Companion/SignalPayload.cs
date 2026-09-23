using System.Text.Json;

namespace Shubbak.Companion;

/// <summary>
/// What a <c>signal</c> event carries: a name, and the words after it.
/// </summary>
/// <param name="Name">Who the signal is for: <c>palette</c>, <c>ayn</c>.</param>
/// <param name="Arguments">Everything after the name, in order; empty when there was nothing.</param>
/// <remarks>
/// The window manager carries a signal without reading it, which is how the palette
/// and the watcher get verbs of their own without the window manager learning them.
/// Both parsed the same two fields by hand; this is the one copy. A payload that is
/// not a signal - not JSON, no name - is null rather than an exception, because the
/// stream it arrived on carries on either way.
/// </remarks>
public sealed record SignalPayload(string Name, IReadOnlyList<string> Arguments)
{
    /// <summary>Reads a signal's payload, or null when it is not one.</summary>
    public static SignalPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (!document.RootElement.TryGetProperty("name", out JsonElement nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                nameElement.GetString() is not { Length: > 0 } name)
            {
                return null;
            }

            List<string> arguments = [];

            if (document.RootElement.TryGetProperty("arguments", out JsonElement list) &&
                list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement argument in list.EnumerateArray())
                {
                    // Anything that is not a string is spelled as its JSON, so a number
                    // written bare in a binding still arrives as a word.
                    arguments.Add(argument.ValueKind == JsonValueKind.String
                        ? argument.GetString() ?? string.Empty
                        : argument.GetRawText());
                }
            }

            return new SignalPayload(name, arguments);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether this signal is addressed to <paramref name="name"/>, case-insensitively.</summary>
    public bool IsFor(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
}
