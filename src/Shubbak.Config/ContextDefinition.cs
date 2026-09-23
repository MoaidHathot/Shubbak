using Shubbak.Core.Animation;
using Shubbak.Core.Commands;
using Shubbak.Core.Geometry;
using Shubbak.Core.Wm;

namespace Shubbak.Config;

// ---- conditions -------------------------------------------------------------

/// <summary>
/// One fact about the desktop a context can ask for.
/// </summary>
/// <param name="Negated">Whether the condition holds when the fact does not.</param>
/// <param name="Span">Where in the config it came from.</param>
/// <remarks>
/// A closed set, deliberately. Every kind here is something the window manager already
/// knows in order to place windows, or something Windows says about the <i>session</i>
/// in one cheap call; anything about what an application is doing arrives as an
/// external context set over the pipe. That is the line drawn in
/// <c>ideas/contexts.md</c>, and adding a kind here is crossing it.
/// </remarks>
public abstract record ContextCondition(bool Negated, TextSpan Span)
{
    /// <summary>The condition as the config wrote it, for reports.</summary>
    public abstract string Describe();

    /// <summary>The <c>!</c> a negated condition is written with.</summary>
    protected string Prefix => Negated ? "!" : "";
}

/// <summary>What a <see cref="WindowCondition"/> asks of the windows.</summary>
public enum WindowConditionKind
{
    /// <summary>Some top-level window matches, managed or not.</summary>
    Present,

    /// <summary>The foreground window matches.</summary>
    Focused,

    /// <summary>A managed window matching is full-screen: natively, or by Shubbak.</summary>
    Fullscreen,
}

/// <summary>
/// <c>window app="x"</c>, <c>focused app="x"</c>, <c>fullscreen [app="x"]</c>, each
/// optionally with inline matchers as children. <c>Kind</c> says which windows are
/// asked; <c>App</c> is a declared <c>app</c> the window must satisfy, or null;
/// <c>Matchers</c> are inline conditions it must satisfy, all of them.
/// </summary>
public sealed record WindowCondition(
    WindowConditionKind Kind,
    string? App,
    IReadOnlyList<WindowMatcher> Matchers,
    bool Negated,
    TextSpan Span) : ContextCondition(Negated, Span)
{
    /// <summary>Whether nothing narrows the window down, which only <c>fullscreen</c> may leave so.</summary>
    public bool IsUnconstrained => App is null && Matchers.Count == 0;

    /// <summary>Whether a window with these attributes is one this condition is about.</summary>
    public bool MatchesWindow(WindowAttributes window, IReadOnlyDictionary<string, AppDefinition> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);

        if (App is not null && !(apps.TryGetValue(App, out AppDefinition? app) && app.Matches(window)))
            return false;

        foreach (WindowMatcher matcher in Matchers)
            if (!matcher.Matches(window.Get(matcher.Target))) return false;

        return true;
    }

    public override string Describe()
    {
        string verb = Kind switch
        {
            WindowConditionKind.Present => "window",
            WindowConditionKind.Focused => "focused",
            _ => "fullscreen",
        };

        string subject = App is not null ? $" app=\"{App}\"" : "";
        string inline = Matchers.Count > 0 ? " { " + string.Join("; ", Matchers) + " }" : "";

        return $"{Prefix}{verb}{subject}{inline}";
    }
}

/// <summary>
/// <c>workspace active="3"</c> or <c>workspace focused="3"</c>. <c>MustBeFocused</c>
/// is the difference: the focused workspace, rather than one merely displayed on one
/// of the monitors.
/// </summary>
public sealed record WorkspaceCondition(
    string Workspace,
    bool MustBeFocused,
    bool Negated,
    TextSpan Span) : ContextCondition(Negated, Span)
{
    public override string Describe() =>
        $"{Prefix}workspace {(MustBeFocused ? "focused" : "active")}=\"{Workspace}\"";
}

