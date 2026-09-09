using System.Diagnostics;
using System.Globalization;
using Shubbak.Config;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;
using Shubbak.Ipc;

namespace Shubbak.Wm;

/// <summary>
/// What the desktop looks like right now, as far as contexts care, handed to the engine
/// at evaluation time.
/// </summary>
/// <remarks>
/// A struct of references rather than a copy of anything, so an evaluation that finds
/// nothing changed allocates nothing. Everything here is state the daemon already
/// holds; the engine reads it and keeps none of it.
/// </remarks>
internal readonly record struct ContextFacts(
    RootNode Root,
    WorkspaceNode? FocusedWorkspace,
    WindowRegistry Windows,
    DisplayTopologyKind Topology,
    bool RemoteSession,
    UserActivity? Activity);

/// <summary>Who asked for a pin, for the report and for a lease.</summary>
/// <param name="Description">Who, in words: <c>rasid.exe (pid 1234)</c>, or <c>a keybinding or rule</c>.</param>
/// <param name="ConnectionId">The pipe connection the request came over, or null for one that did not.</param>
internal readonly record struct PinOrigin(string Description, long? ConnectionId)
{
    public static PinOrigin Local { get; } = new("a keybinding or rule", null);
}

/// <summary>Where a command came from, when it came over the pipe.</summary>
/// <param name="ConnectionId">The connection, for a lease to be tied to.</param>
/// <param name="Description">The process on the other end, in words, for the report.</param>
internal readonly record struct CommandOrigin(long ConnectionId, string Description);

/// <summary>A context becoming active or stopping being.</summary>
internal readonly record struct ContextTransition(string Name, bool Active, string Source, string Reason);

/// <summary>What became of a pin request.</summary>
internal readonly record struct PinOutcome(bool Accepted, string? Refusal)
{
    public static PinOutcome Ok { get; } = new(true, null);

    public static PinOutcome Refused(string why) => new(false, why);
}

/// <summary>
/// Decides which contexts hold.
/// </summary>
/// <remarks>
/// <para>
/// A context holds when any of its <c>when</c> blocks holds, or when a command has
/// pinned it on; it does not hold when a command has pinned it off, whatever its
/// conditions say. Explicit beats inferred. A context whose conditions have just
/// stopped holding lingers for a moment first, because the things conditions watch -
/// a slide show starting, a monitor being plugged in - come and go in bursts, and a
/// context that flapped with them would run its on-enter and on-exit twice.
/// </para>
/// <para>
/// The engine holds three kinds of state and nothing else: the definitions, the pins,
/// and for each <c>window</c> and <c>focused</c> condition which windows currently
/// satisfy it - a set of handles per condition, not a catalogue of windows. Everything
/// else it reads off the daemon's own state at evaluation time. That is rule four in
/// <c>ideas/contexts.md</c>; the daemon never grows a second copy of the desktop.
/// </para>
/// <para>
/// Evaluation is driven, not periodic. Facts changing mark it dirty; the daemon
/// evaluates when it is dirty or when a linger or a time-to-live is due, and an
/// evaluation that finds nothing changed allocates nothing. Rule two.
/// </para>
/// <para>
/// Headless. No Win32, no clock of its own - time is a parameter - so every rule above
/// is stated in a test rather than inferred from a running daemon.
/// </para>
/// </remarks>
internal sealed class ContextEngine
{
    private sealed class PinRecord
    {
        public required bool On { get; init; }
        public required long SetAtTicks { get; init; }
        public long? ExpiresAtTicks { get; init; }
        public long? LeaseConnection { get; init; }
        public required string SetBy { get; init; }
    }

    private sealed class State
    {
        public required ContextDefinition Definition { get; set; }
        public bool Active;
        public bool Detected;
        public string Source = "detected";
        public string Reason = "never evaluated";

        /// <summary>When a block last held; the linger is measured from here.</summary>
        public long LastDetectedAtTicks = long.MinValue;

        public PinRecord? Pin;

        public long LingerTicks;
    }

    private static readonly IReadOnlyList<ContextTransition> NoTransitions = [];

    private State[] _states = [];
    private Dictionary<string, State> _byName = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, AppDefinition> _apps = new Dictionary<string, AppDefinition>();

