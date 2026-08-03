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

        return Load(File.ReadAllText(path));
    }

    private ShubbakConfig Build(KdlDocument document)
    {
        var config = ShubbakConfig.Default;

        config = ApplyGeneral(config, document.Node("general"));
        config = ApplyGaps(config, document.Node("gaps"));
        config = ApplyEffects(config, document.Node("window-effects"));
        config = ApplyAnimation(config, document.Node("animation"));
        config = ApplyLogging(config, document.Node("logging"));

        Dictionary<string, AppDefinition> apps = ParseApps(document);
        List<WorkspaceConfig> workspaces = ParseWorkspaces(document.Node("workspaces"));

        return config with
        {
            Apps = apps,
            Workspaces = workspaces,
            Keybindings = ParseKeybindings(document.Node("keybindings"), workspaces),
            BindingModes = ParseBindingModes(document.Node("binding-modes"), workspaces),
            Rules = ParseRules(document.Node("rules"), apps),
        };
    }

    // ---- general -----------------------------------------------------------

    private ShubbakConfig ApplyGeneral(ShubbakConfig config, KdlNode? node)
    {
        if (node is null) return config;

        List<string> startup = [];
        foreach (KdlNode child in node.ChildrenNamed("startup-command"))
            if (child.Argument(0) is { } value) startup.Add(value.AsString());

        return config with
        {
            FocusFollowsCursor = Bool(node, "focus-follows-cursor", config.FocusFollowsCursor),
            ToggleWorkspaceOnRefocus = Bool(node, "toggle-workspace-on-refocus", config.ToggleWorkspaceOnRefocus),
            FollowWindowOnMove = Bool(node, "follow-window-on-move", config.FollowWindowOnMove),
            CursorJumpOnMonitorFocus = CursorJump(node, "monitor"),
            CursorJumpOnWindowFocus = CursorJump(node, "window"),
            InitialWindowState = InitialState(node, config.InitialWindowState),
            HideMethod = HideMethod(node, config.HideMethod),
            UnmanagedWindowCommands = UnmanagedCommands(node, config.UnmanagedWindowCommands),
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

    private bool CursorJump(KdlNode general, string trigger)
    {
        KdlNode? jump = general.Child("cursor-jump");
        if (jump is null) return false;

        if (!Bool(jump, "enabled", true)) return false;

        string configured = Text(jump, "trigger", "monitor") ?? "monitor";
        return string.Equals(configured, trigger, StringComparison.OrdinalIgnoreCase);
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

    private ShubbakConfig ApplyGaps(ShubbakConfig config, KdlNode? node)
    {
        if (node is null) return config;

        int inner = Int(node, "inner", config.InnerGap);

        Gaps outer = config.OuterGap;
        if (node.Child("outer") is { } outerNode)
        {
            // A single positional argument means "the same on all sides".
            if (outerNode.Argument(0) is { } uniform && uniform.TryAsInt(out int all))
            {
                outer = Gaps.All(all);
            }
            else
            {
                outer = new Gaps(
                    Math.Max(0, Int(outerNode, "left", outer.Left)),
                    Math.Max(0, Int(outerNode, "top", outer.Top)),
                    Math.Max(0, Int(outerNode, "right", outer.Right)),
                    Math.Max(0, Int(outerNode, "bottom", outer.Bottom)));
            }
        }

        return config with { InnerGap = Math.Max(0, inner), OuterGap = outer };
    }

    private ShubbakConfig ApplyEffects(ShubbakConfig config, KdlNode? node)
    {
        if (node is null) return config;

        return config with
        {
            Effects = new WindowEffects(
                Bool(node, "border", false),
                Text(node, "focused-colour", null) ?? Text(node, "focused-color", null),
                Text(node, "unfocused-colour", null) ?? Text(node, "unfocused-color", null),
                Text(node, "floating-colour", null) ?? Text(node, "floating-color", null),
                Text(node, "floating-unfocused-colour", null)
                    ?? Text(node, "floating-unfocused-color", null)),
        };
    }

    private ShubbakConfig ApplyAnimation(ShubbakConfig config, KdlNode? node)
    {
        if (node is null) return config;

        Core.Animation.AnimationOptions animation = config.Animation;

        animation = animation with
        {
            Enabled = Bool(node, "enabled", animation.Enabled),
            MinimumAnimatedDistance = Math.Max(
                0, Int(node, "minimum-distance", animation.MinimumAnimatedDistance)),
            WindowOpen = Profile(node, "window-open", animation.WindowOpen),
            WindowMove = Profile(node, "window-move", animation.WindowMove),
            LayoutChange = Profile(node, "layout-change", animation.LayoutChange),
            WorkspaceSwitch = Profile(node, "workspace-switch", animation.WorkspaceSwitch),
        };

        return config with { Animation = animation };
    }

    /// <summary>
    /// Reads one animation profile, e.g. <c>window-move duration=140 curve="ease-out"</c>.
    /// </summary>
    private Core.Animation.AnimationProfile Profile(
        KdlNode parent, string name, Core.Animation.AnimationProfile fallback)
    {
        KdlNode? node = parent.Child(name);
        if (node is null) return fallback;

        TimeSpan duration = node.Property("duration") is { } d && d.TryAsInt(out int ms)
            ? TimeSpan.FromMilliseconds(Math.Max(0, ms))
            : fallback.Duration;

        Core.Animation.Easing curve = fallback.Curve;

        if (node.Property("curve") is { } c)
        {
            string curveName = c.AsString();

            if (!Core.Animation.Easing.TryParse(curveName, out curve))
            {
                Report(Diagnostic.Warning(
                    "SHB0421",
                    $"Unknown easing curve '{curveName}'; using ease-out.",
                    c.Span,
                    "Available: linear, ease-in, ease-out, ease-in-out, ease-out-back, ease-out-expo."));
            }
        }

        return new Core.Animation.AnimationProfile(duration, curve);
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

    private List<WorkspaceConfig> ParseWorkspaces(KdlNode? node)
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

            workspaces.Add(new WorkspaceConfig(
                name,
                child.Property("display-name")?.AsString(),
                child.Property("monitor") is { } m && m.TryAsInt(out int index) ? index : null,
                child.Property("layout")?.AsString()));
        }

        return workspaces;
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
        KdlNode container, IReadOnlyList<WorkspaceConfig> workspaces, List<Keybinding> into)
    {
        foreach (KdlNode child in container.Children)
        {
            switch (child.Name)
            {
                case "bind":
                    if (ParseBinding(child, substitutions: null) is { } binding) into.Add(binding);
                    break;

                case "for-each":
                    ExpandForEach(child, workspaces, into);
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
    ///   bind "alt+shift+{name}" { move --workspace {name}; focus --workspace {name} }
    /// }
    /// </code>
    /// </remarks>
    private void ExpandForEach(
        KdlNode node, IReadOnlyList<WorkspaceConfig> workspaces, List<Keybinding> into)
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
                if (ParseBinding(child, substitutions) is { } binding) into.Add(binding);
        }
    }

    private Keybinding? ParseBinding(KdlNode node, IReadOnlyDictionary<string, string>? substitutions)
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

        if (commands.Count == 0)
        {
            Report(Diagnostic.Warning(
                "SHB0408",
                $"Binding '{keyText}' runs no commands, so pressing it will do nothing.",
                node.Span));
            return null;
        }

        return new Keybinding(key, commands, node.Span);
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
                    "SHB0419",
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

    private List<WindowRule> ParseRules(KdlNode? node, Dictionary<string, AppDefinition> apps)
    {
        List<WindowRule> rules = [];
        if (node is null) return rules;

        int ordinal = 0;

        foreach (KdlNode child in node.ChildrenNamed("rule"))
        {
            ordinal++;
            string name = child.Argument(0)?.AsString() ?? $"rule #{ordinal}";

            RuleTrigger trigger = (Text(child, "on", "manage") ?? "manage").ToLowerInvariant() switch
            {
                "manage" => RuleTrigger.OnManage,
                "title-change" => RuleTrigger.OnTitleChange,
                "focus" => RuleTrigger.OnFocus,
                _ => RuleTrigger.OnManage,
            };

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
                continue;
            }

            if (commands.Count == 0)
            {
                Report(Diagnostic.Warning(
                    "SHB0418", $"Rule '{name}' runs no commands.", child.Span));
                continue;
            }

            rules.Add(new WindowRule(name, trigger, matchers, appReferences, commands, child.Span));
        }

        return rules;
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

    private static string? Text(KdlNode parent, string name, string? fallback) =>
        SettingValue(parent, name) is { } value ? value.AsString() : fallback;

    private static TextSpan SpanOf(KdlNode parent, string name) =>
        Setting(parent, name)?.Span ?? parent.Span;
}