/// <summary><c>monitors count=2</c>, <c>monitors min=2</c>, <c>monitors max=1</c>.</summary>
public sealed record MonitorCountCondition(
    int? Exactly,
    int? Min,
    int? Max,
    bool Negated,
    TextSpan Span) : ContextCondition(Negated, Span)
{
    /// <summary>Whether a count satisfies every bound given.</summary>
    public bool Accepts(int count) =>
        (Exactly is null || count == Exactly) &&
        (Min is null || count >= Min) &&
        (Max is null || count <= Max);

    public override string Describe()
    {
        List<string> parts = [];
        if (Exactly is { } n) parts.Add($"count={n}");
        if (Min is { } lo) parts.Add($"min={lo}");
        if (Max is { } hi) parts.Add($"max={hi}");
        return $"{Prefix}monitors {string.Join(' ', parts)}";
    }
}

/// <summary>
/// <c>monitor present="dell-left"</c>, naming a declared <c>monitor</c>;
/// <c>absent=</c> is the negated spelling.
/// </summary>
public sealed record MonitorPresentCondition(
    string Monitor,
    bool Negated,
    TextSpan Span) : ContextCondition(Negated, Span)
{
    public override string Describe() => $"monitor {(Negated ? "absent" : "present")}=\"{Monitor}\"";
}

/// <summary><c>display-topology "extend"</c>, with several arguments meaning any of them.</summary>
public sealed record TopologyCondition(
    IReadOnlyList<DisplayTopologyKind> AnyOf,
    bool Negated,
    TextSpan Span) : ContextCondition(Negated, Span)
{
    public override string Describe() =>
        $"{Prefix}display-topology {string.Join(' ', AnyOf.Select(k => $"\"{k.Wire()}\""))}";
}

/// <summary><c>remote-session</c>, or <c>!remote-session</c>.</summary>
public sealed record RemoteSessionCondition(bool Negated, TextSpan Span) : ContextCondition(Negated, Span)
{
    public override string Describe() => $"{Prefix}remote-session";
}

/// <summary><c>system-state "presenting"</c>, with several arguments meaning any of them.</summary>
public sealed record SystemStateCondition(
    IReadOnlyList<UserActivity> AnyOf,
    bool Negated,
    TextSpan Span) : ContextCondition(Negated, Span)
{
    public override string Describe() =>
        $"{Prefix}system-state {string.Join(' ', AnyOf.Select(a => $"\"{a.Wire()}\""))}";
}

/// <summary><c>context "meeting"</c>: another context holds.</summary>
public sealed record ContextReferenceCondition(
    string Context,
    bool Negated,
    TextSpan Span) : ContextCondition(Negated, Span)
{
    public override string Describe() => $"{Prefix}context \"{Context}\"";
}

// ---- overrides --------------------------------------------------------------

/// <summary>
/// The <c>gaps</c> section as a delta: only what was written, layered onto whatever
/// is underneath.
/// </summary>
/// <remarks>
/// The top-level section and a context's block are read by the same code into this
/// shape, and the top-level section is applied onto the defaults. That is what makes a
/// context's <c>gaps { inner 0 }</c> mean "inner zero, everything else as it was"
/// rather than "inner zero, everything else reset".
/// </remarks>
public sealed record GapsOverride(int? Inner, int? Left, int? Top, int? Right, int? Bottom)
{
    public bool Any => Inner is not null || Left is not null || Top is not null || Right is not null || Bottom is not null;

    public ShubbakConfig Apply(ShubbakConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Gaps outer = config.OuterGap;

        return config with
        {
            InnerGap = Math.Max(0, Inner ?? config.InnerGap),
            OuterGap = new Gaps(
                Math.Max(0, Left ?? outer.Left),
                Math.Max(0, Top ?? outer.Top),
                Math.Max(0, Right ?? outer.Right),
                Math.Max(0, Bottom ?? outer.Bottom)),
        };
    }
}

/// <summary>The <c>window-effects</c> section as a delta.</summary>
public sealed record EffectsOverride(
    bool? Border,
    string? FocusedColour,
    string? UnfocusedColour,
    string? FloatingColour,
    string? FloatingUnfocusedColour)
{
    public bool Any =>
        Border is not null || FocusedColour is not null || UnfocusedColour is not null ||
        FloatingColour is not null || FloatingUnfocusedColour is not null;

    public ShubbakConfig Apply(ShubbakConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        WindowEffects effects = config.Effects;

        return config with
        {
            Effects = new WindowEffects(
                Border ?? effects.Enabled,
                FocusedColour ?? effects.FocusedColour,
                UnfocusedColour ?? effects.UnfocusedColour,
                FloatingColour ?? effects.FloatingColour,
                FloatingUnfocusedColour ?? effects.FloatingUnfocusedColour),
        };
    }
}

