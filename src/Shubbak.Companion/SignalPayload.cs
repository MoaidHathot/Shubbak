using System.Text;
using System.Text.Json;

namespace Shubbak.Companion;

/// <summary>
/// What a <c>signal</c> event carries: a name, and the words after it.
/// </summary>
/// <param name="Name">Who the signal is for: <c>palette</c>, <c>ayn</c>.</param>
/// <param name="Arguments">Everything after the name, in order; empty when there was nothing.</param>
/// <remarks>
/// <para>
/// The window manager carries a signal without reading it, which is how the palette
/// and the watcher get verbs of their own without the window manager learning them.
/// Both parsed the same two fields by hand; this is the one copy. A payload that is
/// not a signal - not JSON, no name - is null rather than an exception, because the
/// stream it arrived on carries on either way.
/// </para>
/// <para>
/// Read with <see cref="Utf8JsonReader"/> rather than through a document. The reader
/// is in every companion already, underneath the generated serialiser; the document
/// was not, and pulling it into the bar for two fields cost fifty kilobytes of
/// binary, measured. It also allocates less: two fields and a list, with no tree in
/// between.
/// </para>
/// </remarks>
public sealed record SignalPayload(string Name, IReadOnlyList<string> Arguments)
{
    /// <summary>Reads a signal's payload, or null when it is not one.</summary>
    public static SignalPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;

            string? name = null;
            List<string>? arguments = null;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("name"))
                {
                    if (!reader.Read()) return null;
                    if (reader.TokenType != JsonTokenType.String) return null;

                    name = reader.GetString();
                }
                else if (reader.ValueTextEquals("arguments"))
                {
                    if (!reader.Read()) return null;

                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        // null, or something that is not a list: no arguments.
                        if (reader.TokenType is JsonTokenType.StartObject) reader.Skip();
                        continue;
                    }

                    arguments = [];

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        arguments.Add(Word(ref reader, bytes));
                }
                else
                {
                    reader.Skip();
                }
            }

            return name is { Length: > 0 } ? new SignalPayload(name, arguments ?? []) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One argument as a word. A string is itself; anything else is spelled as its
    /// JSON, so a number written bare in a binding still arrives as a word.
    /// </summary>
    private static string Word(ref Utf8JsonReader reader, byte[] bytes)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString() ?? string.Empty;

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            // The whole nested value, verbatim, from its first byte to past its last.
            int start = (int)reader.TokenStartIndex;
            reader.Skip();
            return Encoding.UTF8.GetString(bytes, start, (int)reader.BytesConsumed - start);
        }

        return Encoding.UTF8.GetString(reader.ValueSpan);
    }

    /// <summary>Whether this signal is addressed to <paramref name="name"/>, case-insensitively.</summary>
    public bool IsFor(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);
}
