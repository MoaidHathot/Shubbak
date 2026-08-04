using Shubbak.Core.Commands;
using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Config;

/// <summary>A key combination bound to commands.</summary>
/// <param name="Modifiers">Modifiers that must be held.</param>
/// <param name="VirtualKey">Virtual-key code of the main key.</param>
/// <param name="Display">The binding as written, for diagnostics and the bar.</param>
public readonly record struct KeyBinding(int Modifiers, ushort VirtualKey, string Display)
{
    public override string ToString() => Display;
}

/// <summary>A binding and the commands it runs.</summary>
public sealed record Keybinding(
    KeyBinding Key,
    IReadOnlyList<WmCommand> Commands,
    TextSpan Span,
    bool? Repeat = null)
{
    /// <summary>Whether holding the key should keep running this binding.</summary>
    /// <remarks>
    /// The config wins when it says anything. Otherwise the commands decide, and a
    /// binding that runs several repeats only if all of them are safe to - the
    /// dangerous one in the list is the one that matters.
    /// </remarks>
    public bool RepeatsOnHold => Repeat ?? Commands.All(command => command.RepeatsOnHold);
}

/// <summary>
/// A named set of bindings that replaces the default set while active.
/// </summary>
/// <param name="Name">Mode name, as used by <c>wm-enable-binding-mode</c>.</param>
/// <param name="Keybindings">Bindings active in this mode.</param>
/// <param name="PassThrough">
/// Whether unbound keys reach applications. False for a mode like <c>pause</c>,
/// which exists precisely to swallow everything except its own escape hatch.
/// </param>
public sealed record BindingMode(
    string Name,
    IReadOnlyList<Keybinding> Keybindings,
    bool PassThrough);

/// <summary>A workspace declared in config.</summary>
public sealed record WorkspaceConfig(
    string Name,
    string? DisplayName,
    int? BindToMonitor,
    string? Layout);

/// <summary>Visual treatment of focused and unfocused windows.</summary>
/// <param name="Enabled">Whether to draw a border at all.</param>
/// <param name="FocusedColour">Border colour for the focused window, as #RRGGBB.</param>
/// <param name="UnfocusedColour">Border colour for other windows.</param>
/// <param name="FloatingColour">
/// Border colour for a focused window that is not in the tiling flow. Falls back to
/// <paramref name="FocusedColour"/> when unset.
/// </param>
/// <param name="FloatingUnfocusedColour">
/// Border colour for an unfocused window that is not in the tiling flow. Falls back
/// to <paramref name="UnfocusedColour"/> when unset.
/// </param>
public sealed record WindowEffects(
    bool Enabled = false,
    string? FocusedColour = null,
    string? UnfocusedColour = null,
    string? FloatingColour = null,
    string? FloatingUnfocusedColour = null);

/// <summary>
/// The whole of Shubbak's configuration.
/// </summary>
/// <remarks>
/// Immutable. Reloading builds a new instance and diffs it against the live one, so
/// a config change never rebuilds the window tree from scratch - which is what makes
/// reloading safe to bind to a key.
/// </remarks>
public sealed record ShubbakConfig
{
    public Gaps OuterGap { get; init; }

    public int InnerGap { get; init; }

    public WindowState InitialWindowState { get; init; } = WindowState.Tiling;

    public bool ToggleWorkspaceOnRefocus { get; init; }

    public bool FollowWindowOnMove { get; init; }

    public bool FocusFollowsCursor { get; init; }

    /// <summary>Move the cursor when focus crosses monitors.</summary>
    public bool CursorJumpOnMonitorFocus { get; init; }

    /// <summary>Move the cursor on every focus change.</summary>
    public bool CursorJumpOnWindowFocus { get; init; }

    public WindowEffects Effects { get; init; } = new();

