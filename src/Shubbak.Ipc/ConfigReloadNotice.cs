using System.Text;
using System.Text.Json;

namespace Shubbak.Ipc;

/// <summary>
/// The payload of <c>config.reloaded</c>: which file the window manager re-read, and
/// whether it is now running what the file says.
/// </summary>
/// <param name="Path">The file that was read, or null when the window manager runs on defaults, or when an older window manager did not say.</param>
/// <param name="Accepted">Whether the file is now the configuration in force. True unless the payload plainly says otherwise.</param>
/// <remarks>
/// <para>
/// One place for both ends, in the manner of <see cref="ShutdownNotice"/>. The event
/// went out as <c>{}</c> for every release before this, and every program reading the
/// same file took it as "the file moved; read it again" - which was right for the
/// reload that landed and wrong for the one the window manager refused. A palette that
/// re-reads a file the window manager would not is then running on settings the
/// window manager is not, and is the only one that does not know. Carrying the outcome
/// lets it stay on what it had, as the window manager did.
/// </para>
/// <para>
/// Read with <see cref="Utf8JsonReader"/> rather than a document, for the reason the
/// signal payload is: the reader is in every client already, under the generated
/// serialiser, and a document is fifty kilobytes of binary for two fields. Absent
/// fields read as they always did - no path, and accepted - so a payload from an older
/// window manager means what <c>{}</c> always meant.
/// </para>
/// </remarks>
public sealed record ConfigReloadNotice(string? Path, bool Accepted)
{
    /// <summary>The property naming the file.</summary>
    public const string PathProperty = "path";

    /// <summary>The property saying whether the window manager took the file.</summary>
    public const string AcceptedProperty = "accepted";

    /// <summary>What an older window manager sent, and what a notice with nothing to say reads as.</summary>
    public static ConfigReloadNotice Empty { get; } = new(null, true);

    /// <summary>The payload for a reload of the given file and outcome.</summary>
    public static string Payload(string? path, bool accepted) =>
        $"{{\"{PathProperty}\":{(path is null ? "null" : JsonSerializer.Serialize(path, IpcJsonContext.Default.String))}," +
        $"\"{AcceptedProperty}\":{(accepted ? "true" : "false")}}}";

    /// <summary>
    /// Reads a reload's payload. Never null: anything that is not a notice reads as
    /// <see cref="Empty"/>, because that is what every such payload used to be.
    /// </summary>
    public static ConfigReloadNotice Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return Empty;

            string? path = null;
            bool accepted = true;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals(PathProperty))
                {
                    if (!reader.Read()) return Empty;
                    if (reader.TokenType == JsonTokenType.String) path = reader.GetString();
                    else reader.Skip();
                }
                else if (reader.ValueTextEquals(AcceptedProperty))
                {
                    if (!reader.Read()) return Empty;
                    if (reader.TokenType == JsonTokenType.False) accepted = false;
                    else reader.Skip();
                }
                else
                {
                    reader.Skip();
                }
            }

            return path is null && accepted ? Empty : new ConfigReloadNotice(path, accepted);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}
