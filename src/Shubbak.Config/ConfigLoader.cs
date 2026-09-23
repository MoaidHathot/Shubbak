using Shubbak.Config.Kdl;
using Shubbak.Core.Commands;
using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Config;

/// <summary>The outcome of loading a config file.</summary>
/// <param name="Config">The config; defaults are used for anything that failed.</param>
/// <param name="Diagnostics">Everything found, errors and warnings alike.</param>
public readonly record struct ConfigLoadResult(
    ShubbakConfig Config, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasErrors
    {
        get
        {
            foreach (Diagnostic d in Diagnostics)
                if (d.Severity == DiagnosticSeverity.Error) return true;

            return false;
        }
    }

    public IEnumerable<Diagnostic> Errors =>
        Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    public IEnumerable<Diagnostic> Warnings =>
        Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning);
}

/// <summary>
/// Builds a <see cref="ShubbakConfig"/> from KDL.
/// </summary>
/// <remarks>
/// <para>
/// Loading is <b>total</b>: any section that fails to parse is reported and skipped,
/// and the rest of the file still loads. A single typo must never leave the user
/// with no window manager at all.
/// </para>
/// </remarks>
public sealed class ConfigLoader
{
    private readonly List<Diagnostic> _diagnostics = [];

    /// <summary>Loads config from KDL source text.</summary>
    public static ConfigLoadResult Load(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var loader = new ConfigLoader();
        KdlParseResult parsed = KdlParser.Parse(source);
        loader._diagnostics.AddRange(parsed.Diagnostics);

        if (parsed.HasErrors)
            return new ConfigLoadResult(ShubbakConfig.Default, loader._diagnostics);

        ShubbakConfig config = loader.Build(parsed.Document);
        return new ConfigLoadResult(config, loader._diagnostics);
    }

