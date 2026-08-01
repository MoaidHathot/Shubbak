using System.Text.Json.Serialization;

namespace Shubbak.Ipc;

/// <summary>
/// A request from a client to the window manager.
/// </summary>
/// <param name="Id">Correlates the response; echoed back unchanged.</param>
/// <param name="Method">What to do, e.g. <c>command</c>, <c>query</c>, <c>subscribe</c>.</param>
/// <param name="Payload">Method-specific argument.</param>
public sealed record IpcRequest(string Method, string? Payload = null, int Id = 0);

/// <summary>A reply to one request.</summary>
/// <param name="Id">The request's id.</param>
/// <param name="Ok">Whether the request succeeded.</param>
/// <param name="Data">Result, when there is one.</param>
/// <param name="Error">Why it failed.</param>
public sealed record IpcResponse(int Id, bool Ok, string? Data = null, string? Error = null);

/// <summary>
/// A pushed notification, sent to clients that subscribed.
/// </summary>
/// <param name="Topic">Event topic, e.g. <c>window.focused</c>.</param>
/// <param name="Data">Event payload as JSON.</param>
public sealed record IpcEvent(string Topic, string Data);

/// <summary>A window as described to clients.</summary>
public sealed record WindowInfo(
    long Id,
    long Handle,
    string Title,
    string ClassName,
    string ProcessName,
    string State,
    bool Focused,
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>A workspace as described to clients.</summary>
public sealed record WorkspaceInfo(
    long Id,
    string Name,
    string DisplayName,
    bool Active,
    bool HasWindows,
    string Monitor,
    string Layout,
    int WindowCount);

/// <summary>A monitor as described to clients.</summary>
public sealed record MonitorInfoDto(
    long Id,
    string DeviceId,
    bool Primary,
    uint Dpi,
    int X,
    int Y,
    int Width,
    int Height,
    string? ActiveWorkspace);

/// <summary>The whole state, for a bar that has just connected.</summary>
public sealed record StateSnapshot(
    IReadOnlyList<MonitorInfoDto> Monitors,
    IReadOnlyList<WorkspaceInfo> Workspaces,
    IReadOnlyList<WindowInfo> Windows,
    WindowInfo? FocusedWindow,
    string? BindingMode,
    bool Paused);

/// <summary>
/// Source-generated JSON serialisation for the IPC protocol.
/// </summary>
/// <remarks>
/// Source generation rather than reflection is required, not merely preferred:
/// ADR 0001 constraint 6 forbids reflection-based serialisation, because it is the
/// single most common cause of NativeAOT failures and would cost the "zero IL
/// warnings" property the whole project depends on.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(IpcEvent))]
[JsonSerializable(typeof(WindowInfo))]
[JsonSerializable(typeof(WorkspaceInfo))]
[JsonSerializable(typeof(MonitorInfoDto))]
[JsonSerializable(typeof(StateSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<WindowInfo>))]
[JsonSerializable(typeof(IReadOnlyList<WorkspaceInfo>))]
[JsonSerializable(typeof(IReadOnlyList<MonitorInfoDto>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
public sealed partial class IpcJsonContext : JsonSerializerContext;

/// <summary>Shared protocol constants.</summary>
public static class IpcProtocol
{
    /// <summary>
    /// The named pipe both ends use.
    /// </summary>
    /// <remarks>
    /// Per-user rather than global, so two people logged into the same machine each
    /// drive their own window manager rather than fighting over one.
    /// </remarks>
    public static string PipeName =>
        $"shubbak-{Environment.UserName.ToLowerInvariant()}";

    /// <summary>Messages are newline-delimited JSON.</summary>
    public const char MessageTerminator = '\n';
}
