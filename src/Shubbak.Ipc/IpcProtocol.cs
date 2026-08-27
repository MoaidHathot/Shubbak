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
/// <remarks>
/// <c>Active</c> and <c>Focused</c> are not the same thing. Active is true for the
/// displayed workspace of every monitor at once; focused is true for exactly one.
/// A bar that only receives the first has to mark them all identically, which is
/// wrong the moment there is more than one display.
/// </remarks>
public sealed record WorkspaceInfo(
    long Id,
    string Name,
    string DisplayName,
    bool Active,
    bool HasWindows,
    string Monitor,
    string Layout,
    int WindowCount,
    int SortIndex,
    int MonitorIndex,
    bool Focused = false);

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

/// <summary>
/// A window on the desktop, managed or not.
/// </summary>
/// <remarks>
/// <para>
/// Wider than <see cref="WindowInfo"/> because it answers a different question.
/// <c>WindowInfo</c> describes a node in the tree; this describes anything with a
/// top-level window, including the ones Shubbak has decided not to manage and the
/// ones it has never seen. Those are the interesting cases: a window is most likely
/// to be lost precisely when the window manager is not arranging it.
/// </para>
/// <para>
/// The tree-derived fields are null or false for a window that is not in the tree.
/// That is not missing data - there genuinely is no workspace for a window nobody
/// manages - and a client should read <see cref="Managed"/> before the rest.
/// </para>
/// </remarks>
/// <param name="Handle">Native window handle.</param>
/// <param name="Title">Window title at the time of the query.</param>
/// <param name="ClassName">Win32 window class.</param>
/// <param name="ProcessName">Owning executable, without extension.</param>
/// <param name="ProcessId">Owning process id.</param>
/// <param name="Elevated">Whether the owner runs above Shubbak's integrity level.</param>
/// <param name="Managed">Whether Shubbak has this window in its tree.</param>
/// <param name="ExclusionReason">Why it is not managed; null when it is.</param>
/// <param name="State">Tree state, when managed.</param>
/// <param name="Concealment">How the window is currently hidden: none, cloaked, hidden or minimised.</param>
/// <param name="Workspace">Owning workspace name, when managed.</param>
/// <param name="WorkspaceDisplayed">Whether that workspace is the one its monitor is showing.</param>
/// <param name="Monitor">Device id of the owning monitor, when managed.</param>
/// <param name="Sticky">Whether the window follows the user to every workspace.</param>
/// <param name="Focused">Whether this is the focused window.</param>
/// <param name="FocusSequence">Higher is more recently focused; zero means never.</param>
/// <param name="Scratchpad">
/// The scratchpad slot holding this window, when one is. Null for everything else.
/// </param>
/// <param name="Tags">
/// Every workspace this window is a member of, including the one it sits on.
/// </param>
public sealed record WindowCandidate(
    long Handle,
    string Title,
    string ClassName,
    string ProcessName,
    int ProcessId,
    bool Elevated,
    bool Managed,
    string? ExclusionReason,
    string? State,
    string Concealment,
    string? Workspace,
    bool WorkspaceDisplayed,
    string? Monitor,
    bool Sticky,
    bool Focused,
    long FocusSequence,

    // Added after the fact, and optional, so the protocol version stays where it is -
    // an older client simply never sees them. A window in a scratchpad is retrieved by
    // naming its slot, and a tagged window relocates to whichever of its workspaces
    // was activated last; without either, a client can tell that a window is behaving
    // oddly and not why.
    string? Scratchpad = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>A command verb, as described to clients.</summary>
/// <remarks>
/// Lets a client offer the command set without hard-coding it, which is what stops a
/// palette or a completion script drifting out of step every time a verb is added.
/// </remarks>
public sealed record CommandInfo(
    string Verb,
    string Summary,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Aliases);

/// <summary>A keybinding, as described to clients.</summary>
/// <param name="Key">The chord, as written in the config.</param>
/// <param name="Mode">The binding mode it belongs to; null for the default bindings.</param>
/// <param name="Commands">The commands it runs, in order.</param>
/// <param name="RepeatsOnHold">Whether holding the key repeats the commands.</param>
public sealed record BindingInfo(
    string Key,
    string? Mode,
    IReadOnlyList<string> Commands,
    bool RepeatsOnHold);

/// <summary>The whole state, for a bar that has just connected.</summary>
public sealed record StateSnapshot(
    IReadOnlyList<MonitorInfoDto> Monitors,
    IReadOnlyList<WorkspaceInfo> Workspaces,
    IReadOnlyList<WindowInfo> Windows,
    WindowInfo? FocusedWindow,
    string? BindingMode,
    bool Paused,

    // Appended and optional, so the protocol version stays where it is and an older
    // client simply never sees it. Distinct from Paused: paused means windows are not
    // being rearranged, suspended means the keyboard hook has been let go of - and a
    // client showing "everything is fine" while keys do nothing is the reason this is
    // worth reporting at all.
    bool Suspended = false);

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
[JsonSerializable(typeof(WindowCandidate))]
[JsonSerializable(typeof(CommandInfo))]
[JsonSerializable(typeof(BindingInfo))]
[JsonSerializable(typeof(StateSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<WindowInfo>))]
[JsonSerializable(typeof(IReadOnlyList<WorkspaceInfo>))]
[JsonSerializable(typeof(IReadOnlyList<MonitorInfoDto>))]
[JsonSerializable(typeof(IReadOnlyList<WindowCandidate>))]
[JsonSerializable(typeof(IReadOnlyList<CommandInfo>))]
[JsonSerializable(typeof(IReadOnlyList<BindingInfo>))]
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
    /// <para>
    /// Per-user rather than global, so two people logged into the same machine each
    /// drive their own window manager rather than fighting over one.
    /// </para>
    /// <para>
    /// Identified by the account's SID and not its name. Two accounts called
    /// <c>alice</c> in different domains share a name and nothing else, and would have
    /// collided; lower-casing a name also carries the Turkish-I problem, where a
    /// user called <c>ALICE</c> resolves differently depending on the machine's
    /// culture. The name is kept as a suffix so the pipe is still recognisable in
    /// Process Explorer.
    /// </para>
    /// <para>
    /// The version is part of the name deliberately. Adding or removing a method
    /// degrades gracefully, but changing a payload does not - System.Text.Json ignores
    /// members it does not know and leaves missing ones at their default, so an old
    /// bar against a new window manager misreads the state rather than failing. A
    /// version in the name turns that into "no window manager is running", which is
    /// wrong in a way anyone can act on.
    /// </para>
    /// </remarks>
    public static string PipeName { get; } = BuildPipeName();

    /// <summary>
    /// The wire format both ends must agree on.
    /// </summary>
    /// <remarks>
    /// Raise this whenever a payload changes shape - a renamed field, a removed one,
    /// or a meaning that no longer matches the name. Adding an optional field with a
    /// sensible default does not need it.
    /// </remarks>
    public const int ProtocolVersion = 1;

    private static string BuildPipeName()
    {
        string account = Environment.UserName;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

                if (identity.User is { } sid) account = sid.Value;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Falling back to the name is worse but still works on one machine
                // with one account, which is every ordinary case.
            }
        }

        return $"shubbak-v{ProtocolVersion}-{account}";
    }

    /// <summary>
    /// The name of the mutex that decides which process is <em>the</em> window manager.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoped exactly like <see cref="PipeName"/>, and for the same reason: one window
    /// manager per logged-in account, not one per machine. Two people signed in at once
    /// each get their own desktop and must each get their own daemon.
    /// </para>
    /// <para>
    /// <c>Local\</c> rather than <c>Global\</c>. The session-local namespace is what
    /// matches "per desktop", needs no privilege, and cannot be squatted by another
    /// session. The account is in the name as well, because a single session can host
    /// more than one account once <c>runas</c> is involved.
    /// </para>
    /// <para>
    /// Kept next to the pipe name deliberately. These two answers must agree - a
    /// daemon that holds the mutex but serves a pipe another daemon also serves is the
    /// exact failure the mutex exists to prevent - and answers that must agree are
    /// easiest to keep that way when they are computed side by side.
    /// </para>
    /// </remarks>
    public static string InstanceMutexName { get; } = @"Local\" + PipeName;

    /// <summary>Messages are newline-delimited JSON.</summary>
    public const char MessageTerminator = '\n';

    /// <summary>
    /// Told to a client whose backlog was dropped, so it re-reads the world.
    /// </summary>
    /// <remarks>
    /// A client mirroring state cannot notice events that never arrived. Without this
    /// it carries on showing whatever it last heard about - wrong, and confident -
    /// until something unrelated corrects it.
    /// </remarks>
    public const string ResyncTopic = "wm.resync";

    /// <summary>
    /// Told to every client as the window manager exits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bar launched by the window manager should go when it does. Without being
    /// told, the only signal is the pipe closing - which is indistinguishable from the
    /// window manager being restarted, so a client cannot act on it immediately
    /// without dying every time the daemon is reloaded.
    /// </para>
    /// <para>
    /// Best-effort, deliberately. Publishing enqueues to each client's outbox and the
    /// server does not flush on the way out, so a client that is slow to read may miss
    /// it. Clients must therefore still cope with the pipe simply going away; this
    /// only makes the common case prompt rather than making it certain.
    /// </para>
    /// </remarks>
    public const string ShutdownTopic = "wm.shutdown";

    /// <summary>
    /// A named gesture the user asked for, which the window manager does not
    /// interpret.
    /// </summary>
    /// <remarks>
    /// Raised by the <c>signal</c> command. The window manager carries the name and
    /// nothing else; whichever client is subscribed decides what it means. That is
    /// what lets user interface live outside the daemon without the daemon growing a
    /// command per program that might be listening.
    /// </remarks>
    public const string SignalTopic = "signal";

    /// <summary>
    /// Every topic the window manager publishes.
    /// </summary>
    /// <remarks>
    /// Held here so both ends agree, and so a subscription can be checked. Subscribing
    /// to anything at all was accepted, which meant a bar author who wrote
    /// <c>window.focus</c> for <c>window.focused</c> was told it had worked and then
    /// heard nothing for the life of the process.
    /// </remarks>
    public static readonly IReadOnlySet<string> Topics = new HashSet<string>(StringComparer.Ordinal)
    {
        "window.managed",
        "window.unmanaged",
        "window.focused",
        "window.title_changed",
        "window.state_changed",
        "window.tags_changed",
        "window.moved",
        "workspace.activated",
        "workspace.created",
        "workspace.destroyed",
        "workspace.moved",
        "layout.changed",
        "container.resized",
        "monitor.added",
        "monitor.removed",
        "monitor.changed",
        "binding_mode.changed",
        "command.rejected",
        "config.reloaded",
        "wm.paused",
        "wm.suspended",
        SignalTopic,
        ShutdownTopic,
        ResyncTopic,
    };

    /// <summary>
    /// The longest message either end will read.
    /// </summary>
    /// <remarks>
    /// A reader that waits for a newline will wait for one that never comes, growing
    /// its buffer until the process dies. A window tree serialises to a few tens of
    /// kilobytes, so a megabyte is far more than any honest message and far less than
    /// enough to hurt.
    /// </remarks>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>How many clients may be connected at once.</summary>
    /// <remarks>
    /// A bar per monitor, a CLI call or two, and a tail. Anything past that is a
    /// runaway rather than a workflow, and each connection costs a lock taken on the
    /// daemon thread for every published event.
    /// </remarks>
    public const int MaxClients = 32;

    /// <summary>How many distinct topics one client may subscribe to.</summary>
    public const int MaxSubscriptionsPerClient = 64;
}