    /// <summary>Loads config from a file.</summary>
    /// <remarks>
    /// A file that cannot be read is a diagnostic, not an exception. The reload paths
    /// call this while an editor may still hold the file - a save-and-reload key, and
    /// now a watcher that reloads on the save itself - and an <c>IOException</c> from
    /// here used to travel up into the message loop, where the tick has no business
    /// catching it. The same code as a missing file, because the answer to both is the
    /// same: nothing was loaded, and here is why.
    /// </remarks>
    public static ConfigLoadResult LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return new ConfigLoadResult(ShubbakConfig.Default, [
                Diagnostic.Error(
                    "SHB0400",
                    $"Config file not found: {path}",
                    new TextSpan(new TextPosition(1, 1, 0), 0),
                    "Run 'shubbak config init' to write a starter config.")
            ]);
        }

        string text;

        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigLoadResult(ShubbakConfig.Default, [
                Diagnostic.Error(
                    "SHB0400",
                    $"Config file could not be read: {path} ({ex.Message})",
                    new TextSpan(new TextPosition(1, 1, 0), 0),
                    "Another program may be holding it. Try again in a moment.")
            ]);
        }

        return Load(text);
    }

    private ShubbakConfig Build(KdlDocument document)
    {
        var config = ShubbakConfig.Default;

        WarnAboutUnknown(document.Nodes, KnownSections, "section", "SHB0427");

        config = ApplyGeneral(config, document.Node("general"));
        config = ApplyGaps(config, document.Node("gaps"));
        config = ApplyEffects(config, document.Node("window-effects"));
        config = ApplyAnimation(config, document.Node("animation"));
        config = ApplyLogging(config, document.Node("logging"));

        Dictionary<string, AppDefinition> apps = ParseApps(document);
        Dictionary<string, MonitorDefinition> monitors = ParseMonitors(document);
        List<WorkspaceConfig> workspaces = ParseWorkspaces(document.Node("workspaces"), monitors);

        ShubbakConfig loaded = config with
        {
            Apps = apps,
            Monitors = monitors,
            Workspaces = workspaces,
            Keybindings = ParseKeybindings(document.Node("keybindings"), workspaces),
            BindingModes = ParseBindingModes(document.Node("binding-modes"), workspaces),

            // Every block, not the first. The loader used to read one `rules { }` and
            // drop any other in silence - no diagnostic, nothing in the log - which
            // made "paste this at the end of your file" wrong advice for every file
            // that already had a rules block, which is every file the starter config
            // produces. A second block is now simply more rules, in file order, which
            // is also what lets a tool append one without having to find and edit the
            // block that is already there.
            Rules = ParseRules(document.NodesNamed("rules"), apps),
            Contexts = ParseContexts(document.Node("contexts"), apps, monitors, workspaces),
        };

        WarnAboutUndeclaredBindingModes(loaded);
        WarnAboutUndeclaredMonitors(loaded);
        WarnAboutUndeclaredContexts(loaded);

        return loaded;
    }

    /// <summary>
    /// Reports commands that enter a binding mode nobody declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A post-pass, because keybindings are parsed before the modes are known and
    /// this needs both.
    /// </para>
    /// <para>
    /// Caught at runtime as well, where it is refused out loud - but by then the user
    /// has pressed a key and watched nothing happen. This is the moment the mistake
    /// can be pointed at, with a line and a caret, before it has cost them anything.
    /// </para>
    /// </remarks>
    private void WarnAboutUndeclaredBindingModes(ShubbakConfig config)
    {
        string[] declared = [.. config.BindingModes.Select(mode => mode.Name)];

        foreach (Keybinding binding in AllBindings(config))
        {
            foreach (WmCommand command in binding.Commands)
            {
                if (command is not EnableBindingModeCommand enter) continue;
                if (declared.Contains(enter.Mode, StringComparer.OrdinalIgnoreCase)) continue;

                string? guess = Suggestion.Closest(enter.Mode, declared);

                Report(Diagnostic.Warning(
                    "SHB0434",
                    $"Binding '{binding.Key.Display}' enters binding mode '{enter.Mode}', " +
                    "which is not declared.",
                    binding.Span,
                    declared.Length == 0
                        ? "No binding modes are declared. Add one with binding-modes { mode \"pause\" { ... } }."
                        : guess is not null
                            ? $"Did you mean '{guess}'?"
                            : $"Declared modes: {string.Join(", ", declared)}."));
            }
        }
    }

    /// <summary>Every binding in the config: default, per-mode, and per-context alike.</summary>
    /// <remarks>
    /// A mode can enter another mode, so the bindings inside modes have to be checked
    /// too - and a typo there is harder to notice, because reaching it means being in
    /// the first mode already. A context's overlay bindings are the same story one
    /// level further away: reaching them means the context being active.
    /// </remarks>
    private static IEnumerable<Keybinding> AllBindings(ShubbakConfig config) =>
        config.Keybindings
            .Concat(config.BindingModes.SelectMany(mode => mode.Keybindings))
            .Concat(config.Contexts.SelectMany(context => context.Effects.Bindings));

    /// <summary>
    /// Everywhere a command can be written, with a name for the place, for the
    /// post-passes that check what commands refer to.
    /// </summary>
    private static IEnumerable<(IReadOnlyList<WmCommand> Commands, TextSpan Span, string Where)> AllCommandSites(
        ShubbakConfig config)
    {
        foreach (Keybinding binding in AllBindings(config))
            yield return (binding.Commands, binding.Span, $"Binding '{binding.Key.Display}'");

        foreach (WindowRule rule in config.Rules)
            yield return (rule.Commands, rule.Span, $"Rule '{rule.Name}'");

        foreach (ContextDefinition context in config.Contexts)
        {
            foreach (WindowRule rule in context.Effects.Rules)
                yield return (rule.Commands, rule.Span, $"Rule '{rule.Name}' in context '{context.Name}'");

            if (context.Effects.OnEnter.Count > 0)
                yield return (context.Effects.OnEnter, context.Span, $"on-enter of context '{context.Name}'");

            if (context.Effects.OnExit.Count > 0)
                yield return (context.Effects.OnExit, context.Span, $"on-exit of context '{context.Name}'");
        }
    }

    /// <summary>
    /// Reports <c>move-workspace --monitor</c> commands that name a monitor nobody
    /// declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference may also be a position (<c>1</c>) or a GDI device name
    /// (<c>DISPLAY2</c>, with or without the <c>\\.\</c>), neither of which the loader
    /// can check - the desktop is not attached to the config file. Only a word that is
    /// none of those is reported, and as a warning: the command is refused out loud at
    /// runtime too, but by then the key has been pressed.
    /// </para>
    /// <para>
    /// Rules are walked as well as bindings, because a rule can run any command.
    /// </para>
    /// </remarks>
    private void WarnAboutUndeclaredMonitors(ShubbakConfig config)
    {
        string[] declared = [.. config.Monitors.Keys];

        foreach ((IReadOnlyList<WmCommand> commands, TextSpan span, string where) in AllCommandSites(config))
        {
            foreach (WmCommand command in commands)
            {
                if (command is not MoveWorkspaceToMonitorCommand { Monitor: { } reference }) continue;
                if (MonitorReference.IsPositional(reference)) continue;
                if (declared.Contains(reference, StringComparer.OrdinalIgnoreCase)) continue;

                string? guess = Suggestion.Closest(reference, declared);

                Report(Diagnostic.Warning(
                    "SHB0443",
                    $"{where} moves a workspace to monitor '{reference}', which is not declared.",
                    span,
                    declared.Length == 0
                        ? "No monitors are declared. Add one with monitor \"name\" { path ~= \"...\" }, " +
                          "or give a position such as --monitor 1."
                        : guess is not null
                            ? $"Did you mean '{guess}'?"
                            : $"Declared monitors: {string.Join(", ", declared)}."));
            }
        }
    }

    /// <summary>
    /// Reports <c>context</c> commands that name a context nobody declared.
    /// </summary>
    /// <remarks>
    /// A warning, like the two above: refused out loud at runtime as well, but this is
    /// the moment the mistake can be pointed at with a line and a caret. An external
    /// context - one with no <c>when</c> - is declared like any other, so a provider's
    /// name has to appear in the file before a key can set it; that is deliberate, since
    /// the file is where what the context <i>does</i> is written.
    /// </remarks>
    private void WarnAboutUndeclaredContexts(ShubbakConfig config)
    {
        string[] declared = [.. config.Contexts.Select(c => c.Name)];

        foreach ((IReadOnlyList<WmCommand> commands, TextSpan span, string where) in AllCommandSites(config))
        {
            foreach (WmCommand command in commands)
            {
                if (command is not ContextCommand { Context: var reference }) continue;
                if (declared.Contains(reference, StringComparer.OrdinalIgnoreCase)) continue;

                string? guess = Suggestion.Closest(reference, declared);

                Report(Diagnostic.Warning(
                    "SHB0451",
                    $"{where} refers to context '{reference}', which is not declared.",
                    span,
                    declared.Length == 0
                        ? "No contexts are declared. Add one with contexts { context \"" + reference + "\" { } }."
                        : guess is not null
                            ? $"Did you mean '{guess}'?"
                            : $"Declared contexts: {string.Join(", ", declared)}."));
            }
        }
    }

    private static readonly string[] KnownSections =
    [
        "general", "gaps", "window-effects", "animation", "logging",
        "workspaces", "keybindings", "binding-modes", "rules", "app", "monitor", "contexts", "bar", "dalil", "ayn",
    ];

    private static readonly string[] KnownWorkspaceKeys = ["display-name", "monitor", "layout"];

    private static readonly string[] KnownGeneralKeys =
    [
        "toggle-workspace-on-refocus", "follow-window-on-move",
        "initial-window-state", "hide-method", "keep-in-taskbar",
        "default-layout", "unmanaged-window-commands", "allow-shell-exec-over-ipc",
        "allow-config-edits-over-ipc", "reload-on-save",
        "startup-command", "new-window-placement", "empty-workspace-focus",
    ];

    /// <summary>
    /// Settings that were once accepted and are no more, with what to do instead.
    /// </summary>
    /// <remarks>
    /// A setting that parses and does nothing is worse than one that is refused: the
    /// person who wrote it believes it is in force. <c>focus-follows-cursor</c> and
    /// <c>cursor-jump</c> were read into the configuration and consulted by nothing,
    /// for their whole existence; the example file carried them and the documentation
    /// said they did what they said. Reported by name (SHB0455) rather than as an unknown
    /// setting with a guess, because the guess would be the setting itself.
    /// </remarks>
    private static readonly Dictionary<string, string> RemovedGeneralKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["focus-follows-cursor"] = "Shubbak follows the keyboard, not the pointer; the setting never did anything. Remove it.",
        ["cursor-jump"] = "The pointer is never moved by Shubbak; the setting never did anything. Remove it.",
    };

    private static readonly string[] KnownAnimationKeys =
    [
        "enabled", "animate-new-windows", "minimum-distance", "fps",
        "window-open", "window-move", "layout-change", "workspace-switch",
    ];

    private static readonly string[] KnownEffectsKeys =
    [
        "border", "focused-colour", "focused-color", "unfocused-colour", "unfocused-color",
        "floating-colour", "floating-color", "floating-unfocused-colour", "floating-unfocused-color",
    ];

    private static readonly string[] KnownGapsKeys = ["inner", "outer"];

    private static readonly string[] KnownLoggingKeys = ["level", "file"];

    /// <summary>See <see cref="RemovedGeneralKeys"/>.</summary>
    private static readonly Dictionary<string, string> RemovedLoggingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["console"] = "The log goes to the console whenever there is one; the setting never did anything. Remove it.",
    };

    /// <summary>
    /// Reports anything in a block that is not a name this loader knows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A warning rather than an error, so loading stays total: a config with one
    /// misspelling still produces a usable window manager rather than none.
    /// </para>
    /// <para>
    /// Nothing checked this, so a misspelled section or setting was discarded in
    /// perfect silence and check-config said "ok". Writing <c>focus-follows-mouse</c>
    /// for <c>focus-follows-cursor</c> left the user reading the documentation again
    /// for a setting they had already written correctly by their own reckoning - the
    /// loader knew it was wrong and said nothing.
    /// </para>
    /// </remarks>
    private void WarnAboutUnknown(
        IEnumerable<KdlNode> nodes, string[] known, string what, string code,
        Dictionary<string, string>? removed = null)
    {
        foreach (KdlNode node in nodes)
        {
            if (known.Contains(node.Name, StringComparer.OrdinalIgnoreCase)) continue;

            // A setting that used to exist is named as such, with what replaced it or
            // why nothing did; a guess at the nearest known setting would only ever be
            // the removed one's own name.
            if (removed is not null && removed.TryGetValue(node.Name, out string? advice))
            {
                Report(Diagnostic.Warning(
                    "SHB0455",
                    $"The setting '{node.Name}' has been removed and is ignored.",
                    node.Span,
                    advice));
                continue;
            }

            string? guess = Suggestion.Closest(node.Name, known);

            Report(Diagnostic.Warning(
                code,
                $"Unknown {what} '{node.Name}'; it will be ignored.",
                node.Span,
                guess is null ? null : $"Did you mean '{guess}'?"));
        }
    }

    // ---- general -----------------------------------------------------------

    private ShubbakConfig ApplyGeneral(ShubbakConfig config, KdlNode? node)
    {
        if (node is null) return config;

        WarnAboutUnknown(node.Children, KnownGeneralKeys, "setting in 'general'", "SHB0428", RemovedGeneralKeys);

        List<string> startup = [];
        foreach (KdlNode child in node.ChildrenNamed("startup-command"))
            if (child.Argument(0) is { } value) startup.Add(value.AsString());

        return config with
        {
            ToggleWorkspaceOnRefocus = Bool(node, "toggle-workspace-on-refocus", config.ToggleWorkspaceOnRefocus),
            FollowWindowOnMove = Bool(node, "follow-window-on-move", config.FollowWindowOnMove),
            InitialWindowState = InitialState(node, config.InitialWindowState),
            NewWindowPlacement = Placement(node, config.NewWindowPlacement),
            EmptyWorkspaceFocus = EmptyFocus(node, config.EmptyWorkspaceFocus),
            HideMethod = HideMethod(node, config.HideMethod),
            UnmanagedWindowCommands = UnmanagedCommands(node, config.UnmanagedWindowCommands),
            AllowShellExecOverIpc = Bool(node, "allow-shell-exec-over-ipc", config.AllowShellExecOverIpc),
            AllowConfigEditsOverIpc = Bool(node, "allow-config-edits-over-ipc", config.AllowConfigEditsOverIpc),
            ReloadOnSave = Bool(node, "reload-on-save", config.ReloadOnSave),
            KeepInTaskbar = Bool(node, "keep-in-taskbar", config.KeepInTaskbar),
            DefaultLayout = DefaultLayout(node, config.DefaultLayout),
            StartupCommands = startup,
        };
    }

    /// <summary>Reads and validates <c>default-layout</c>.</summary>
    /// <remarks>
    /// Checked here so an unrecognised name is reported once, at load, rather than
    /// falling back silently on every workspace that is created. The key spent a while
    /// being read and never applied, which looked exactly like a typo would.
    /// </remarks>
    private string? DefaultLayout(KdlNode general, string? fallback)
    {
        string? name = Text(general, "default-layout", fallback);

        if (string.IsNullOrWhiteSpace(name)) return fallback;
        if (Core.Layouts.LayoutRegistry.TryResolve(name, out _)) return name;

        Report(Diagnostic.Error(
            "SHB0113",
            $"Unknown layout '{name}'.",
            SpanOf(general, "default-layout"),
            $"Available: {string.Join(", ", Core.Layouts.LayoutRegistry.CanonicalNames)}."));

        return fallback;
    }

    /// <summary>
    /// Reads <c>new-window-placement</c>: <c>"focus"</c> or <c>"window"</c>.
    /// </summary>
    private NewWindowPlacement Placement(KdlNode node, NewWindowPlacement fallback)
    {
        string? text = Text(node, "new-window-placement", null);
        if (text is null) return fallback;

        switch (text.ToLowerInvariant())
        {
            case "focus": return NewWindowPlacement.FollowFocus;
            case "window": return NewWindowPlacement.FollowWindow;
            default:
                Report(Diagnostic.Error(
                    "SHB0436",
                    $"Unknown new window placement '{text}'.",
                    SpanOf(node, "new-window-placement"),
                    "Use 'focus' to open new windows on the workspace you are looking at, " +
                    "or 'window' to open them on the monitor the window itself appeared on."));
                return fallback;
        }
    }

    /// <summary>
    /// Reads <c>empty-workspace-focus</c>: <c>"hold"</c> or <c>"desktop"</c>.
    /// </summary>
    /// <remarks>
    /// An unrecognised value is an error rather than a silent fallback, for the same
    /// reason as the placement above: the two behaviours are indistinguishable until a
    /// window opens on the wrong monitor, and a typo should not quietly choose one.
    /// </remarks>
    private EmptyWorkspaceFocus EmptyFocus(KdlNode node, EmptyWorkspaceFocus fallback)
    {
        string? text = Text(node, "empty-workspace-focus", null);
        if (text is null) return fallback;

        switch (text.ToLowerInvariant())
        {
            case "hold": return EmptyWorkspaceFocus.Hold;
            case "desktop": return EmptyWorkspaceFocus.Desktop;
            default:
                Report(Diagnostic.Error(
                    "SHB0453",
                    $"Unknown empty workspace focus '{text}'.",
                    SpanOf(node, "empty-workspace-focus"),
                    "Use 'hold' to keep the keyboard on the empty workspace's monitor with " +
                    "an invisible window of Shubbak's own, or 'desktop' to give it to the desktop."));
                return fallback;
        }
    }

    private WindowState InitialState(KdlNode node, WindowState fallback)
    {
        string? text = Text(node, "initial-window-state", null);
        if (text is null) return fallback;

        switch (text.ToLowerInvariant())
        {
            case "tiling": return WindowState.Tiling;
            case "floating": return WindowState.Floating;
            default:
                Report(Diagnostic.Error(
                    "SHB0401",
                    $"Unknown initial window state '{text}'.",
                    SpanOf(node, "initial-window-state"),
                    "Use 'tiling' or 'floating'."));
                return fallback;
        }
    }

    /// <summary>
    /// Reads <c>hide-method</c>: <c>"cloak"</c> or <c>"hide"</c>.
    /// </summary>
    /// <remarks>
    /// An unrecognised value is an error rather than a silent fallback, because
    /// getting this wrong has a severe consequence - with <c>hide</c>, a crash leaves
    /// windows unreachable - and a typo should not quietly select it.
    /// </remarks>
    private WindowHideMethod HideMethod(KdlNode node, WindowHideMethod fallback)
    {
        string? text = Text(node, "hide-method", null);
        if (text is null) return fallback;

        switch (text.ToLowerInvariant())
        {
            case "cloak": return WindowHideMethod.Cloak;
            case "minimise":
            case "minimize": return WindowHideMethod.Minimise;
            case "hide": return WindowHideMethod.Hide;

            default:
                Report(Diagnostic.Error(
                    "SHB0423",
                    $"Unknown hide method '{text}'.",
                    SpanOf(node, "hide-method"),
                    "Use \"cloak\" (recommended), \"minimize\", or \"hide\"."));

                return fallback;
        }
    }

    private UnmanagedWindowCommands UnmanagedCommands(
        KdlNode node, UnmanagedWindowCommands fallback)
    {
        string? text = Text(node, "unmanaged-window-commands", null);
        if (text is null) return fallback;

        switch (text.ToLowerInvariant())
        {
            case "refuse":
            case "reject": return UnmanagedWindowCommands.Refuse;
            case "adopt":
            case "manage": return UnmanagedWindowCommands.Adopt;

            default:
                Report(Diagnostic.Error(
                    "SHB0424",
                    $"Unknown setting '{text}' for unmanaged-window-commands.",
                    SpanOf(node, "unmanaged-window-commands"),
                    "Use \"refuse\" (the default) or \"adopt\"."));

                return fallback;
        }
    }

    // ---- gaps --------------------------------------------------------------

    private ShubbakConfig ApplyGaps(ShubbakConfig config, KdlNode? node) =>
        node is null ? config : ReadGaps(node).Apply(config);

    private ShubbakConfig ApplyEffects(ShubbakConfig config, KdlNode? node) =>
        node is null ? config : ReadEffects(node).Apply(config);

    private ShubbakConfig ApplyAnimation(ShubbakConfig config, KdlNode? node) =>
        node is null ? config : ReadAnimation(node).Apply(config);

    /// <summary>
    /// Reads a <c>gaps</c> block as a delta: only what was written.
    /// </summary>
    /// <remarks>
    /// The top-level section and a context's block are read by this same method. Read
    /// as a delta and then applied, the top-level section layers onto the defaults
    /// exactly as it always did, and a context's block layers onto whatever is
    /// underneath it - which is what lets <c>gaps { inner 0 }</c> inside a context
    /// mean "inner zero, everything else as it was".
    /// </remarks>
    private GapsOverride ReadGaps(KdlNode node)
    {
        WarnAboutUnknown(node.Children, KnownGapsKeys, "setting in 'gaps'", "SHB0428");

        int? inner = OptionalInt(node, "inner");
        int? left = null, top = null, right = null, bottom = null;

        if (node.Child("outer") is { } outerNode)
        {
            // A single positional argument means "the same on all sides".
            if (outerNode.Argument(0) is { } uniform && uniform.TryAsInt(out int all))
            {
                left = top = right = bottom = all;
            }
            else
            {
                left = OptionalInt(outerNode, "left");
                top = OptionalInt(outerNode, "top");
                right = OptionalInt(outerNode, "right");
                bottom = OptionalInt(outerNode, "bottom");
            }
        }

        return new GapsOverride(inner, left, top, right, bottom);
    }

    /// <summary>Reads a <c>window-effects</c> block as a delta.</summary>
    /// <remarks>
    /// This used to rebuild the effects from scratch, with the border defaulting to off
    /// when unspecified. Layering changes nothing for the top-level section - the
    /// defaults underneath it are off and unset - and is what a context needs.
    /// </remarks>
    private EffectsOverride ReadEffects(KdlNode node)
    {
        WarnAboutUnknown(node.Children, KnownEffectsKeys, "setting in 'window-effects'", "SHB0428");

        return new EffectsOverride(
            OptionalBool(node, "border"),
            Text(node, "focused-colour", null) ?? Text(node, "focused-color", null),
            Text(node, "unfocused-colour", null) ?? Text(node, "unfocused-color", null),
            Text(node, "floating-colour", null) ?? Text(node, "floating-color", null),
            Text(node, "floating-unfocused-colour", null) ?? Text(node, "floating-unfocused-color", null));
    }

    /// <summary>Reads an <c>animation</c> block as a delta.</summary>
    private AnimationOverride ReadAnimation(KdlNode node)
    {
        WarnAboutUnknown(node.Children, KnownAnimationKeys, "setting in 'animation'", "SHB0428");

        return new AnimationOverride(
            OptionalBool(node, "enabled"),
            OptionalBool(node, "animate-new-windows"),
            OptionalInt(node, "minimum-distance"),
            ReadFps(node),
            ReadProfile(node, "window-open"),
            ReadProfile(node, "window-move"),
            ReadProfile(node, "layout-change"),
            ReadProfile(node, "workspace-switch"));
    }

    /// <summary>
    /// Reads <c>fps</c>: a number, or <c>"auto"</c> to follow the display. Null when
    /// not written.
    /// </summary>
    /// <remarks>
    /// Clamped rather than rejected when out of range, because a frame rate is a
    /// preference and a configuration that is merely ambitious should still start. The
    /// warning says what was used, so a rate that was silently ignored does not look
    /// like one that was silently honoured.
    /// </remarks>
    private FpsSetting? ReadFps(KdlNode parent)
    {
        KdlNode? node = parent.Child("fps");
        if (node is null || node.Arguments.Count == 0) return null;

        KdlValue argument = node.Arguments[0];

        if (argument.Kind == KdlValueKind.Text &&
            string.Equals(argument.StringValue, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return new FpsSetting(null);
        }

        if (!argument.TryAsInt(out int fps))
        {
            Report(Diagnostic.Error(
                "SHB0437",
                "Animation 'fps' must be a number or \"auto\".",
                argument.Span,
                "Write fps \"auto\" to follow the display's refresh rate, which is the " +
                "default, or a number such as fps 60 to override it."));

            return null;
        }

        int clamped = Math.Clamp(
            fps,
            Core.Animation.AnimationOptions.MinimumFps,
            Core.Animation.AnimationOptions.MaximumFps);

        if (clamped != fps)
        {
            Report(Diagnostic.Warning(
                "SHB0435",
                $"Animation 'fps' of {fps} is outside the supported range " +
                $"{Core.Animation.AnimationOptions.MinimumFps}-" +
                $"{Core.Animation.AnimationOptions.MaximumFps}; {clamped} will be used.",
                argument.Span,
                "Below the minimum the motion reads as a series of jumps. Above the " +
                "maximum the frames are asked for faster than any panel shows them or " +
                "any application repaints them, so the work is done and then discarded."));
        }

        return new FpsSetting(clamped);
    }

    /// <summary>
    /// Reads one animation profile, e.g. <c>window-move duration=140 curve="ease-out"</c>,
    /// as a delta. Null when not written.
    /// </summary>
    private ProfileOverride? ReadProfile(KdlNode parent, string name)
    {
        KdlNode? node = parent.Child(name);
        if (node is null) return null;

        TimeSpan? duration = node.Property("duration") is { } d && d.TryAsInt(out int ms)
            ? TimeSpan.FromMilliseconds(Math.Max(0, ms))
            : null;

        Core.Animation.Easing? curve = null;

        if (node.Property("curve") is { } c)
        {
            string curveName = c.AsString();

            if (Core.Animation.Easing.TryParse(curveName, out Core.Animation.Easing parsed))
            {
                curve = parsed;
            }
            else
            {
                Report(Diagnostic.Warning(
                    "SHB0421",
                    $"Unknown easing curve '{curveName}'; using ease-out.",
                    c.Span,
                    "Available: linear, ease-in, ease-out, ease-in-out, ease-out-back, " +
                    "ease-out-expo, or cubic-bezier(x1, y1, x2, y2)."));

                curve = Core.Animation.Easing.EaseOut;
            }
        }

        return new ProfileOverride(duration, curve);
    }

    /// <summary>
    /// Reads the logging section.
    /// </summary>
    /// <remarks>
    /// Command line flags win over config, because the reason to raise the level is
    /// usually "reproduce this once", and editing a config file to do so - then
    /// remembering to change it back - is friction that stops people bothering.
    /// </remarks>
    private ShubbakConfig ApplyLogging(ShubbakConfig config, KdlNode? node)
    {
        if (node is null) return config;
        WarnAboutUnknown(node.Children, KnownLoggingKeys, "setting in 'logging'", "SHB0428", RemovedLoggingKeys);

        Core.Diagnostics.LogLevel level = config.LogLevel;

        if (node.Child("level")?.Argument(0) is { } levelValue)
        {
            string text = levelValue.AsString();

            if (!Core.Diagnostics.Log.TryParseLevel(text, out level))
            {
                Report(Diagnostic.Error(
                    "SHB0422",
                    $"Unknown log level '{text}'.",
                    levelValue.Span,
                    "Use trace, debug, info, warn, error or none."));

                level = config.LogLevel;
            }
        }

        string? file = Text(node, "file", config.LogFile);

        // An empty path is a common way of writing "the default location"; honour it
        // rather than opening a file called "".
        if (file is not null && file.Trim().Length == 0)
            file = Core.Diagnostics.Log.DefaultLogPath;

        return config with { LogLevel = level, LogFile = file };
    }

    // ---- workspaces --------------------------------------------------------

    private List<WorkspaceConfig> ParseWorkspaces(
        KdlNode? node, IReadOnlyDictionary<string, MonitorDefinition> monitors)
    {
        List<WorkspaceConfig> workspaces = [];
        if (node is null) return workspaces;

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (KdlNode child in node.ChildrenNamed("workspace"))
        {
            if (child.Argument(0) is not { } nameValue)
            {
                Report(Diagnostic.Error(
                    "SHB0402", "A workspace must be given a name.", child.Span,
                    "Write workspace \"3\" display-name=\"Code\"."));
                continue;
            }

            string name = nameValue.AsString();

            if (!seen.Add(name))
            {
                Report(Diagnostic.Warning(
                    "SHB0403",
                    $"Workspace '{name}' is declared more than once; the first declaration wins.",
                    child.Span));
                continue;
            }

            // A name the command language cannot spell is a workspace no key, no bar
            // click and no palette row could ever reach: the tokeniser has no escape,
            // so a name holding both kinds of quote has no written form.
            if (!CommandParser.CanQuote(name))
            {
                Report(Diagnostic.Error(
                    "SHB0454",
                    $"Workspace '{name}' holds both a double and a single quote, which no command can write.",
                    nameValue.Span,
                    "Use one kind of quote in the name, or neither."));
                continue;
            }

            // A misspelt setting on a workspace was discarded in silence, which for the
            // one people reach for by analogy - bind-to-monitor, GlazeWM's name for
            // monitor= - meant the workspace quietly took the primary display.
            WarnAboutUnknownProperties(child, KnownWorkspaceKeys, "setting on a workspace", "SHB0428");

            string? layout = child.Property("layout")?.AsString();

            // Validated here for the same reason default-layout is: an unrecognised
            // name would otherwise fall back to the default in silence, and a
            // workspace quietly not being the layout it says it is looks like the
            // setting being ignored - which it was.
            if (layout is { Length: > 0 } && !Core.Layouts.LayoutRegistry.TryResolve(layout, out _))
            {
                Report(Diagnostic.Error(
                    "SHB0429",
                    $"Workspace '{name}' asks for unknown layout '{layout}'.",
                    child.Property("layout")?.Span ?? child.Span,
                    $"Available: {string.Join(", ", Core.Layouts.LayoutRegistry.CanonicalNames)}."));

                layout = null;
            }

            (int? monitorIndex, string? monitorName) = ReadWorkspaceMonitor(name, child.Property("monitor"), monitors);

            workspaces.Add(new WorkspaceConfig(
                name,
                child.Property("display-name")?.AsString(),
                monitorIndex,
                layout,
                monitorName));
        }

        return workspaces;
    }

    /// <summary>
    /// Reads a workspace's <c>monitor=</c>: a position in the enumeration, or the name
    /// of a declared <c>monitor</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A number is a position, numbered from 0 in the order Windows reports displays.
    /// Anything else is a name and has to have been declared, because a name nothing
    /// declares would leave the workspace on the primary display with nothing to say
    /// why - the same silent fallback the number path already refuses.
    /// </para>
    /// <para>
    /// A quoted number - <c>monitor="1"</c> - is a position too, unless a monitor has
    /// literally been named <c>1</c>. The value type is not a reliable signal of what
    /// was meant: workspace names are written both ways in this project's own config,
    /// and a person who writes one that way will write the other that way.
    /// </para>
    /// </remarks>
    private (int? Index, string? Name) ReadWorkspaceMonitor(
        string workspace, KdlValue? value, IReadOnlyDictionary<string, MonitorDefinition> monitors)
    {
        if (value is null) return (null, null);

        if (value.TryAsInt(out int index))
        {
            // A negative index is never a monitor, and it would silently fall through
            // to the primary rather than being reported.
            if (index < 0)
            {
                Report(Diagnostic.Error(
                    "SHB0430",
                    $"Workspace '{workspace}' asks for monitor {index}.",
                    value.Span,
                    "Monitors are numbered from 0."));

                return (null, null);
            }

            return (index, null);
        }

        string reference = value.AsString();

        if (monitors.ContainsKey(reference)) return (null, reference);

        if (MonitorReference.TryIndex(reference, out int quoted))
        {
            if (quoted >= 0) return (quoted, null);

            Report(Diagnostic.Error(
                "SHB0430",
                $"Workspace '{workspace}' asks for monitor {quoted}.",
                value.Span,
                "Monitors are numbered from 0."));

            return (null, null);
        }

        // A device name - DISPLAY2, with or without the \\.\ - is positional too, and
        // the tree resolves it for itself against whatever is attached. Kept as the
        // name rather than turned into an index here, because which index it is cannot
        // be known until the desktop is.
        if (MonitorReference.IsPositional(reference)) return (null, reference);

        string[] declared = [.. monitors.Keys];
        string? guess = Suggestion.Closest(reference, declared);

        Report(Diagnostic.Error(
            "SHB0442",
            $"Workspace '{workspace}' asks for monitor '{reference}', which is not declared.",
            value.Span,
            declared.Length == 0
                ? "Declare it with monitor \"" + reference + "\" { path ~= \"...\" } - `shubbak monitors` " +
                  "prints a definition for each display - or give a position: monitors are " +
                  "numbered from 0 in the order Windows reports them, so the second is monitor=1."
                : guess is not null
                    ? $"Did you mean '{guess}'?"
                    : $"Declared monitors: {string.Join(", ", declared)}."));

        return (null, null);
    }

    /// <summary>
    /// Reports properties on a node that are not names this loader knows.
    /// </summary>
    /// <remarks>
    /// The property-shaped sibling of <see cref="WarnAboutUnknown"/>, for nodes whose
    /// settings are written as <c>key=value</c> on one line rather than as children.
    /// </remarks>
    private void WarnAboutUnknownProperties(KdlNode node, string[] known, string what, string code)
    {
        foreach ((string key, KdlValue value) in node.Properties)
        {
            if (known.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;

            string? guess = Suggestion.Closest(key, known);

            Report(Diagnostic.Warning(
                code,
                $"Unknown {what} '{key}'; it will be ignored.",
                value.Span,
                guess is null ? null : $"Did you mean '{guess}'?"));
        }
    }

    // ---- keybindings -------------------------------------------------------

    private List<Keybinding> ParseKeybindings(KdlNode? node, IReadOnlyList<WorkspaceConfig> workspaces)
    {
        List<Keybinding> bindings = [];
        if (node is null) return bindings;

        CollectBindings(node, workspaces, bindings);
        WarnOnDuplicates(bindings);

        return bindings;
    }

    private void CollectBindings(
        KdlNode container, IReadOnlyList<WorkspaceConfig> workspaces, List<Keybinding> into,
        bool allowEmpty = false)
    {
        foreach (KdlNode child in container.Children)
        {
            switch (child.Name)
            {
                case "bind":
                    if (ParseBinding(child, substitutions: null, allowEmpty) is { } binding) into.Add(binding);
                    break;

                case "for-each":
                    ExpandForEach(child, workspaces, into, allowEmpty);
                    break;

                default:
                    Report(Diagnostic.Warning(
                        "SHB0404",
                        $"Unexpected '{child.Name}' inside keybindings; expected 'bind' or 'for-each'.",
                        child.NameSpan));
                    break;
            }
        }
    }

    /// <summary>
    /// Expands a <c>for-each</c> template over the declared workspaces.
    /// </summary>
    /// <remarks>
    /// This is the feature that removes the largest source of noise from a real
    /// config. The author's GlazeWM file spends 40 lines on two near-identical
    /// blocks of per-workspace bindings; the same thing here is six lines:
    /// <code>
    /// for-each "workspace" {
    ///   bind "alt+{name}"       { focus --workspace {name} }
    ///   bind "alt+shift+{name}" { move --workspace {name} --focus }
    /// }
    /// </code>
    /// </remarks>
    private void ExpandForEach(
        KdlNode node, IReadOnlyList<WorkspaceConfig> workspaces, List<Keybinding> into, bool allowEmpty = false)
    {
        string source = node.Argument(0)?.AsString() ?? "workspace";

        if (!string.Equals(source, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            Report(Diagnostic.Error(
                "SHB0405",
                $"Unknown for-each source '{source}'.",
                node.Span,
                "The only source available is \"workspace\"."));
            return;
        }

        if (workspaces.Count == 0)
        {
            Report(Diagnostic.Warning(
                "SHB0406",
                "for-each \"workspace\" produced no bindings because no workspaces are declared.",
                node.Span,
                "Declare workspaces before the keybindings section."));
            return;
        }

        foreach (WorkspaceConfig workspace in workspaces)
        {
            Dictionary<string, string> substitutions = new(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = workspace.Name,
                ["display-name"] = workspace.DisplayName ?? workspace.Name,
            };

            foreach (KdlNode child in node.ChildrenNamed("bind"))
                if (ParseBinding(child, substitutions, allowEmpty) is { } binding) into.Add(binding);
        }
    }

    /// <param name="node">The <c>bind</c> node.</param>
    /// <param name="substitutions">Placeholders from an enclosing <c>for-each</c>.</param>
    /// <param name="allowEmpty">
    /// Whether a binding with no commands is meaningful. It is not in the keybindings
    /// section, where it is a key that does nothing and is reported as such; it is inside
    /// a context's <c>bindings</c>, where laying an empty binding over a key is how the
    /// key is disarmed while the context holds.
    /// </param>
    private Keybinding? ParseBinding(
        KdlNode node, IReadOnlyDictionary<string, string>? substitutions, bool allowEmpty = false)
    {
        if (node.Argument(0) is not { } keyValue)
        {
            Report(Diagnostic.Error(
                "SHB0407", "A binding must name a key combination.", node.Span,
                "Write bind \"alt+h\" { focus --direction left }."));
            return null;
        }

        string keyText = Substitute(keyValue.AsString(), substitutions);

        if (!KeyParser.TryParse(keyText, keyValue.Span, out KeyBinding key, out Diagnostic? keyError))
        {
            Report(keyError!);
            return null;
        }

        List<WmCommand> commands = ParseCommandBlock(node, substitutions);

        if (commands.Count == 0 && !allowEmpty)
        {
            Report(Diagnostic.Warning(
                "SHB0408",
                $"Binding '{keyText}' runs no commands, so pressing it will do nothing.",
                node.Span));
            return null;
        }

        bool? repeat = null;

        // Properties on a bind node were read by nothing at all, so the natural thing
        // to write - bind "alt+q" repeat=#false { close } - parsed cleanly, produced no
        // diagnostic, and was silently ignored.
        foreach ((string name, KdlValue value) in node.Properties)
        {
            if (string.Equals(name, "repeat", StringComparison.OrdinalIgnoreCase))
            {
                if (value.TryAsBool(out bool wanted)) repeat = wanted;
                else
                {
                    Report(Diagnostic.Warning(
                        "SHB0432",
                        $"'repeat' on binding '{keyText}' must be #true or #false.",
                        value.Span,
                        "Write repeat=#false to stop the binding running while the key is held."));
                }

                continue;
            }

            Report(Diagnostic.Warning(
                "SHB0433",
                $"Unknown property '{name}' on binding '{keyText}'; it will be ignored.",
                value.Span,
                "The only property a binding takes is repeat=#true or repeat=#false."));
        }

        return new Keybinding(key, commands, node.Span, repeat);
    }

    /// <summary>
    /// Reads the commands inside a block.
    /// </summary>
    /// <remarks>
    /// Each child node is one command, reconstructed from its name and arguments.
    /// Writing <c>focus --direction left</c> as a KDL node rather than a quoted
    /// string keeps the config readable and lets the parser point at the exact
    /// argument that is wrong.
    /// </remarks>
    private List<WmCommand> ParseCommandBlock(
        KdlNode node, IReadOnlyDictionary<string, string>? substitutions)
    {
        List<WmCommand> commands = [];

        foreach (KdlNode child in node.Children)
        {
            // Tokens are passed through directly rather than rebuilt into a string
            // and re-split. Re-splitting would destroy any argument containing a
            // quote - and the author's config has a workspace named `'`.
            List<string> tokens = [Substitute(child.Name, substitutions)];

            foreach (KdlValue argument in child.Arguments)
                tokens.Add(Substitute(argument.AsString(), substitutions));

            string display = string.Join(' ', tokens);

            if (CommandParser.TryParseTokens(tokens, display, child.Span,
                    out WmCommand? command, out Diagnostic? error))
            {
                commands.Add(command!);
            }
            else
            {
                Report(error!);
            }
        }

        return commands;
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, string>? substitutions)
    {
        if (substitutions is null || !text.Contains('{', StringComparison.Ordinal)) return text;

        foreach ((string key, string value) in substitutions)
            text = text.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);

        return text;
    }

    /// <summary>
    /// Warns when two bindings claim the same key.
    /// </summary>
    /// <remarks>
    /// Silent shadowing is a genuinely difficult problem to debug: the binding
    /// appears in the config, looks correct, and simply never fires.
    /// </remarks>
    private void WarnOnDuplicates(List<Keybinding> bindings)
    {
        Dictionary<KeyBinding, Keybinding> seen = [];

        foreach (Keybinding binding in bindings)
        {
            if (seen.TryGetValue(binding.Key, out Keybinding? first))
            {
                Report(Diagnostic.Warning(
                    "SHB0409",
                    $"'{binding.Key.Display}' is bound more than once; the first binding wins.",
                    binding.Span,
                    $"The earlier binding is at line {first.Span.Start.Line}."));
            }
            else
            {
                seen[binding.Key] = binding;
            }
        }
    }

    private List<BindingMode> ParseBindingModes(
        KdlNode? node, IReadOnlyList<WorkspaceConfig> workspaces)
    {
        List<BindingMode> modes = [];
        if (node is null) return modes;

        foreach (KdlNode child in node.ChildrenNamed("mode"))
        {
            if (child.Argument(0) is not { } nameValue)
            {
                Report(Diagnostic.Error("SHB0410", "A binding mode must be named.", child.Span));
                continue;
            }

            List<Keybinding> bindings = [];
            CollectBindings(child, workspaces, bindings);

            string name = nameValue.AsString();
            bool passThrough = Bool(child, "pass-through", false);

            // A mode that swallows every keystroke and has no binding that leaves it
            // is a trap: once entered, the keyboard is inert and no key can undo it.
            // Caught here, where it is a typo being pointed at, rather than at two in
            // the morning with no way to type.
            if (!passThrough && !LeavesTheMode(bindings))
            {
                Report(Diagnostic.Error(
                    "SHB0425",
                    $"Binding mode '{name}' swallows every key and has no binding that leaves it.",
                    child.Span,
                    "Add a way out, e.g. bind \"escape\" { wm-disable-binding-mode }, " +
                    "or set pass-through #true so unbound keys still reach applications."));
            }

            modes.Add(new BindingMode(name, bindings, passThrough));
        }

        return modes;
    }

    /// <summary>Whether any binding in a mode returns to the default set.</summary>
    private static bool LeavesTheMode(List<Keybinding> bindings) =>
        bindings.Any(b => b.Commands.Any(
            c => c is DisableBindingModeCommand or EnableBindingModeCommand));

    // ---- monitors ------------------------------------------------------------

    /// <summary>
    /// Reads the top-level <c>monitor "name" { ... }</c> definitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same shape as <c>app</c>, on purpose: a block of matchers with the same five
    /// operators and the same <c>!</c> negation, so someone who has written one has
    /// written the other. The matchable facts are the ones the display configuration
    /// reports - <c>name</c> from the EDID, <c>path</c> for the connector, <c>device</c>
    /// for the GDI name - plus two booleans, <c>internal</c> and <c>primary</c>.
    /// </para>
    /// <para>
    /// Duplicates keep the first, as workspaces do, and say so. Apps silently keep the
    /// last, which is a different rule for no reason and not one to copy.
    /// </para>
    /// </remarks>
    private Dictionary<string, MonitorDefinition> ParseMonitors(KdlDocument document)
    {
        Dictionary<string, MonitorDefinition> monitors = new(StringComparer.OrdinalIgnoreCase);

        foreach (KdlNode node in document.NodesNamed("monitor"))
        {
            if (node.Argument(0) is not { } nameValue)
            {
                Report(Diagnostic.Error(
                    "SHB0438", "A monitor definition must be named.", node.Span,
                    "Write monitor \"dell-left\" { path ~= \"UID4355\" }. `shubbak monitors` prints one per display."));
                continue;
            }

            string name = nameValue.AsString();

            if (monitors.ContainsKey(name))
            {
                Report(Diagnostic.Warning(
                    "SHB0441",
                    $"Monitor '{name}' is declared more than once; the first declaration wins.",
                    node.Span));
                continue;
            }

            List<MonitorMatcher> matchers = [];
            bool? isInternal = null;
            bool? isPrimary = null;

            foreach (KdlNode child in node.Children)
            {
                string key = child.Name;
                bool negated = key.StartsWith('!');
                if (negated) key = key[1..];

                switch (key.ToLowerInvariant())
                {
                    case "internal":
                        isInternal = ReadMonitorFlag(child, negated);
                        continue;

                    case "primary":
                        isPrimary = ReadMonitorFlag(child, negated);
                        continue;
                }

                MonitorMatchTarget? target = key.ToLowerInvariant() switch
                {
                    "name" or "friendly-name" => MonitorMatchTarget.FriendlyName,
                    "path" or "device-path" => MonitorMatchTarget.DevicePath,
                    "device" or "device-id" or "device-name" => MonitorMatchTarget.DeviceId,
                    _ => null,
                };

                if (target is null)
                {
                    Report(Diagnostic.Error(
                        "SHB0439",
                        $"Unknown monitor matcher '{child.Name}'.",
                        child.Span,
                        "Match on name, path, or device; or say internal #true / primary #true."));
                    continue;
                }

                (MatchOperator op, KdlValue? value) = ReadMatcherOperand(child);

                if (value is null)
                {
                    Report(Diagnostic.Error(
                        "SHB0413",
                        $"Matcher '{child.Name}' has no pattern.",
                        child.Span,
                        "Write name = \"DELL U3219Q\", or path ~= \"UID4355\" for a regex."));
                    continue;
                }

                string pattern = value.AsString();
                ValidatePattern(op, pattern, value.Span);

                matchers.Add(new MonitorMatcher(target.Value, op, pattern, negated, child.Span));
            }

            // Properties on the node itself are accepted for the two flags, so the
            // one-line form reads naturally: monitor "laptop" internal=#true
            if (node.Property("internal") is { } internalProperty)
                isInternal = ReadMonitorFlag(internalProperty, "internal");

            if (node.Property("primary") is { } primaryProperty)
                isPrimary = ReadMonitorFlag(primaryProperty, "primary");

            var definition = new MonitorDefinition(name, matchers, isInternal, isPrimary, node.Span);

            if (!definition.HasConditions)
            {
                Report(Diagnostic.Warning(
                    "SHB0440",
                    $"Monitor '{name}' defines no conditions, so it will never match.",
                    node.Span,
                    "A definition with nothing to check matches no display rather than every display."));
            }

            monitors[name] = definition;
        }

        return monitors;
    }

    /// <summary>Reads <c>internal #true</c>, <c>internal</c> alone, or <c>!internal</c>.</summary>
    private bool? ReadMonitorFlag(KdlNode child, bool negated)
    {
        // Bare, the way the matchers read: `internal` means built in, `!internal`
        // means not. With an argument the argument decides and `!` inverts it.
        bool value = true;

        if (child.Argument(0) is { } argument)
        {
            if (!argument.TryAsBool(out value))
            {
                Report(Diagnostic.Error(
                    "SHB0419",
                    $"'{child.Name}' expects true or false but got '{argument.Raw}'.",
                    argument.Span));

                return null;
            }
        }

        return negated ? !value : value;
    }

    private bool? ReadMonitorFlag(KdlValue value, string name)
    {
        if (value.TryAsBool(out bool result)) return result;

        Report(Diagnostic.Error(
            "SHB0419",
            $"'{name}' expects true or false but got '{value.Raw}'.",
            value.Span));

        return null;
    }

    // ---- contexts ------------------------------------------------------------

    private static readonly string[] KnownContextKeys =
    [
        "when", "linger", "gaps", "window-effects", "animation", "bindings", "rules",
        "workspaces", "on-enter", "on-exit",
    ];

    private static readonly string[] KnownConditions =
    [
        "window", "focused", "fullscreen", "workspace", "monitors", "monitor",
        "display-topology", "remote-session", "system-state", "context",
    ];

    /// <summary>
    /// Reads <c>contexts { context "name" { when { ... } ... } }</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything inside a context reuses the reader for the section it overrides -
    /// <c>gaps</c>, <c>window-effects</c> and <c>animation</c> come out as deltas from
    /// the same code that reads the top-level sections, <c>bindings</c> is the
    /// keybindings reader, <c>rules</c> the rules reader, <c>on-enter</c> a command block
    /// - so there is no second vocabulary and a mistake is reported with the same code
    /// it would get at the top level.
    /// </para>
    /// <para>
    /// References to other contexts are checked after every context has been read, so a
    /// context may name one declared below it. What it may not do is name itself by any
    /// route; a cycle is reported and the offending reference dropped.
    /// </para>
    /// </remarks>
    private List<ContextDefinition> ParseContexts(
        KdlNode? node,
        IReadOnlyDictionary<string, AppDefinition> apps,
        IReadOnlyDictionary<string, MonitorDefinition> monitors,
        IReadOnlyList<WorkspaceConfig> workspaces)
    {
        List<ContextDefinition> contexts = [];
        if (node is null) return contexts;

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (KdlNode child in node.ChildrenNamed("context"))
        {
            if (child.Argument(0) is not { } nameValue)
            {
                Report(Diagnostic.Error(
                    "SHB0444", "A context must be named.", child.Span,
                    "Write context \"presenting\" { when { window app=\"slides\" } }."));
                continue;
            }

            string name = nameValue.AsString();

            if (!seen.Add(name))
            {
                Report(Diagnostic.Warning(
                    "SHB0445",
                    $"Context '{name}' is declared more than once; the first declaration wins.",
                    child.Span));
                continue;
            }

            WarnAboutUnknown(child.Children, KnownContextKeys, $"setting in context '{name}'", "SHB0428");

            List<IReadOnlyList<ContextCondition>> when = [];

            foreach (KdlNode block in child.ChildrenNamed("when"))
            {
                List<ContextCondition> conditions = ParseConditions(block, name, apps, monitors);

                if (conditions.Count == 0)
                {
                    // Not "matches everything". An empty block would make the context
                    // permanently active, which is the one reading nobody who wrote an
                    // empty block meant.
                    Report(Diagnostic.Error(
                        "SHB0449",
                        $"A 'when' block in context '{name}' has no conditions, so it is ignored.",
                        block.Span,
                        "Put a condition in it, or remove it. A context with no 'when' at all is " +
                        "external: only `context --set` can turn it on."));
                    continue;
                }

                when.Add(conditions);
            }

            TimeSpan linger = ContextDefinition.DefaultLinger;

            if (OptionalInt(child, "linger") is { } ms)
            {
                if (ms < 0)
                {
                    Report(Diagnostic.Error(
                        "SHB0448",
                        $"Context '{name}' has a negative linger.",
                        SpanOf(child, "linger"),
                        "linger is how many milliseconds the conditions must have stopped holding " +
                        "before the context lets go; 0 means at once."));
                }
                else
                {
                    linger = TimeSpan.FromMilliseconds(ms);
                }
            }

            List<Keybinding> bindings = [];

            if (child.Child("bindings") is { } bindingsNode)
            {
                CollectBindings(bindingsNode, workspaces, bindings, allowEmpty: true);
                WarnOnDuplicates(bindings);
            }

            var effects = new ContextEffects(
                child.Child("gaps") is { } gaps ? ReadGaps(gaps) : null,
                child.Child("window-effects") is { } look ? ReadEffects(look) : null,
                child.Child("animation") is { } motion ? ReadAnimation(motion) : null,
                bindings,
                ParseRules(child.ChildrenNamed("rules"), apps),
                ParseWorkspaceHomes(child.Child("workspaces"), name, monitors, workspaces),
                child.Child("on-enter") is { } enter ? ParseCommandBlock(enter, null) : [],
                child.Child("on-exit") is { } exit ? ParseCommandBlock(exit, null) : []);

            contexts.Add(new ContextDefinition(name, when, linger, effects, child.Span));
        }

        return ResolveContextReferences(contexts);
    }

    private List<ContextCondition> ParseConditions(
        KdlNode block,
        string context,
        IReadOnlyDictionary<string, AppDefinition> apps,
        IReadOnlyDictionary<string, MonitorDefinition> monitors)
    {
        List<ContextCondition> conditions = [];

        foreach (KdlNode node in block.Children)
        {
            string name = node.Name;
            bool negated = name.StartsWith('!');
            if (negated) name = name[1..];

            ContextCondition? condition = name.ToLowerInvariant() switch
            {
                "window" => ParseWindowCondition(node, WindowConditionKind.Present, negated, apps),
                "focused" => ParseWindowCondition(node, WindowConditionKind.Focused, negated, apps),
                "fullscreen" => ParseWindowCondition(node, WindowConditionKind.Fullscreen, negated, apps),
                "workspace" => ParseWorkspaceCondition(node, negated),
                "monitors" => ParseMonitorCountCondition(node, negated),
                "monitor" => ParseMonitorPresentCondition(node, negated, monitors),
                "display-topology" => ParseTopologyCondition(node, negated),
                "remote-session" => ParseRemoteSessionCondition(node, negated),
                "system-state" => ParseSystemStateCondition(node, negated),
                "context" => ParseContextReference(node, negated),
                _ => Unknown(node, context),
            };

            if (condition is not null) conditions.Add(condition);
        }

        return conditions;

        ContextCondition? Unknown(KdlNode node, string context)
        {
            string bare = node.Name.TrimStart('!');
            string? guess = Suggestion.Closest(bare, KnownConditions);

            Report(Diagnostic.Error(
                "SHB0446",
                $"Unknown condition '{node.Name}' in context '{context}'.",
                node.Span,
                guess is not null
                    ? $"Did you mean '{guess}'?"
                    : $"Conditions: {string.Join(", ", KnownConditions)}."));

            return null;
        }
    }

    private WindowCondition? ParseWindowCondition(
        KdlNode node, WindowConditionKind kind, bool negated, IReadOnlyDictionary<string, AppDefinition> apps)
    {
        string? app = node.Property("app")?.AsString();

        if (app is not null && !apps.ContainsKey(app))
        {
            string? guess = Suggestion.Closest(app, [.. apps.Keys]);

            Report(Diagnostic.Error(
                "SHB0447",
                $"'{node.Name}' refers to app '{app}', which is not declared.",
                node.Property("app")!.Span,
                guess is not null ? $"Did you mean '{guess}'?" : "Declare it with app \"" + app + "\" { process = \"...\" }."));

            return null;
        }

        // Inline matchers, the way a rule's match block takes them; the property form
        // above is the compact spelling and the two may be combined.
        List<WindowMatcher> matchers = ParseMatchers(node);

        var condition = new WindowCondition(kind, app, matchers, negated, node.Span);

        if (condition.IsUnconstrained && kind != WindowConditionKind.Fullscreen)
        {
            // "Some window exists" is true of every desktop with a window on it.
            // Full-screen without a subject is the one that means something on its own.
            Report(Diagnostic.Error(
                "SHB0448",
                $"'{node.Name}' does not say which window.",
                node.Span,
                $"Write {node.Name} app=\"slides\", or {node.Name} {{ process = \"POWERPNT\" }}."));

            return null;
        }

        return condition;
    }

    private WorkspaceCondition? ParseWorkspaceCondition(KdlNode node, bool negated)
    {
        KdlValue? active = node.Property("active");
        KdlValue? focused = node.Property("focused");

        if ((active is null) == (focused is null))
        {
            Report(Diagnostic.Error(
                "SHB0448",
                active is null
                    ? "'workspace' does not say which workspace, or how."
                    : "'workspace' gives both active= and focused=.",
                node.Span,
                "Write workspace active=\"3\" for one that is on a screen, or workspace " +
                "focused=\"3\" for the one being worked on."));

            return null;
        }

        return new WorkspaceCondition((active ?? focused)!.AsString(), focused is not null, negated, node.Span);
    }

    private MonitorCountCondition? ParseMonitorCountCondition(KdlNode node, bool negated)
    {
        int? exactly = OptionalIntProperty(node, "count");
        int? min = OptionalIntProperty(node, "min");
        int? max = OptionalIntProperty(node, "max");

        if (exactly is null && min is null && max is null)
        {
            Report(Diagnostic.Error(
                "SHB0448",
                "'monitors' does not say how many.",
                node.Span,
                "Write monitors count=2, monitors min=2, or monitors max=1."));

            return null;
        }

        if (exactly < 0 || min < 0 || max < 0)
        {
            Report(Diagnostic.Error(
                "SHB0448", "'monitors' is given a negative count.", node.Span,
                "Counts are zero or more."));

            return null;
        }

        return new MonitorCountCondition(exactly, min, max, negated, node.Span);
    }

    private MonitorPresentCondition? ParseMonitorPresentCondition(
        KdlNode node, bool negated, IReadOnlyDictionary<string, MonitorDefinition> monitors)
    {
        KdlValue? present = node.Property("present");
        KdlValue? absent = node.Property("absent");

        if ((present is null) == (absent is null))
        {
            Report(Diagnostic.Error(
                "SHB0448",
                present is null
                    ? "'monitor' does not say which monitor, or whether it should be there."
                    : "'monitor' gives both present= and absent=.",
                node.Span,
                "Write monitor present=\"dell-left\" or monitor absent=\"dell-left\", naming a " +
                "declared monitor."));

            return null;
        }

        KdlValue value = (present ?? absent)!;
        string monitor = value.AsString();

        if (!monitors.ContainsKey(monitor))
        {
            string[] declared = [.. monitors.Keys];
            string? guess = Suggestion.Closest(monitor, declared);

            Report(Diagnostic.Error(
                "SHB0447",
                $"'monitor' refers to '{monitor}', which is not a declared monitor.",
                value.Span,
                declared.Length == 0
                    ? "Declare it with monitor \"" + monitor + "\" { path *= \"...\" }; `shubbak monitors` prints one per display."
                    : guess is not null
                        ? $"Did you mean '{guess}'?"
                        : $"Declared monitors: {string.Join(", ", declared)}."));

            return null;
        }

        // `absent` is the negated spelling; `!monitor absent=` is present again.
        return new MonitorPresentCondition(monitor, negated ^ (absent is not null), node.Span);
    }

    private TopologyCondition? ParseTopologyCondition(KdlNode node, bool negated)
    {
        List<Core.Wm.DisplayTopologyKind> kinds = [];

        foreach (KdlValue argument in node.Arguments)
        {
            string word = argument.AsString();

            if (Core.Wm.DisplayTopologyNames.Parse(word) is { } kind && kind != Core.Wm.DisplayTopologyKind.Unknown)
            {
                kinds.Add(kind);
                continue;
            }

            Report(Diagnostic.Error(
                "SHB0448",
                $"'{word}' is not a display topology.",
                argument.Span,
                $"One of: {string.Join(", ", Core.Wm.DisplayTopologyNames.Accepted)} - the four choices on the Win+P panel."));
        }

        if (kinds.Count == 0)
        {
            if (node.Arguments.Count == 0)
            {
                Report(Diagnostic.Error(
                    "SHB0448", "'display-topology' does not say which.", node.Span,
                    $"Write display-topology \"extend\"; one of {string.Join(", ", Core.Wm.DisplayTopologyNames.Accepted)}."));
            }

            return null;
        }

        return new TopologyCondition(kinds, negated, node.Span);
    }

    private RemoteSessionCondition? ParseRemoteSessionCondition(KdlNode node, bool negated)
    {
        // Bare means true, the way the matchers read; an argument decides, and `!`
        // inverts whatever it decided.
        if (node.Argument(0) is { } argument)
        {
            if (!argument.TryAsBool(out bool wanted))
            {
                Report(Diagnostic.Error(
                    "SHB0419",
                    $"'remote-session' expects true or false but got '{argument.Raw}'.",
                    argument.Span));

                return null;
            }

            negated ^= !wanted;
        }

        return new RemoteSessionCondition(negated, node.Span);
    }

    private SystemStateCondition? ParseSystemStateCondition(KdlNode node, bool negated)
    {
        List<Core.Wm.UserActivity> states = [];

        foreach (KdlValue argument in node.Arguments)
        {
            string word = argument.AsString();

            if (Core.Wm.UserActivityNames.Parse(word) is { } state)
            {
                states.Add(state);
                continue;
            }

            Report(Diagnostic.Error(
                "SHB0448",
                $"'{word}' is not a system state.",
                argument.Span,
                "One of: ordinary, presenting, fullscreen-app, fullscreen-game, quiet-time, away."));
        }

        if (states.Count == 0)
        {
            if (node.Arguments.Count == 0)
            {
                Report(Diagnostic.Error(
                    "SHB0448", "'system-state' does not say which.", node.Span,
                    "Write system-state \"presenting\"; one of ordinary, presenting, fullscreen-app, " +
                    "fullscreen-game, quiet-time, away."));
            }

            return null;
        }

        return new SystemStateCondition(states, negated, node.Span);
    }

    private ContextReferenceCondition? ParseContextReference(KdlNode node, bool negated)
    {
        if (node.Argument(0) is not { } argument)
        {
            Report(Diagnostic.Error(
                "SHB0448", "'context' does not name a context.", node.Span,
                "Write context \"meeting\", naming another declared context."));

            return null;
        }

        return new ContextReferenceCondition(argument.AsString(), negated, node.Span);
    }

    private int? OptionalIntProperty(KdlNode node, string name)
    {
        if (node.Property(name) is not { } value) return null;

        if (value.TryAsInt(out int result)) return result;

        Report(Diagnostic.Error(
            "SHB0420",
            $"'{name}' expects a whole number but got '{value.Raw}'.",
            value.Span));

        return null;
    }

    /// <summary>
    /// Reads a context's <c>workspaces { workspace "3" monitor="projector" }</c>.
    /// </summary>
    /// <remarks>
    /// Only <c>monitor=</c>, because a context re-homes a workspace and nothing else
    /// about it; and only for a workspace declared at the top level, because a
    /// workspace that exists on demand has no home to override.
    /// </remarks>
    private List<WorkspaceHome> ParseWorkspaceHomes(
        KdlNode? node,
        string context,
        IReadOnlyDictionary<string, MonitorDefinition> monitors,
        IReadOnlyList<WorkspaceConfig> workspaces)
    {
        List<WorkspaceHome> homes = [];
        if (node is null) return homes;

        foreach (KdlNode child in node.ChildrenNamed("workspace"))
        {
            if (child.Argument(0) is not { } nameValue)
            {
                Report(Diagnostic.Error(
                    "SHB0402", "A workspace must be given a name.", child.Span,
                    "Write workspace \"3\" monitor=\"projector\"."));
                continue;
            }

            string name = nameValue.AsString();

            WarnAboutUnknownProperties(child, ["monitor"], $"setting on a workspace in context '{context}'", "SHB0428");

            if (!workspaces.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Report(Diagnostic.Error(
                    "SHB0448",
                    $"Context '{context}' re-homes workspace '{name}', which is not declared in workspaces {{ }}.",
                    nameValue.Span,
                    "A context can move a declared workspace to another monitor while it holds; declare " +
                    "the workspace first."));
                continue;
            }

            if (child.Property("monitor") is not { } monitor)
            {
                Report(Diagnostic.Error(
                    "SHB0448",
                    $"Workspace '{name}' in context '{context}' does not say which monitor.",
                    child.Span,
                    "Write workspace \"" + name + "\" monitor=\"projector\"."));
                continue;
            }

            (int? index, string? monitorName) = ReadWorkspaceMonitor(name, monitor, monitors);

            if (index is null && monitorName is null) continue;

            homes.Add(new WorkspaceHome(name, index, monitorName, child.Span));
        }

        return homes;
    }

    /// <summary>
    /// Checks every <c>context "x"</c> condition against the declared contexts, and
    /// drops the ones that would make a context depend on itself.
    /// </summary>
    /// <remarks>
    /// A cycle is not merely a mistake; it is undecidable. "Presenting holds when meeting
    /// holds, and meeting holds when presenting holds" has two consistent answers, and
    /// an evaluator that picked one would be right by accident. The reference that
    /// closes the cycle is reported and removed, and a block left empty by that removal
    /// goes with it, so what remains is decidable.
    /// </remarks>
    private List<ContextDefinition> ResolveContextReferences(List<ContextDefinition> contexts)
    {
        Dictionary<string, ContextDefinition> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ContextDefinition context in contexts) byName[context.Name] = context;

        // Undeclared references first, so a cycle check only ever sees real edges.
        for (int i = 0; i < contexts.Count; i++)
        {
            contexts[i] = contexts[i] with
            {
                When = contexts[i].When
                    .Select(block => (IReadOnlyList<ContextCondition>)[.. block.Where(c => !IsUndeclared(c, contexts[i].Name))])
                    .Where(block => block.Count > 0)
                    .ToList(),
            };
        }

        // Then cycles, one edge at a time until none remain.
        bool removed;

        do
        {
            removed = false;

            for (int i = 0; i < contexts.Count && !removed; i++)
            {
                foreach (ContextReferenceCondition reference in contexts[i].AllConditions.OfType<ContextReferenceCondition>())
                {
                    if (!Reaches(reference.Context, contexts[i].Name, byName, [])) continue;

                    Report(Diagnostic.Error(
                        "SHB0450",
                        $"Context '{contexts[i].Name}' depends on '{reference.Context}', which depends on it; the reference is ignored.",
                        reference.Span,
                        "A context cannot decide itself. Break the loop: one of the two has to be decided by something else."));

                    contexts[i] = contexts[i] with
                    {
                        When = contexts[i].When
                            .Select(block => (IReadOnlyList<ContextCondition>)[.. block.Where(c => !ReferenceEquals(c, reference))])
                            .Where(block => block.Count > 0)
                            .ToList(),
                    };

                    byName[contexts[i].Name] = contexts[i];
                    removed = true;
                    break;
                }
            }
        }
        while (removed);

        return contexts;

        bool IsUndeclared(ContextCondition condition, string owner)
        {
            if (condition is not ContextReferenceCondition reference) return false;
            if (byName.ContainsKey(reference.Context)) return false;

            string? guess = Suggestion.Closest(reference.Context, [.. byName.Keys]);

            Report(Diagnostic.Error(
                "SHB0447",
                $"Context '{owner}' refers to context '{reference.Context}', which is not declared.",
                reference.Span,
                guess is not null ? $"Did you mean '{guess}'?" : $"Declared contexts: {string.Join(", ", byName.Keys)}."));

            return true;
        }

        static bool Reaches(string from, string target, Dictionary<string, ContextDefinition> byName, HashSet<string> visited)
        {
            if (string.Equals(from, target, StringComparison.OrdinalIgnoreCase)) return true;
            if (!visited.Add(from)) return false;
            if (!byName.TryGetValue(from, out ContextDefinition? context)) return false;

            foreach (ContextReferenceCondition next in context.AllConditions.OfType<ContextReferenceCondition>())
                if (Reaches(next.Context, target, byName, visited)) return true;

            return false;
        }
    }

    // ---- apps and rules ----------------------------------------------------

    private Dictionary<string, AppDefinition> ParseApps(KdlDocument document)
    {
        Dictionary<string, AppDefinition> apps = new(StringComparer.OrdinalIgnoreCase);

        foreach (KdlNode node in document.NodesNamed("app"))
        {
            if (node.Argument(0) is not { } nameValue)
            {
                Report(Diagnostic.Error(
                    "SHB0411", "An app definition must be named.", node.Span,
                    "Write app \"firefox\" { process = \"firefox\" }."));
                continue;
            }

            string name = nameValue.AsString();
            List<WindowMatcher> matchers = ParseMatchers(node);

            if (matchers.Count == 0)
            {
                Report(Diagnostic.Warning(
                    "SHB0412",
                    $"App '{name}' defines no conditions, so it will never match.",
                    node.Span));
            }

            apps[name] = new AppDefinition(name, matchers, node.Span);
        }

        return apps;
    }

    private List<WindowMatcher> ParseMatchers(KdlNode node)
    {
        List<WindowMatcher> matchers = [];

        foreach (KdlNode child in node.Children)
        {
            string name = child.Name;
            bool negated = name.StartsWith('!');
            if (negated) name = name[1..];

            MatchTarget? target = name.ToLowerInvariant() switch
            {
                "title" => MatchTarget.Title,
                "class" or "class-name" => MatchTarget.ClassName,
                "process" or "process-name" => MatchTarget.ProcessName,
                "path" or "process-path" => MatchTarget.ProcessPath,
                _ => null,
            };

            if (target is null)
            {
                // `app` is a reference to a named definition, handled by the caller.
                if (string.Equals(name, "app", StringComparison.OrdinalIgnoreCase)) continue;

                // Everything else here is a mistake worth naming. Dropped in silence
                // before, so a misspelt target left the rule matching on whatever else
                // was in the block - or, if it was the only one, on nothing at all.
                Report(Diagnostic.Error(
                    "SHB0426",
                    $"Unknown matcher '{child.Name}'.",
                    child.Span,
                    "Match on title, class, process, or path."));

                continue;
            }

            (MatchOperator op, KdlValue? value) = ReadMatcherOperand(child);

            if (value is null)
            {
                Report(Diagnostic.Error(
                    "SHB0413",
                    $"Matcher '{child.Name}' has no pattern.",
                    child.Span,
                    "Write title = \"Untitled\", or title ~= \"^Untitled\" for a regex."));
                continue;
            }

            string pattern = value.AsString();
            ValidatePattern(op, pattern, value.Span);

            matchers.Add(new WindowMatcher(target.Value, op, pattern, negated, child.Span));
        }

        return matchers;
    }

    private static (MatchOperator Operator, KdlValue? Value) ReadMatcherOperand(KdlNode node)
    {
        // `title = "x"` parses as a bare argument; `title regex="x"` as a property.
        // Supporting both keeps simple cases terse and complex ones explicit.
        //
        // The symbolic spellings arrive here as properties too, and that is not
        // obvious: KDL excludes `=` from identifiers, so `title ~= "x"` is read as the
        // property `~` with the value `"x"`, never as an operator token followed by a
        // pattern. All four were documented and none of them worked - they reported
        // "matcher has no pattern", which reads as the pattern being at fault.
        foreach ((string key, KdlValue value) in node.Properties)
        {
            MatchOperator? op = key.ToLowerInvariant() switch
            {
                "equals" or "is" => MatchOperator.Equals,
                "regex" or "matches" or "~" => MatchOperator.Regex,
                "starts-with" or "prefix" or "^" => MatchOperator.StartsWith,
                "ends-with" or "suffix" or "$" => MatchOperator.EndsWith,
                "contains" or "*" => MatchOperator.Contains,
                _ => null,
            };

            if (op is not null) return (op.Value, value);
        }

        // A leading operator token, e.g. `title ~= "..."`.
        if (node.Argument(0) is { } first)
        {
            string raw = first.AsString();

            MatchOperator? op = raw switch
            {
                "=" or "==" => MatchOperator.Equals,
                "~=" or "=~" => MatchOperator.Regex,
                "^=" => MatchOperator.StartsWith,
                "$=" => MatchOperator.EndsWith,
                "*=" => MatchOperator.Contains,
                _ => null,
            };

            if (op is not null) return (op.Value, node.Argument(1));

            return (MatchOperator.Equals, first);
        }

        return (MatchOperator.Equals, null);
    }

    /// <summary>
    /// Checks a pattern for mistakes that would otherwise fail silently.
    /// </summary>
    /// <remarks>
    /// The slash-delimited check exists because of a real line in the author's
    /// GlazeWM config:
    /// <c>window_title: { regex: "/[Pp]ower[Pp]oint [Ss]lide [Ss]how.*/" }</c>.
    /// The slashes are literal characters there, so the pattern only matches titles
    /// that genuinely begin and end with a slash - meaning that rule has never once
    /// fired, and nothing ever said so.
    /// </remarks>
    private void ValidatePattern(MatchOperator op, string pattern, TextSpan span)
    {
        if (op != MatchOperator.Regex) return;

        if (pattern.Length >= 2 && pattern[0] == '/' && pattern[^1] == '/')
        {
            Report(Diagnostic.Warning(
                "SHB0414",
                "This regex is wrapped in slashes, which are matched literally.",
                span,
                $"Shubbak patterns are not slash-delimited. Write \"{pattern.Trim('/')}\" instead."));
        }

        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern);
        }
        catch (ArgumentException ex)
        {
            Report(Diagnostic.Error(
                "SHB0415", $"Invalid regular expression: {ex.Message}", span));
        }
    }

    /// <summary>Reads every rule in every <c>rules { }</c> block given, in order.</summary>
    /// <param name="blocks">The blocks, in file order. Their rules are concatenated.</param>
    /// <param name="apps">The app definitions a rule may refer to.</param>
    private List<WindowRule> ParseRules(IEnumerable<KdlNode> blocks, IReadOnlyDictionary<string, AppDefinition> apps)
    {
        List<WindowRule> rules = [];

        // Numbered across blocks, so an unnamed rule in a second block does not share
        // "rule #1" with an unnamed rule in the first.
        int ordinal = 0;

        foreach (KdlNode node in blocks)
        {
            foreach (KdlNode child in node.ChildrenNamed("rule"))
            {
                ordinal++;

                if (ParseRule(child, ordinal, apps) is { } rule) rules.Add(rule);
            }
        }

        return rules;
    }

    /// <summary>The default name of an unnamed rule, as a report will show it.</summary>
    /// <remarks>
    /// Public because a tool removing a rule by the name a report gave it has to
    /// recognise the name as one the loader invented rather than one the user wrote.
    /// </remarks>
    public static string DefaultRuleName(int ordinal) => $"rule #{ordinal}";

    /// <summary>Reads one <c>rule</c> node, or null when it is not worth keeping.</summary>
    private WindowRule? ParseRule(KdlNode child, int ordinal, IReadOnlyDictionary<string, AppDefinition> apps)
    {
        string name = child.Argument(0)?.AsString() ?? DefaultRuleName(ordinal);

        string triggerName = (Text(child, "on", "manage") ?? "manage").ToLowerInvariant();

        RuleTrigger trigger = triggerName switch
        {
            "manage" => RuleTrigger.OnManage,
            "title-change" => RuleTrigger.OnTitleChange,
            "focus" => RuleTrigger.OnFocus,
            _ => RuleTrigger.OnManage,
        };

        // Reported rather than assumed. Falling back to "manage" meant a rule
        // written on="titel-change" ran at a completely different moment from the
        // one intended, and looked from the outside like the rule not matching.
        if (triggerName is not ("manage" or "title-change" or "focus"))
        {
            Report(Diagnostic.Error(
                "SHB0431",
                $"Rule '{name}' has unknown trigger '{triggerName}'.",
                SpanOf(child, "on"),
                "Use on=\"manage\" (the default), on=\"title-change\", or on=\"focus\"."));
        }

        List<WindowMatcher> matchers = [];
        List<string> appReferences = [];

        if (child.Child("match") is { } match)
        {
            matchers = ParseMatchers(match);

            foreach (KdlValue value in match.ChildrenNamed("app").SelectMany(a => a.Arguments))
            {
                string reference = value.AsString();

                if (!apps.ContainsKey(reference))
                {
                    Report(Diagnostic.Error(
                        "SHB0416",
                        $"Rule '{name}' references app '{reference}', which is not defined.",
                        value.Span,
                        $"Define it with: app \"{reference}\" {{ process = \"...\" }}"));
                    continue;
                }

                appReferences.Add(reference);
            }
        }

        List<WmCommand> commands = child.Child("do") is { } doBlock
            ? ParseCommandBlock(doBlock, null)
            : [];

        if (matchers.Count == 0 && appReferences.Count == 0)
        {
            Report(Diagnostic.Error(
                "SHB0417",
                $"Rule '{name}' has no conditions, so it would match every window.",
                child.Span,
                "Add a match block, e.g. match { process = \"firefox\" }."));
            return null;
        }

        if (commands.Count == 0)
        {
            Report(Diagnostic.Warning(
                "SHB0418", $"Rule '{name}' runs no commands.", child.Span));
            return null;
        }

        // The two adoption verbs are consulted only when a window is first considered,
        // which is the manage trigger and no other. Written under title-change or
        // focus they parsed, loaded, and were then stripped out at the moment the rule
        // fired - so a rule that plainly said `ignore` did nothing at all, and the
        // report showed it matching. The rule is kept for whatever else it runs.
        if (trigger is not RuleTrigger.OnManage)
        {
            foreach (WmCommand command in commands)
            {
                if (command is not (IgnoreCommand or ManageCommand)) continue;

                Report(Diagnostic.Warning(
                    "SHB0452",
                    $"Rule '{name}' runs '{command.Name}' on=\"{triggerName}\", where it does nothing.",
                    SpanOf(child, "on"),
                    $"'{command.Name}' decides whether a window is taken on at all, so it only " +
                    "acts in a rule with on=\"manage\" (the default). Move it to one, or drop it."));
            }
        }

        return new WindowRule(name, trigger, matchers, appReferences, commands, child.Span);
    }

    // ---- value helpers -----------------------------------------------------

    private void Report(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    /// <summary>Reads a setting written either as a child node or as a property.</summary>
    /// <remarks>
    /// <para>
    /// Both spellings appear throughout a real config - <c>border #true</c> as a child,
    /// <c>monitor=0</c> as a property - and which one a given setting wanted was not
    /// discoverable. Reading only children meant the property form was ignored in
    /// silence rather than rejected.
    /// </para>
    /// <para>
    /// That was worst for <c>pass-through</c>. Written as a property it did nothing,
    /// so a mode meant to leave the keyboard usable swallowed every key instead, and
    /// the config said plainly that it should not.
    /// </para>
    /// </remarks>
    private static KdlValue? SettingValue(KdlNode parent, string name) =>
        parent.Child(name)?.Argument(0) ?? parent.Property(name);

    private static KdlNode? Setting(KdlNode parent, string name) => parent.Child(name);

    private bool Bool(KdlNode parent, string name, bool fallback)
    {
        if (SettingValue(parent, name) is not { } value) return fallback;

        if (value.TryAsBool(out bool result)) return result;

        Report(Diagnostic.Error(
            "SHB0419",
            $"'{name}' expects true or false but got '{value.Raw}'.",
            value.Span));

        return fallback;
    }

    private int Int(KdlNode parent, string name, int fallback)
    {
        if (SettingValue(parent, name) is not { } value) return fallback;

        if (value.TryAsInt(out int result)) return result;

        Report(Diagnostic.Error(
            "SHB0420",
            $"'{name}' expects a whole number but got '{value.Raw}'.",
            value.Span));

        return fallback;
    }

    /// <summary>A setting's value when written, null when not - and null on a type error, once reported.</summary>
    private bool? OptionalBool(KdlNode parent, string name)
    {
        if (SettingValue(parent, name) is not { } value) return null;

        if (value.TryAsBool(out bool result)) return result;

        Report(Diagnostic.Error(
            "SHB0419",
            $"'{name}' expects true or false but got '{value.Raw}'.",
            value.Span));

        return null;
    }

    private int? OptionalInt(KdlNode parent, string name)
    {
        if (SettingValue(parent, name) is not { } value) return null;

        if (value.TryAsInt(out int result)) return result;

        Report(Diagnostic.Error(
            "SHB0420",
            $"'{name}' expects a whole number but got '{value.Raw}'.",
            value.Span));

        return null;
    }

    private static string? Text(KdlNode parent, string name, string? fallback) =>
        SettingValue(parent, name) is { } value ? value.AsString() : fallback;

    private static TextSpan SpanOf(KdlNode parent, string name) =>
        Setting(parent, name)?.Span ?? parent.Span;
}