    /// <summary>Which windows currently satisfy each <c>window</c> condition.</summary>
    private readonly Dictionary<WindowCondition, HashSet<nint>> _present = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether the foreground window satisfies each <c>focused</c> condition.</summary>
    private readonly Dictionary<WindowCondition, bool> _focused = new(ReferenceEqualityComparer.Instance);

    // Scratch for evaluation, sized once per load so evaluating allocates nothing.
    private bool[] _effective = [];
    private bool[] _detected = [];
    private readonly List<ContextTransition> _transitions = [];

    /// <summary>Whether something has changed since the last evaluation.</summary>
    public bool Dirty { get; private set; }

    /// <summary>The earliest moment a linger or a time-to-live falls due, or <see cref="long.MaxValue"/>.</summary>
    public long NextDeadlineTicks { get; private set; } = long.MaxValue;

    /// <summary>Whether any context asks about windows being present, so attributes are worth reading.</summary>
    public bool HasPresenceConditions => _present.Count > 0;

    /// <summary>Whether any context asks about the foreground window.</summary>
    public bool HasFocusConditions => _focused.Count > 0;

    /// <summary>Whether any context asks anything at all about windows.</summary>
    public bool HasWindowConditions => _present.Count > 0 || _focused.Count > 0 || _fullscreen;

    private bool _fullscreen;

    /// <summary>How many contexts are declared.</summary>
    public int Count => _states.Length;

    /// <summary>Marks the facts as changed, so the next evaluation runs.</summary>
    public void MarkDirty() => Dirty = true;

    /// <summary>
    /// Takes on a configuration, keeping what survives from the last one.
    /// </summary>
    /// <remarks>
    /// Pins and the active flag survive for a context that is still declared, so a
    /// reload is not a request to leave the context you are in - the same rule the
    /// binding table applies to modes. The window sets do not survive: the conditions
    /// are new objects, and the daemon re-reads the desktop into them straight after.
    /// </remarks>
    /// <returns>The names of pinned contexts the new configuration no longer declares.</returns>
    public IReadOnlyList<string> Load(ShubbakConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Dictionary<string, State> previous = _byName;
        List<string> lostPins = [];

        foreach ((string name, State old) in previous)
        {
            if (old.Pin is not null && !config.Contexts.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                lostPins.Add(name);
        }

        _apps = config.Apps;
        _states = new State[config.Contexts.Count];
        _byName = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
        _present.Clear();
        _focused.Clear();
        _fullscreen = false;

        for (int i = 0; i < config.Contexts.Count; i++)
        {
            ContextDefinition definition = config.Contexts[i];

            State state = previous.TryGetValue(definition.Name, out State? kept)
                ? kept
                : new State { Definition = definition };

            state.Definition = definition;
            state.LingerTicks = ToTicks(definition.Linger);

            _states[i] = state;
            _byName[definition.Name] = state;

            foreach (ContextCondition condition in definition.AllConditions)
            {
                if (condition is not WindowCondition window) continue;

                switch (window.Kind)
                {
                    case WindowConditionKind.Present: _present[window] = []; break;
                    case WindowConditionKind.Focused: _focused[window] = false; break;
                    case WindowConditionKind.Fullscreen: _fullscreen = true; break;
                }
            }
        }

        _effective = new bool[_states.Length];
        _detected = new bool[_states.Length];

        Dirty = true;
        return lostPins;
    }

    // ---- facts about windows ----------------------------------------------------

    /// <summary>
    /// A top-level window was shown, or its title changed: re-test it against every
    /// <c>window</c> condition.
    /// </summary>
    /// <remarks>
    /// Cheap-first is the caller's job - it reads the attributes once and only when
    /// <see cref="HasPresenceConditions"/> says anyone cares. Here it is one match per
    /// condition and a set add or remove, and dirty only when a set actually changed.
    /// </remarks>
    public void WindowSeen(nint handle, in WindowAttributes attributes)
    {
        foreach ((WindowCondition condition, HashSet<nint> handles) in _present)
        {
            bool matches = condition.MatchesWindow(attributes, _apps);
            bool changed = matches ? handles.Add(handle) : handles.Remove(handle);

            if (changed) Dirty = true;
        }
    }

    /// <summary>A top-level window was destroyed or hidden.</summary>
    public void WindowGone(nint handle)
    {
        foreach (HashSet<nint> handles in _present.Values)
            if (handles.Remove(handle)) Dirty = true;
    }

    /// <summary>The foreground window changed; null attributes mean nothing has focus.</summary>
    public void ForegroundChanged(in WindowAttributes? attributes)
    {
        foreach (WindowCondition condition in _focused.Keys.ToArray())
        {
            bool matches = attributes is { } present && condition.MatchesWindow(present, _apps);

            if (_focused[condition] != matches)
            {
                _focused[condition] = matches;
                Dirty = true;
            }
        }
    }

    /// <summary>
    /// Drops handles that no longer name a window, for the 2 s tick to call while any
    /// set is non-empty.
    /// </summary>
    /// <remarks>
    /// A destroy event can be missed - the hook is not installed while suspended - and
    /// a set that kept a dead handle would keep a context on for ever.
    /// </remarks>
    public void Prune(Func<nint, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        foreach (HashSet<nint> handles in _present.Values)
        {
            if (handles.Count == 0) continue;

            if (handles.RemoveWhere(h => !exists(h)) > 0) Dirty = true;
        }
    }

    /// <summary>Whether any window-presence set has anything in it, so pruning has work.</summary>
    public bool TracksAnyWindow
    {
        get
        {
            foreach (HashSet<nint> handles in _present.Values)
                if (handles.Count > 0) return true;

            return false;
        }
    }

    // ---- pins --------------------------------------------------------------------

    /// <summary>Pins a context, or takes its pin off.</summary>
    public PinOutcome Pin(string name, Core.Commands.ContextAction action, TimeSpan? ttl, bool lease, PinOrigin origin, long nowTicks)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_byName.TryGetValue(name, out State? state))
        {
            string declared = _states.Length == 0 ? "none" : string.Join(", ", _states.Select(s => s.Definition.Name));
            return PinOutcome.Refused($"No context called '{name}'. Declared: {declared}.");
        }