/// <summary>A frame rate as written: a number, or <c>"auto"</c> as null.</summary>
public sealed record FpsSetting(int? Value);

/// <summary>One animation profile as a delta: a duration, a curve, or both.</summary>
public sealed record ProfileOverride(TimeSpan? Duration, Easing? Curve)
{
    public AnimationProfile Apply(AnimationProfile profile) =>
        new(Duration ?? profile.Duration, Curve ?? profile.Curve);
}

/// <summary>The <c>animation</c> section as a delta.</summary>
public sealed record AnimationOverride(
    bool? Enabled,
    bool? AnimateNewWindows,
    int? MinimumDistance,
    FpsSetting? Fps,
    ProfileOverride? WindowOpen,
    ProfileOverride? WindowMove,
    ProfileOverride? LayoutChange,
    ProfileOverride? WorkspaceSwitch,
    ProfileOverride? Resize = null)
{
    public bool Any =>
        Enabled is not null || AnimateNewWindows is not null || MinimumDistance is not null ||
        Fps is not null || WindowOpen is not null || WindowMove is not null ||
        LayoutChange is not null || WorkspaceSwitch is not null || Resize is not null;

    public ShubbakConfig Apply(ShubbakConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        AnimationOptions animation = config.Animation;

        return config with
        {
            Animation = animation with
            {
                Enabled = Enabled ?? animation.Enabled,
                AnimateNewWindows = AnimateNewWindows ?? animation.AnimateNewWindows,
                MinimumAnimatedDistance = Math.Max(0, MinimumDistance ?? animation.MinimumAnimatedDistance),
                FramesPerSecond = Fps is { } fps ? fps.Value : animation.FramesPerSecond,
                WindowOpen = WindowOpen?.Apply(animation.WindowOpen) ?? animation.WindowOpen,
                WindowMove = WindowMove?.Apply(animation.WindowMove) ?? animation.WindowMove,
                LayoutChange = LayoutChange?.Apply(animation.LayoutChange) ?? animation.LayoutChange,
                WorkspaceSwitch = WorkspaceSwitch?.Apply(animation.WorkspaceSwitch) ?? animation.WorkspaceSwitch,
                Resize = Resize?.Apply(animation.Resize) ?? animation.Resize,
            },
        };
    }
}

/// <summary>Where a workspace lives while a context holds: <c>workspace "3" monitor="projector"</c>.</summary>
public sealed record WorkspaceHome(string Workspace, int? MonitorIndex, string? MonitorName, TextSpan Span);

/// <summary>
/// Everything a context changes while it holds.
/// </summary>
/// <param name="Gaps">A delta on the gaps, or null.</param>
/// <param name="Effects">A delta on the border colours, or null.</param>
/// <param name="Animation">A delta on the animation settings, or null.</param>
/// <param name="Bindings">
/// Keybindings laid over the default table. A binding with no commands disarms the key.
/// </param>
/// <param name="Rules">Window rules that apply only while the context holds.</param>
/// <param name="Workspaces">Workspaces that live somewhere else while it holds.</param>
/// <param name="OnEnter">Commands run once as the context becomes active.</param>
/// <param name="OnExit">Commands run once as it stops being.</param>
public sealed record ContextEffects(
    GapsOverride? Gaps,
    EffectsOverride? Effects,
    AnimationOverride? Animation,
    IReadOnlyList<Keybinding> Bindings,
    IReadOnlyList<WindowRule> Rules,
    IReadOnlyList<WorkspaceHome> Workspaces,
    IReadOnlyList<WmCommand> OnEnter,
    IReadOnlyList<WmCommand> OnExit)
{
    public static ContextEffects None { get; } = new(null, null, null, [], [], [], [], []);

    /// <summary>Whether the context changes anything at all, as opposed to being a flag.</summary>
    public bool Any =>
        Gaps is { Any: true } || Effects is { Any: true } || Animation is { Any: true } ||
        Bindings.Count > 0 || Rules.Count > 0 || Workspaces.Count > 0 ||
        OnEnter.Count > 0 || OnExit.Count > 0;
}