    /// <summary>
    /// How the windows of inactive workspaces are taken off screen.
    /// </summary>
    /// <remarks>
    /// Cloaking is strongly preferred and is the default: a cloaked window is still
    /// visible to <c>IsWindowVisible</c>, so if Shubbak exits or is killed the next run
    /// adopts it and un-cloaks it through the ordinary path. The alternatives exist
    /// because cloaking relies on an undocumented shell interface, and if that becomes
    /// unavailable a config switch beats a rebuild.
    /// </remarks>
    public WindowHideMethod HideMethod { get; init; } = WindowHideMethod.Cloak;

    /// <summary>
    /// What a command that targets a window does when the focused window is one
    /// Shubbak does not manage.
    /// </summary>
    /// <remarks>
    /// Focus can be on a dialog, a tray popup, or an application the filter passed
    /// over. Shubbak's own idea of the focused window is then whatever was focused
    /// before, and running the command against that acts on a window the user is not
    /// looking at - which is a surprise for the float key and a disaster for the close
    /// key.
    /// </remarks>
    public UnmanagedWindowCommands UnmanagedWindowCommands { get; init; } =
        UnmanagedWindowCommands.Refuse;

    /// <summary>
    /// Whether <c>shell-exec</c> may be sent over the IPC pipe.
    /// </summary>
    /// <remarks>
    /// Off by default. A window manager is not an execution service: the command
    /// exists so a keybinding or a startup command can launch a terminal, which is a
    /// decision made deliberately in this file. The pipe is scoped to the account and
    /// not to the integrity level, so leaving it open means any process running as the
    /// user can ask an elevated daemon to start something elevated.
    /// </remarks>
    public bool AllowShellExecOverIpc { get; init; }

    /// <summary>
    /// Whether windows on inactive workspaces keep their taskbar button.
    /// </summary>
    /// <remarks>
    /// On by default, so the taskbar remains a complete list of what is open and a
    /// window on another workspace is one click away. Turning it off makes an inactive
    /// workspace vanish completely - tidier, but you have to remember where things
    /// are. Only meaningful with <see cref="WindowHideMethod.Cloak"/>; hiding and
    /// minimising already decide the matter themselves.
    /// </remarks>
    public bool KeepInTaskbar { get; init; } = true;

    /// <summary>Minimum level written to the log sinks.</summary>
    public Core.Diagnostics.LogLevel LogLevel { get; init; } = Core.Diagnostics.LogLevel.Information;

    /// <summary>Log file path, or null for none.</summary>
    public string? LogFile { get; init; }

    /// <summary>Animation durations and curves.</summary>
    public Core.Animation.AnimationOptions Animation { get; init; } =
        Core.Animation.AnimationOptions.Default;

    public IReadOnlyList<string> StartupCommands { get; init; } = [];

    public IReadOnlyList<WorkspaceConfig> Workspaces { get; init; } = [];

    public IReadOnlyList<Keybinding> Keybindings { get; init; } = [];

    public IReadOnlyList<BindingMode> BindingModes { get; init; } = [];

    public IReadOnlyList<WindowRule> Rules { get; init; } = [];

    public IReadOnlyDictionary<string, AppDefinition> Apps { get; init; } =
        new Dictionary<string, AppDefinition>(StringComparer.OrdinalIgnoreCase);

    public string? DefaultLayout { get; init; }

    public static ShubbakConfig Default => new();

    /// <summary>Projects the parts the state machine cares about.</summary>
    public Core.Wm.WmOptions ToWmOptions() => new()
    {
        OuterGap = OuterGap,
        InnerGap = InnerGap,
        InitialWindowState = InitialWindowState,
        ToggleWorkspaceOnRefocus = ToggleWorkspaceOnRefocus,
        FollowWindowOnMove = FollowWindowOnMove,

        // Resolved here rather than stored as a name, so an unknown layout is a
        // startup problem rather than a silent fallback every time a workspace is made.
        DefaultLayout = DefaultLayout is { Length: > 0 } name &&
                        Core.Layouts.LayoutRegistry.TryResolve(name, out Core.Layouts.ILayout layout)
            ? layout
            : null,
    };
}
