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
/// <param name="Name">The name keybindings use.</param>
/// <param name="DisplayName">What the bar shows, when it differs.</param>
/// <param name="BindToMonitor">
/// The position of its home monitor in the enumeration, from <c>monitor=1</c>. Null
/// when unbound or bound by name.
/// </param>
/// <param name="Layout">The layout it starts in, when the config names one.</param>
/// <param name="BindToMonitorName">
/// The declared <c>monitor "name"</c> it lives on, from <c>monitor="name"</c>. Takes
/// precedence over the index; the two are never both set.
/// </param>
public sealed record WorkspaceConfig(
    string Name,
    string? DisplayName,
    int? BindToMonitor,
    string? Layout,
    string? BindToMonitorName = null);

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

    /// <summary>
    /// Which workspace a newly-managed window lands on.
    /// </summary>
    /// <remarks>
    /// Follows focus, because that is what every comparable window manager does and
    /// what pressing a launcher key means. It was previously the active workspace of
    /// whichever monitor the window opened on, which sounds equivalent and is not:
    /// Windows reopens most applications wherever they were last, so a window would
    /// arrive on a display the user was not looking at and read as having gone
    /// missing. <see cref="NewWindowPlacement.FollowWindow"/> restores that for
    /// applications that pick their display deliberately.
    /// </remarks>
    public NewWindowPlacement NewWindowPlacement { get; init; } = NewWindowPlacement.FollowFocus;

    /// <summary>
    /// Where the keyboard goes when the workspace being looked at has nothing to
    /// focus.
    /// </summary>
    /// <remarks>
    /// Held by a window of Shubbak's own by default, because Windows returns the
    /// foreground to whatever had it before when a launcher closes, and if that was
    /// a window on the other monitor the application the launcher started opens
    /// there. <see cref="EmptyWorkspaceFocus.Desktop"/> is the older behaviour, kept
    /// for anyone who would rather Shubbak owned no visible window at all.
    /// </remarks>
    public EmptyWorkspaceFocus EmptyWorkspaceFocus { get; init; } = EmptyWorkspaceFocus.Hold;

    /// <summary>
    /// Switch back to the previous workspace when re-focusing the active one.
    /// </summary>
    /// <remarks>
    /// GlazeWM's <c>toggle_workspace_on_refocus</c>. Only a genuine re-focus bounces:
    /// pressing the key of a workspace displayed on a monitor you are not on means
    /// "go there".
    /// </remarks>
    public bool ToggleWorkspaceOnRefocus { get; init; }

    /// <summary>
    /// Whether <c>move --workspace N</c> takes the view with the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, matching i3 and GlazeWM: "put this away" and "go there with
    /// it" are separate intentions, and sending a window somewhere you are not
    /// looking is the commoner of the two.
    /// </para>
    /// <para>
    /// This says it for every move at once, including the ones window rules and the
    /// command palette make - the palette's "send it to 3 and leave it there" stops
    /// leaving it there. A single binding says it with
    /// <c>move --workspace N --focus</c>, which is what the shipped config uses.
    /// </para>
    /// </remarks>
    public bool FollowWindowOnMove { get; init; }

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
    /// Whether the configuration file may be edited over the IPC pipe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default, and the asymmetry with <see cref="AllowShellExecOverIpc"/> is
    /// deliberate. The pipe is scoped to the account, and every process running as the
    /// user can already open the file and write to it; refusing to do so on their behalf
    /// would protect nothing. What the pipe adds is the reload, and a reload is
    /// something any client can already ask for.
    /// </para>
    /// <para>
    /// The edits themselves are narrow: rules can be added and removed, and nothing
    /// else can be written - a block that is not a rule is refused, and a rule that
    /// runs <c>shell-exec</c> is refused unless the pipe may run it directly. Somebody
    /// who nevertheless wants their file left alone by every tool says so here.
    /// </para>
    /// </remarks>
    public bool AllowConfigEditsOverIpc { get; init; } = true;

    /// <summary>
    /// Whether saving the configuration file reloads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On by default. Reload used to be entirely command-driven, so editing the file
    /// meant remembering to press the reload key - and the first sign of having
    /// forgotten was a rule that appeared not to work. The watcher is a directory
    /// notification with nothing polled, so a daemon nobody is editing pays nothing.
    /// </para>
    /// <para>
    /// The reload it triggers is the ordinary one, gate and all: a file with errors is
    /// reported and the running configuration is kept, exactly as it is for
    /// <c>wm-reload-config</c>. Saving mid-edit costs a message in the log, not a
    /// desktop.
    /// </para>
    /// </remarks>
    public bool ReloadOnSave { get; init; } = true;

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

    /// <summary>Displays named by what they are, for workspaces and commands to refer to.</summary>
    public IReadOnlyDictionary<string, MonitorDefinition> Monitors { get; init; } =
        new Dictionary<string, MonitorDefinition>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Named conditions on the desktop that layer overrides on this configuration while
    /// they hold, in declaration order.
    /// </summary>
    public IReadOnlyList<ContextDefinition> Contexts { get; init; } = [];

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
