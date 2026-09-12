using System.Text.Json;

namespace Shubbak.Ipc;

/// <summary>
/// The payload of <see cref="IpcProtocol.ShutdownTopic"/>: whether the window manager
/// is leaving alone, or taking everything with it.
/// </summary>
/// <remarks>
/// <para>
/// One place for both ends, because the two readings of a shutdown are opposites and
/// a bar, a palette and a watcher each decide alone. After <c>wm-exit</c> the palette
/// and the watcher stay and reconnect - a restarted window manager without a palette
/// is what that avoids - and the bar waits a while for the same reason. After
/// <c>exit-all</c> all of them go: the user is done with Shubbak, and a palette
/// waiting for a window manager that is not coming back is the thing <em>that</em>
/// avoids. Only the window manager knows which was asked, so it says.
/// </para>
/// <para>
/// One optional boolean, hand-written and hand-read, in the manner of the signal
/// payload. A message that was <c>{}</c> for every release before this stays
/// readable by every client that ever read it: absent means alone, which is what
/// <c>{}</c> always meant.
/// </para>
/// </remarks>
public static class ShutdownNotice
{
    /// <summary>The property that says everything is leaving.</summary>
    public const string EverythingProperty = "everything";

    /// <summary>The window manager is leaving by itself.</summary>
    public const string Alone = "{}";

    /// <summary>The window manager is leaving and the rest should go with it.</summary>
    public const string Everything = "{\"" + EverythingProperty + "\":true}";

    /// <summary>The payload for a shutdown of the given reach.</summary>
    public static string Payload(bool everything) => everything ? Everything : Alone;

    /// <summary>
    /// Whether a shutdown payload asks every program to leave.
    /// </summary>
    /// <remarks>
    /// False for anything that is not plainly <c>"everything": true</c> - an empty
    /// object, a payload from an older window manager, or one that does not parse.
    /// Staying is the safe misreading: a palette that stayed when it was asked to go
    /// is a stray process, and one that went when it was asked to stay is a desktop
    /// with no palette and nothing to say why.
    /// </remarks>
    public static bool IsForEveryone(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(EverythingProperty, out JsonElement everything)
                && everything.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