/// <summary>
/// A named condition on the desktop that layers overrides on the configuration while
/// it holds.
/// </summary>
/// <param name="Name">What commands, the bar and the palette call it.</param>
/// <param name="When">
/// The conditions, as blocks: every condition in a block must hold, and any block
/// holding is enough. Empty for an external context, which only a command can set.
/// </param>
/// <param name="Linger">
/// How long the conditions have to have stopped holding before the context lets go.
/// PowerPoint creates and destroys several windows while a slide show starts; without
/// this the context flaps and its on-enter and on-exit run twice.
/// </param>
/// <param name="Effects">What it changes.</param>
/// <param name="Span">Where in the config it came from.</param>
public sealed record ContextDefinition(
    string Name,
    IReadOnlyList<IReadOnlyList<ContextCondition>> When,
    TimeSpan Linger,
    ContextEffects Effects,
    TextSpan Span)
{
    /// <summary>The linger a context gets when it does not say.</summary>
    public static readonly TimeSpan DefaultLinger = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Whether nothing on the desktop decides this context, so only a command can.
    /// </summary>
    /// <remarks>
    /// The extension primitive. A process that watches something the window manager
    /// does not - a camera, a calendar, a call - declares nothing here and sets the
    /// context over the pipe; the configuration decides what the context <i>does</i>,
    /// and the process never needs to know.
    /// </remarks>
    public bool IsExternal => When.Count == 0;

    /// <summary>Every condition in every block, for validation and reports.</summary>
    public IEnumerable<ContextCondition> AllConditions => When.SelectMany(block => block);
}

// ---- the cascade ------------------------------------------------------------

/// <summary>What the active contexts make of the configuration.</summary>
/// <param name="Config">The file's settings with the active contexts' deltas applied, in order.</param>
/// <param name="OverlayBindings">
/// Keybindings to lay over the default table, later contexts winning on the same key.
/// Kept apart from <see cref="ShubbakConfig.Keybindings"/>, which stays what the file
/// said, because the overlay is applied differently: on top of the table rather than
/// as the table.
/// </param>
public sealed record EffectiveConfig(ShubbakConfig Config, IReadOnlyList<Keybinding> OverlayBindings);

/// <summary>
/// Layers the active contexts onto the configuration.
/// </summary>
/// <remarks>
/// <para>
/// Declaration order, later wins: the same model the bar's <c>extends</c> uses, and the
/// one a person reading the file top to bottom already has in their head. The base
/// config is layer zero.
/// </para>
/// <para>
/// Pure, so that "what does the config look like while presenting and docked" is a
/// question a test can ask without a desktop.
/// </para>
/// </remarks>
public static class ContextCascade
{
    public static EffectiveConfig Apply(ShubbakConfig baseConfig, IReadOnlyList<ContextDefinition> active)
    {
        ArgumentNullException.ThrowIfNull(baseConfig);
        ArgumentNullException.ThrowIfNull(active);

        if (active.Count == 0) return new EffectiveConfig(baseConfig, []);

        ShubbakConfig config = baseConfig;
        List<WindowRule> rules = [.. baseConfig.Rules];
        List<Keybinding> overlay = [];
        Dictionary<string, WorkspaceHome>? homes = null;

        foreach (ContextDefinition context in active)
        {
            ContextEffects effects = context.Effects;

            if (effects.Gaps is { Any: true } gaps) config = gaps.Apply(config);
            if (effects.Effects is { Any: true } look) config = look.Apply(config);
            if (effects.Animation is { Any: true } motion) config = motion.Apply(config);

            rules.AddRange(effects.Rules);
            overlay.AddRange(effects.Bindings);

            foreach (WorkspaceHome home in effects.Workspaces)
            {
                homes ??= new Dictionary<string, WorkspaceHome>(StringComparer.OrdinalIgnoreCase);
                homes[home.Workspace] = home;
            }
        }

        IReadOnlyList<WorkspaceConfig> workspaces = baseConfig.Workspaces;

        if (homes is not null)
        {
            workspaces =
            [
                .. baseConfig.Workspaces.Select(w => homes.TryGetValue(w.Name, out WorkspaceHome? home)
                    ? w with { BindToMonitor = home.MonitorIndex, BindToMonitorName = home.MonitorName }
                    : w),
            ];
        }

        return new EffectiveConfig(
            config with { Rules = rules, Workspaces = workspaces },
            overlay);
    }
}