        if (lease && origin.ConnectionId is null)
        {
            return PinOutcome.Refused(
                "A lease needs a connection that stays open, and this request did not come over one. " +
                "Use --ttl from a keybinding, a rule or the command line.");
        }

        switch (action)
        {
            case Core.Commands.ContextAction.Auto:
                state.Pin = null;
                break;

            case Core.Commands.ContextAction.Set:
            case Core.Commands.ContextAction.Clear:
            case Core.Commands.ContextAction.Toggle:
                bool on = action switch
                {
                    Core.Commands.ContextAction.Set => true,
                    Core.Commands.ContextAction.Clear => false,
                    _ => !state.Active,
                };

                state.Pin = new PinRecord
                {
                    On = on,
                    SetAtTicks = nowTicks,
                    ExpiresAtTicks = ttl is { } life ? nowTicks + ToTicks(life) : null,
                    LeaseConnection = lease ? origin.ConnectionId : null,
                    SetBy = origin.Description,
                };
                break;
        }

        Dirty = true;
        return PinOutcome.Ok;
    }

    /// <summary>Takes off every pin a connection held, because the connection has gone.</summary>
    /// <returns>The names released.</returns>
    public IReadOnlyList<string> ReleaseLeases(long connectionId)
    {
        List<string>? released = null;

        foreach (State state in _states)
        {
            if (state.Pin?.LeaseConnection != connectionId) continue;

            state.Pin = null;
            (released ??= []).Add(state.Definition.Name);
            Dirty = true;
        }

        return released ?? [];
    }

    // ---- evaluation ----------------------------------------------------------------

    /// <summary>
    /// Decides every context against the facts, and says which changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A context may refer to another declared after it, so detection is run to a fixed
    /// point: the effective set is recomputed until a pass changes nothing. The loader
    /// has removed every cycle, so this converges in at most one pass per context; the
    /// cap below is a guard against a loader bug, not a design.
    /// </para>
    /// <para>
    /// Transitions are diffed once, against the state before the call, so a context that
    /// flipped and flipped back inside the fixed point reports nothing - which is the
    /// truth about it.
    /// </para>
    /// <para>
    /// The list returned is reused by the next call.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ContextTransition> Evaluate(in ContextFacts facts, long nowTicks)
    {
        if (!Dirty && nowTicks < NextDeadlineTicks) return NoTransitions;

        Dirty = false;
        _transitions.Clear();

        // Time-to-lives first, so a pin that has just lapsed does not decide this pass.
        foreach (State state in _states)
        {
            if (state.Pin is { ExpiresAtTicks: { } expiry } && nowTicks >= expiry)
                state.Pin = null;
        }

        for (int i = 0; i < _states.Length; i++) _effective[i] = _states[i].Active;

        int passes = 0;
        bool changed;

        do
        {
            changed = false;

            for (int i = 0; i < _states.Length; i++)
            {
                State state = _states[i];
                bool detected = Detected(state, facts, nowTicks);
                _detected[i] = detected;

                bool effective = state.Pin is { } pin
                    ? pin.On
                    : detected || (_effective[i] && Lingering(state, nowTicks));

                if (effective != _effective[i])
                {
                    _effective[i] = effective;
                    changed = true;
                }
            }
        }
        while (changed && ++passes <= _states.Length);

        long nextDeadline = long.MaxValue;

        for (int i = 0; i < _states.Length; i++)
        {
            State state = _states[i];

            state.Detected = _detected[i];
            if (_detected[i]) state.LastDetectedAtTicks = nowTicks;

            if (_effective[i] != state.Active)
            {
                state.Active = _effective[i];
                state.Source = state.Pin is not null ? "pinned" : "detected";
                state.Reason = Reason(state, facts, nowTicks);
                _transitions.Add(new ContextTransition(state.Definition.Name, state.Active, state.Source, state.Reason));
            }

            if (state.Pin is { ExpiresAtTicks: { } expiry }) nextDeadline = Math.Min(nextDeadline, expiry);

            // Lingering: active by detection with nothing holding, waiting to let go.
            if (state.Pin is null && state.Active && !state.Detected)
                nextDeadline = Math.Min(nextDeadline, state.LastDetectedAtTicks + state.LingerTicks);
        }

        NextDeadlineTicks = nextDeadline;
        return _transitions;
    }

    private static bool Lingering(State state, long nowTicks) =>
        state.LastDetectedAtTicks != long.MinValue && nowTicks - state.LastDetectedAtTicks < state.LingerTicks;

    private bool Detected(State state, in ContextFacts facts, long nowTicks)
    {
        IReadOnlyList<IReadOnlyList<ContextCondition>> blocks = state.Definition.When;

        for (int b = 0; b < blocks.Count; b++)
        {
            if (BlockHolds(blocks[b], facts)) return true;
        }

        return false;
    }

    private bool BlockHolds(IReadOnlyList<ContextCondition> block, in ContextFacts facts)
    {
        for (int c = 0; c < block.Count; c++)
        {
            if (!Holds(block[c], facts, out _)) return false;
        }

        return true;
    }

    /// <summary>Whether one condition holds, and what it saw.</summary>
    /// <remarks>
    /// The detail is a string only when asked for by a report; the evaluation path
    /// passes <c>out _</c> and the branches below build nothing in that case. Kept in
    /// one method so the report can never disagree with the decision.
    /// </remarks>
    private bool Holds(ContextCondition condition, in ContextFacts facts, out string? detail, bool describe = false)
    {
        bool raw;
        detail = null;

        switch (condition)
        {
            case WindowCondition { Kind: WindowConditionKind.Present } present:
            {
                int count = _present.TryGetValue(present, out HashSet<nint>? set) ? set.Count : 0;
                raw = count > 0;
                if (describe) detail = count == 0 ? "no such window" : $"{count} window(s)";
                break;
            }

            case WindowCondition { Kind: WindowConditionKind.Focused } focused:
                raw = _focused.TryGetValue(focused, out bool holds) && holds;
                if (describe) detail = raw ? "the foreground window matches" : "the foreground window does not match";
                break;

            case WindowCondition fullscreen:
            {
                WindowNode? found = null;

                foreach ((nint _, WindowNode window) in facts.Windows)
                {
                    bool isFullscreen = window.IsNativeFullscreen ||
                        window.State is WindowState.Fullscreen or WindowState.MonitorFullscreen;

                    if (!isFullscreen) continue;

                    if (fullscreen.IsUnconstrained || fullscreen.MatchesWindow(Attributes(window), _apps))
                    {
                        found = window;
                        break;
                    }
                }

                raw = found is not null;
                if (describe) detail = found is null ? "nothing matching is full-screen" : $"{found.Identity.ProcessName} is full-screen";
                break;
            }

            case WorkspaceCondition workspace:
            {
                WorkspaceNode? node = FindWorkspace(facts.Root, workspace.Workspace);
                raw = node is not null && (workspace.MustBeFocused
                    ? ReferenceEquals(facts.FocusedWorkspace, node)
                    : node.IsActive);

                if (describe)
                {
                    detail = node is null ? "no such workspace"
                        : workspace.MustBeFocused ? (raw ? "it is the focused workspace" : $"the focused workspace is {facts.FocusedWorkspace?.Name ?? "none"}")
                        : (raw ? $"shown on {node.Monitor?.DeviceId}" : "not shown on any monitor");
                }

                break;
            }

            case MonitorCountCondition count:
                raw = count.Accepts(facts.Root.Monitors.Count);
                if (describe) detail = $"{facts.Root.Monitors.Count} attached";
                break;

            case MonitorPresentCondition monitor:
            {
                MonitorNode? found = null;
                IReadOnlyList<MonitorNode> monitors = facts.Root.Monitors;

                // Indexed rather than foreach: enumerating an IReadOnlyList allocates
                // its enumerator, and this runs on every evaluation.
                for (int i = 0; i < monitors.Count && found is null; i++)
                {
                    if (IsNamed(monitors[i], monitor.Monitor)) found = monitors[i];
                }

                raw = found is not null;
                if (describe) detail = found is null ? "no attached display has that name" : $"{found.DeviceId} is \"{monitor.Monitor}\"";

                // `absent=` was folded into Negated at load; the raw fact is presence.
                break;
            }

            case TopologyCondition topology:
            {
                raw = false;
                for (int i = 0; i < topology.AnyOf.Count; i++)
                    if (topology.AnyOf[i] == facts.Topology) { raw = true; break; }

                if (describe) detail = $"the topology is {facts.Topology.Wire()}";
                break;
            }

            case RemoteSessionCondition:
                raw = facts.RemoteSession;
                if (describe) detail = raw ? "the session is remote" : "the session is local";
                break;

            case SystemStateCondition system:
            {
                raw = false;
                if (facts.Activity is { } activity)
                {
                    for (int i = 0; i < system.AnyOf.Count; i++)
                        if (system.AnyOf[i] == activity) { raw = true; break; }
                }

                if (describe) detail = facts.Activity is { } a ? $"the shell says {a.Wire()}" : "the shell has not been asked yet";
                break;
            }

            case ContextReferenceCondition reference:
            {
                // The current pass's value, which is what makes the fixed point converge.
                raw = false;
                for (int i = 0; i < _states.Length; i++)
                {
                    if (string.Equals(_states[i].Definition.Name, reference.Context, StringComparison.OrdinalIgnoreCase))
                    {
                        raw = _effective[i];
                        break;
                    }
                }

                if (describe) detail = raw ? $"\"{reference.Context}\" holds" : $"\"{reference.Context}\" does not hold";
                break;
            }

            default:
                raw = false;
                if (describe) detail = "unknown condition";
                break;
        }

        return condition.Negated ? !raw : raw;
    }

    private static WindowAttributes Attributes(WindowNode window) => new(
        window.Identity.Title,
        window.Identity.ClassName,
        window.Identity.ProcessName,
        window.Identity.ProcessPath);

    // Allocation-free lookups, for the evaluation path. The tree's own FindWorkspace and
    // MonitorNode.IsNamed are LINQ and foreach over interfaces, both of which allocate
    // an enumerator, and this runs on every tick that follows a window event.

    private static WorkspaceNode? FindWorkspace(RootNode root, string name)
    {
        IReadOnlyList<MonitorNode> monitors = root.Monitors;

        for (int m = 0; m < monitors.Count; m++)
        {
            IReadOnlyList<WorkspaceNode> workspaces = monitors[m].Workspaces;

            for (int w = 0; w < workspaces.Count; w++)
            {
                if (string.Equals(workspaces[w].Name, name, StringComparison.OrdinalIgnoreCase))
                    return workspaces[w];
            }
        }

        return null;
    }

    private static bool IsNamed(MonitorNode monitor, string name)
    {
        IReadOnlyList<string> names = monitor.Names;

        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private string Reason(State state, in ContextFacts facts, long nowTicks)
    {
        if (state.Pin is { } pin)
            return $"{(pin.On ? "set" : "cleared")} by {pin.SetBy}";

        if (state.Active)
        {
            if (!state.Detected) return "lingering after its conditions stopped holding";

            foreach (IReadOnlyList<ContextCondition> block in state.Definition.When)
            {
                if (!BlockHolds(block, facts)) continue;

                return string.Join(" and ", block.Select(c => c.Describe()));
            }

            return "its conditions hold";
        }

        return state.Definition.IsExternal
            ? "nothing has set it"
            : "no block of conditions holds";
    }

    // ---- what the daemon reads off the engine ----------------------------------------

    /// <summary>The active contexts, in declaration order.</summary>
    public IReadOnlyList<ContextDefinition> ActiveDefinitions()
    {
        List<ContextDefinition> active = [];

        foreach (State state in _states)
            if (state.Active) active.Add(state.Definition);

        return active;
    }

    /// <summary>The names of the active contexts, in declaration order.</summary>
    public IReadOnlyList<string> ActiveNames()
    {
        List<string> names = [];

        foreach (State state in _states)
            if (state.Active) names.Add(state.Definition.Name);

        return names;
    }

    /// <summary>Whether a context holds.</summary>
    public bool IsActive(string name) => _byName.TryGetValue(name, out State? state) && state.Active;

    /// <summary>Everything about every context, for <c>query contexts</c>.</summary>
    /// <remarks>
    /// Read-only: the conditions are asked again against the facts but nothing about
    /// the engine changes, so a report taken mid-linger describes the linger rather
    /// than ending it.
    /// </remarks>
    public IReadOnlyList<ContextReport> Report(in ContextFacts facts, long nowTicks)
    {
        List<ContextReport> reports = new(_states.Length);

        foreach (State state in _states)
        {
            List<WhenReport> blocks = [];

            foreach (IReadOnlyList<ContextCondition> block in state.Definition.When)
            {
                List<ConditionReport> conditions = [];
                bool blockHolds = true;

                foreach (ContextCondition condition in block)
                {
                    bool holds = Holds(condition, facts, out string? detail, describe: true);
                    blockHolds &= holds;
                    conditions.Add(new ConditionReport(condition.Describe(), holds, detail ?? ""));
                }

                blocks.Add(new WhenReport(blockHolds, conditions));
            }

            PinRecord? pin = state.Pin;
            bool lingering = pin is null && state.Active && !state.Detected;

            reports.Add(new ContextReport(
                state.Definition.Name,
                state.Active,
                pin is not null ? "pinned" : "detected",
                state.Definition.IsExternal,

                // Worked out now rather than read off the last transition, because a
                // context that has never changed has never had one - and the question
                // being asked is why it is the way it is right now.
                Reason(state, facts, nowTicks),
                pin is null ? null : pin.On ? "set" : "clear",
                pin?.SetBy,
                pin is null ? null : ToMilliseconds(nowTicks - pin.SetAtTicks),
                pin?.ExpiresAtTicks is { } expiry ? Math.Max(0, ToMilliseconds(expiry - nowTicks)) : null,
                pin?.LeaseConnection is not null,
                lingering ? Math.Max(0, ToMilliseconds(state.LastDetectedAtTicks + state.LingerTicks - nowTicks)) : null,
                blocks,
                DescribeEffects(state.Definition.Effects)));
        }

        return reports;
    }

    /// <summary>Short words for what a context changes, for the report.</summary>
    public static IReadOnlyList<string> DescribeEffects(ContextEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);

        List<string> words = [];

        if (effects.Gaps is { Any: true }) words.Add("gaps");
        if (effects.Effects is { Any: true }) words.Add("borders");
        if (effects.Animation is { Any: true }) words.Add("animation");
        if (effects.Bindings.Count > 0) words.Add($"{effects.Bindings.Count} binding(s)");
        if (effects.Rules.Count > 0) words.Add($"{effects.Rules.Count} rule(s)");

        foreach (WorkspaceHome home in effects.Workspaces)
            words.Add($"workspace \"{home.Workspace}\" on {home.MonitorName ?? home.MonitorIndex?.ToString(CultureInfo.InvariantCulture)}");

        if (effects.OnEnter.Count > 0) words.Add("on-enter");
        if (effects.OnExit.Count > 0) words.Add("on-exit");

        return words;
    }

    private static long ToTicks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    private static long ToMilliseconds(long ticks) => ticks * 1000 / Stopwatch.Frequency;
}
