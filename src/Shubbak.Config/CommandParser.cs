using System.Globalization;
using Shubbak.Core.Commands;
using Shubbak.Core.Geometry;

namespace Shubbak.Config;

/// <summary>
/// Parses command strings such as <c>focus --direction left</c>.
/// </summary>
/// <remarks>
/// <para>
/// The syntax intentionally matches GlazeWM's, so an existing config's command
/// strings can be pasted over unchanged. What differs is that unknown commands and
/// bad arguments are <b>reported with a span</b> at load time rather than failing
/// silently when the key is eventually pressed.
/// </para>
/// <para>
/// Parsing produces <see cref="WmCommand"/> values, which are the same type the CLI
/// and IPC produce, so all three paths converge before execution and cannot drift
/// apart in behaviour.
/// </para>
/// </remarks>
public static class CommandParser
{
    /// <summary>Parses one command string.</summary>
    public static bool TryParse(
        string text, TextSpan span, out WmCommand? command, out Diagnostic? diagnostic)
    {
        string[] tokens = Tokenise(text);
        return TryParseTokens(tokens, text, span, out command, out diagnostic);
    }

    /// <summary>
    /// Parses a command from already-separated tokens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Preferred over <see cref="TryParse"/> wherever the tokens are already known,
    /// because rebuilding a command line and re-splitting it silently destroys
    /// arguments that contain quote characters. The author's config has a workspace
    /// literally named <c>'</c>; round-tripping <c>focus --workspace '</c> through a
    /// tokeniser turns it into an empty name, and the binding then fails at load
    /// time for reasons that look like nonsense.
    /// </para>
    /// </remarks>
    /// <param name="tokens">Verb followed by arguments.</param>
    /// <param name="display">The command as written, for diagnostics.</param>
    /// <param name="span">Where it came from.</param>
    /// <param name="command">The parsed command.</param>
    /// <param name="diagnostic">Why parsing failed.</param>
    public static bool TryParseTokens(
        IReadOnlyList<string> tokens, string display, TextSpan span,
        out WmCommand? command, out Diagnostic? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        command = null;
        diagnostic = null;

        if (tokens.Count == 0)
        {
            diagnostic = Diagnostic.Error("SHB0301", "Empty command.", span);
            return false;
        }

        string text = display;
        string verb = tokens[0].ToLowerInvariant();
        ReadOnlySpan<string> rest = tokens.Skip(1).ToArray();

        switch (verb)
        {
            case "focus":
                return ParseFocus(rest, text, span, out command, out diagnostic);

            case "move":
                return ParseMove(rest, text, span, out command, out diagnostic);

            case "move-workspace":
            {
                // Two ways to say where: a direction, or a monitor by name. Both at
                // once is a contradiction rather than a preference, and is refused so
                // the binding does not quietly obey whichever one this happens to
                // read first.
                string? monitor = Value(rest, "--monitor");
                bool hasDirection = Value(rest, "--direction") is not null;

                if (monitor is not null && hasDirection)
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0315",
                        $"'{text}' gives both a direction and a monitor.",
                        span,
                        "Write move-workspace --direction left, or move-workspace --monitor \"name\", not both.");
                    return false;
                }

                if (monitor is not null)
                {
                    if (monitor.Length == 0)
                    {
                        diagnostic = Diagnostic.Error(
                            "SHB0316", $"'{text}' does not say which monitor.", span,
                            "Write move-workspace --monitor \"dell-left\" for a declared monitor, " +
                            "--monitor 1 for the second display, or --monitor DISPLAY2.");
                        return false;
                    }

                    command = new MoveWorkspaceToMonitorCommand(Monitor: monitor);
                    return true;
                }

                if (!TryDirection(rest, "--direction", out Direction direction))
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0310",
                        $"'{text}' does not name a direction or a monitor.",
                        span,
                        "Write move-workspace --direction left (or right, up, down), " +
                        "or move-workspace --monitor \"name\".");
                    return false;
                }

                command = new MoveWorkspaceToMonitorCommand(direction);
                return true;
            }

            case "resize":
                return ParseResize(rest, text, span, out command, out diagnostic);

            case "tag":
            {
                if (Flag(rest, "--clear")) { command = new ClearTagsCommand(); return true; }

                string? workspace =
                    Value(rest, "--add") ?? Value(rest, "--remove") ??
                    Value(rest, "--toggle") ?? Positional(rest);

                if (workspace is null)
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0311", $"'{text}' does not say which workspace to tag to.", span,
                        "Write tag --toggle 3, or tag --clear to remove every tag.");
                    return false;
                }

                Core.Wm.TagMode mode =
                    Value(rest, "--add") is not null ? Core.Wm.TagMode.Add :
                    Value(rest, "--remove") is not null ? Core.Wm.TagMode.Remove :
                    Core.Wm.TagMode.Toggle;

                command = new TagCommand(workspace, mode);
                return true;
            }

            case "sticky":
                command = new ToggleStickyCommand();
                return true;

            case "scratchpad":
            {
                // --name is the only flag there is. Anything else was a guess at one
                // that does not exist - most likely --show, --hide or --toggle, none
                // of which are real, because the command is already a toggle.
                //
                // These used to be accepted in silence: the unknown flag was skipped,
                // no positional remained, and the slot quietly became "default". So a
                // key bound to `scratchpad --hide notes` stashed into the wrong slot
                // and summoned from it, and nothing anywhere said why the named slot
                // appeared to be empty.
                if (UnknownFlag(rest, "--name") is { } unknown)
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0312", $"'{text}' has an option scratchpad does not take: {unknown}.", span,
                        "The only option is --name. scratchpad is already a toggle, so it needs " +
                        "no --show, --hide or --toggle: write scratchpad --name notes.");
                    return false;
                }

                // A --name with nothing after it. Value() needs a following token and
                // returns null without one, which fell through to "default" - so a
                // typo silently used a slot the user never named.
                if (Flag(rest, "--name") && Value(rest, "--name") is null)
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0313", $"'{text}' does not say which slot to use.", span,
                        "Write scratchpad --name notes, or scratchpad on its own for the default slot.");
                    return false;
                }

                // Named slots default to "default" so the common single-scratchpad
                // case needs no argument.
                string slot = Value(rest, "--name") ?? Positional(rest) ?? "default";
                command = new ScratchpadCommand(slot);
                return true;
            }

            case "toggle-tiling-direction":
                command = new ToggleTilingDirectionCommand();
                return true;

            case "toggle-floating":
                command = new ToggleFloatingCommand();
                return true;

            case "toggle-tiling":
                // GlazeWM spells "return to tiling" as its own command; it is the
                // same toggle from the other side.
                command = new ToggleFloatingCommand();
                return true;

            case "float":
                command = new FloatCommand();
                return true;

            case "tile":
                command = new TileCommand();
                return true;

            case "toggle-fullscreen":
                // --monitor covers the bar and the taskbar; without it a fullscreen
                // window stops at the work area, which is what most of the time is
                // wanted and so stays the default.
                command = new ToggleFullscreenCommand(
                    Flag(rest, "--monitor") || Flag(rest, "--whole-monitor"));
                return true;

            case "toggle-minimized" or "toggle-minimised":
                command = new ToggleMinimisedCommand();
                return true;

            case "split":
            {
                string layout =
                    Flag(rest, "--vertical") ? "splitv" :
                    Flag(rest, "--horizontal") ? "splith" :
                    Value(rest, "--layout") ?? "splitv";

                command = new SplitCommand(layout);
                return true;
            }

            case "layout":
            {
                if (Flag(rest, "--cycle")) { command = new CycleLayoutCommand(true); return true; }
                if (Flag(rest, "--cycle-back")) { command = new CycleLayoutCommand(false); return true; }

                string? layout = Value(rest, "--set") ?? Positional(rest);
                if (layout is null)
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0302", $"'{text}' does not say which layout to use.", span,
                        $"Write layout --set <name>, or layout --cycle. Available: {string.Join(", ", Core.Layouts.LayoutRegistry.CanonicalNames)}.");
                    return false;
                }

                command = new SetLayoutCommand(layout);
                return true;
            }

            case "equalise" or "equalize":
                command = new EqualiseCommand();
                return true;

            case "close":
                command = new CloseWindowCommand();
                return true;

            case "ignore":
                command = new IgnoreCommand();
                return true;

            case "manage":
                command = new ManageCommand();
                return true;

            case "toggle-managed":
                command = new ToggleManagedCommand();
                return true;

            case "wm-enable-binding-mode":
            {
                string? mode = Value(rest, "--name") ?? Positional(rest);
                if (mode is null)
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0303", $"'{text}' does not name a binding mode.", span,
                        "Write wm-enable-binding-mode --name resize.");
                    return false;
                }

                command = new EnableBindingModeCommand(mode);
                return true;
            }

            case "wm-disable-binding-mode":
                command = new DisableBindingModeCommand();
                return true;

            case "arrangement":
                return ParseArrangement(rest, text, span, out command, out diagnostic);

            case "context":
                return ParseContext(rest, text, span, out command, out diagnostic);

            case "wm-toggle-pause":
                command = new TogglePauseCommand();
                return true;

            case "wm-suspend":
                command = new SuspendCommand();
                return true;

            case "wm-resume":
                command = new ResumeCommand();
                return true;

            case "wm-toggle-suspend":
                command = new ToggleSuspendCommand();
                return true;

            case "wm-reload-config":
                command = new ReloadConfigCommand();
                return true;

            case "wm-redraw":
                command = new RedrawCommand();
                return true;

            case "wm-exit":
                command = new ExitCommand();
                return true;

            case "exit-all":
                command = new ExitAllCommand();
                return true;

            case "focus-recent-window":
                command = new FocusRecentWindowCommand();
                return true;

            case "focus-window":
            {
                if (rest.Length != 1)
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0305", "focus-window takes one window handle.", span)
                        with
                    { Hint = "For example: focus-window 0x1D0076" };
                    return false;
                }

                if (!TryHandle(rest[0], out long handle))
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0306", $"'{rest[0]}' is not a window handle.", span)
                        with
                    { Hint = "Handles are decimal, or hexadecimal with an 0x prefix." };
                    return false;
                }

                command = new FocusWindowCommand(handle);
                return true;
            }

            case "signal":
            {
                if (rest.Length == 0)
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0307", "signal has no name to announce.", span)
                        with
                    { Hint = "For example: signal \"palette\"" };
                    return false;
                }

                command = new SignalCommand(rest[0], [.. rest[1..]]);
                return true;
            }

            case "shell-exec":
            {
                // Everything after the verb is the command line, rejoined verbatim
                // so quoting stays the shell's problem rather than ours.
                string commandLine = string.Join(' ', tokens.Skip(1));

                if (commandLine.Length == 0)
                {
                    diagnostic = Diagnostic.Error(
                        "SHB0304", "shell-exec has nothing to run.", span);
                    return false;
                }

                command = new ShellExecCommand(commandLine);
                return true;
            }

            default:
                diagnostic = Diagnostic.Error(
                    "SHB0305",
                    $"Unknown command '{verb}'.",
                    span,
                    Suggest(verb));
                return false;
        }
    }

    private static bool ParseFocus(
        ReadOnlySpan<string> rest, string text, TextSpan span,
        out WmCommand? command, out Diagnostic? diagnostic)
    {
        command = null;
        diagnostic = null;

        if (TryDirection(rest, "--direction", out Direction direction))
        {
            command = new FocusDirectionCommand(direction);
            return true;
        }

        if (Value(rest, "--workspace") is { } workspace)
        {
            command = new FocusWorkspaceCommand(workspace);
            return true;
        }

        if (Flag(rest, "--recent-workspace"))
        {
            command = new FocusRecentWorkspaceCommand();
            return true;
        }

        if (Flag(rest, "--next")) { command = new CycleFocusCommand(true); return true; }
        if (Flag(rest, "--prev") || Flag(rest, "--previous")) { command = new CycleFocusCommand(false); return true; }

        diagnostic = Diagnostic.Error(
            "SHB0306",
            $"'{text}' does not say what to focus.",
            span,
            "Use focus --direction left, focus --workspace 3, focus --recent-workspace, or focus --next.");

        return false;
    }

    private static bool ParseMove(
        ReadOnlySpan<string> rest, string text, TextSpan span,
        out WmCommand? command, out Diagnostic? diagnostic)
    {
        command = null;
        diagnostic = null;

        if (TryDirection(rest, "--direction", out Direction direction))
        {
            // Said no, rather than accepted and ignored. A directional move already
            // carries focus with the window when it crosses to another monitor, and
            // within a workspace focus never leaves it, so there is nothing --focus
            // could add here - and a flag that is read, validated and does nothing is
            // worse than one that is refused.
            if (Flag(rest, "--focus"))
            {
                diagnostic = Diagnostic.Error(
                    "SHB0314",
                    $"'{text}' cannot take --focus.",
                    span,
                    "A directional move already takes focus with the window. --focus is for move --workspace.");

                return false;
            }

            command = new MoveDirectionCommand(direction);
            return true;
        }

        if (Value(rest, "--workspace") is { } workspace)
        {
            command = new MoveToWorkspaceCommand(workspace, Flag(rest, "--focus"));
            return true;
        }

        diagnostic = Diagnostic.Error(
            "SHB0307",
            $"'{text}' does not say where to move.",
            span,
            "Use move --direction right or move --workspace 3.");

        return false;
    }

    private static bool ParseResize(
        ReadOnlySpan<string> rest, string text, TextSpan span,
        out WmCommand? command, out Diagnostic? diagnostic)
    {
        command = null;
        diagnostic = null;

        Axis axis;
        string? amount;

        if (Value(rest, "--width") is { } width) { axis = Axis.Horizontal; amount = width; }
        else if (Value(rest, "--height") is { } height) { axis = Axis.Vertical; amount = height; }
        else
        {
            diagnostic = Diagnostic.Error(
                "SHB0308", $"'{text}' does not say which dimension to resize.", span,
                "Use resize --width +2% or resize --height -2%.");
            return false;
        }

        if (!TryParseAmount(amount, out double delta))
        {
            diagnostic = Diagnostic.Error(
                "SHB0309",
                $"'{amount}' is not a valid resize amount.",
                span,
                "Write a signed percentage such as +2% or -5%.");
            return false;
        }

        command = new ResizeCommand(axis, delta);
        return true;
    }

    /// <summary>
    /// <c>context --set name [--ttl 5s] [--lease]</c>, and the three other verbs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly one of <c>--set</c>, <c>--clear</c>, <c>--toggle</c> and <c>--auto</c>,
    /// each taking the context's name. Two at once is a contradiction rather than a
    /// preference and is refused, as <c>move-workspace</c> refuses a direction and a
    /// monitor together.
    /// </para>
    /// <para>
    /// <c>--ttl</c> and <c>--lease</c> only mean something on a set or a clear: a toggle
    /// is one of those two decided at the far end, so they are accepted there and apply
    /// to whichever it turns out to be; on <c>--auto</c> there is no pin for either to
    /// govern, and they are refused rather than ignored.
    /// </para>
    /// </remarks>
    private static bool ParseContext(
        ReadOnlySpan<string> rest, string text, TextSpan span,
        out WmCommand? command, out Diagnostic? diagnostic)
    {
        command = null;
        diagnostic = null;

        (ContextAction Action, string Flag)[] verbs =
        [
            (ContextAction.Set, "--set"),
            (ContextAction.Clear, "--clear"),
            (ContextAction.Toggle, "--toggle"),
            (ContextAction.Auto, "--auto"),
        ];

        ContextAction? action = null;
        string? name = null;
        int given = 0;

        foreach ((ContextAction candidate, string flag) in verbs)
        {
            if (!Flag(rest, flag)) continue;

            given++;
            action = candidate;
            name = Value(rest, flag);
        }

        if (given > 1)
        {
            diagnostic = Diagnostic.Error(
                "SHB0317", $"'{text}' asks for more than one of --set, --clear, --toggle and --auto.", span,
                "Write one of them: context --set presenting, or context --auto presenting.");
            return false;
        }

        if (action is null)
        {
            diagnostic = Diagnostic.Error(
                "SHB0317", $"'{text}' does not say what to do with the context.", span,
                "Write context --set presenting to hold it on, --clear to hold it off, " +
                "--toggle to flip it, or --auto to let its conditions decide again.");
            return false;
        }

        if (name is null || name.StartsWith("--", StringComparison.Ordinal))
        {
            diagnostic = Diagnostic.Error(
                "SHB0318", $"'{text}' does not name a context.", span,
                "Write context --set presenting, using a name declared in contexts { }.");
            return false;
        }

        TimeSpan? ttl = null;

        if (Value(rest, "--ttl") is { } ttlText)
        {
            if (!TryParseDuration(ttlText, out TimeSpan parsed) || parsed <= TimeSpan.Zero)
            {
                diagnostic = Diagnostic.Error(
                    "SHB0319", $"'{ttlText}' is not a duration.", span,
                    "Write --ttl 5s, --ttl 500ms, --ttl 2m or --ttl 1h; a bare number is seconds.");
                return false;
            }

            ttl = parsed;
        }
        else if (Flag(rest, "--ttl"))
        {
            diagnostic = Diagnostic.Error(
                "SHB0319", $"'{text}' gives --ttl without a duration.", span,
                "Write --ttl 5s, --ttl 500ms, --ttl 2m or --ttl 1h.");
            return false;
        }

        bool lease = Flag(rest, "--lease");

        if (action == ContextAction.Auto && (ttl is not null || lease))
        {
            diagnostic = Diagnostic.Error(
                "SHB0320", $"'{text}' gives --ttl or --lease with --auto, which has no pin for them to govern.", span,
                "Drop them: context --auto presenting takes the pin off outright.");
            return false;
        }

        command = new ContextCommand(name, action.Value, ttl, lease);
        return true;
    }

    /// <summary>
    /// <c>arrangement --save|--restore|--delete &lt;name&gt;</c>. The name may follow the
    /// flag or stand on its own.
    /// </summary>
    private static bool ParseArrangement(
        ReadOnlySpan<string> rest, string text, TextSpan span,
        out WmCommand? command, out Diagnostic? diagnostic)
    {
        command = null;
        diagnostic = null;

        (ArrangementAction Action, string Flag)[] verbs =
        [
            (ArrangementAction.Save, "--save"),
            (ArrangementAction.Restore, "--restore"),
            (ArrangementAction.Delete, "--delete"),
        ];

        ArrangementAction? action = null;
        string? name = null;
        int given = 0;

        foreach ((ArrangementAction candidate, string flag) in verbs)
        {
            if (!Flag(rest, flag)) continue;

            given++;
            action = candidate;
            name = Value(rest, flag);
        }

        if (given > 1)
        {
            diagnostic = Diagnostic.Error(
                "SHB0321", $"'{text}' asks for more than one of --save, --restore and --delete.", span,
                "Write one of them: arrangement --save demo, or arrangement --restore demo.");
            return false;
        }

        if (action is null)
        {
            diagnostic = Diagnostic.Error(
                "SHB0321", $"'{text}' does not say what to do with the arrangement.", span,
                "Write arrangement --save demo to record the workspace's tree under that name, " +
                "--restore demo to put it back, or --delete demo to forget it.");
            return false;
        }

        name ??= Positional(rest);

        if (name is null || name.StartsWith("--", StringComparison.Ordinal))
        {
            diagnostic = Diagnostic.Error(
                "SHB0322", $"'{text}' does not name an arrangement.", span,
                "Write arrangement --save demo; the name is yours to choose.");
            return false;
        }

        command = new ArrangementCommand(name, action.Value);
        return true;
    }

    /// <summary>
    /// Reads <c>500ms</c>, <c>5s</c>, <c>2m</c>, <c>1h</c>, or a bare number of seconds.
    /// </summary>
    /// <remarks>
    /// Seconds for a bare number because that is the unit a person reaching for a
    /// time-to-live thinks in: a heartbeat every few seconds, a pin that outlives a
    /// crash by not much more. Milliseconds is the unit a linger is written in, and the
    /// two are in different places for exactly that reason.
    /// </remarks>
    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        double scale;

        if (span.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) { scale = 1; span = span[..^2]; }
        else if (span.EndsWith("s", StringComparison.OrdinalIgnoreCase)) { scale = 1000; span = span[..^1]; }
        else if (span.EndsWith("m", StringComparison.OrdinalIgnoreCase)) { scale = 60_000; span = span[..^1]; }
        else if (span.EndsWith("h", StringComparison.OrdinalIgnoreCase)) { scale = 3_600_000; span = span[..^1]; }
        else scale = 1000;

        if (!double.TryParse(span, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount))
            return false;

        if (amount < 0 || double.IsNaN(amount) || double.IsInfinity(amount)) return false;

        duration = TimeSpan.FromMilliseconds(amount * scale);
        return true;
    }

    /// <summary>Converts <c>+2%</c> into <c>0.02</c>.</summary>
    private static bool TryParseAmount(string text, out double delta)
    {
        delta = 0;
        if (text.Length == 0) return false;

        bool percent = text.EndsWith('%');
        string number = percent ? text[..^1] : text;

        if (!double.TryParse(number, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double value))
        {
            return false;
        }

        // Percentages are the only unit that makes sense for a ratio-based tree.
        // Pixel amounts would depend on the container's current size, so the same
        // binding would behave differently on different monitors.
        delta = percent ? value / 100.0 : value;
        return true;
    }

    private static bool TryDirection(ReadOnlySpan<string> tokens, string flag, out Direction direction)
    {
        direction = default;

        string? value = Value(tokens, flag);
        if (value is null) return false;

        switch (value.ToLowerInvariant())
        {
            case "left": direction = Direction.Left; return true;
            case "right": direction = Direction.Right; return true;
            case "up": direction = Direction.Up; return true;
            case "down": direction = Direction.Down; return true;
            default: return false;
        }
    }

    private static string? Value(ReadOnlySpan<string> tokens, string flag)
    {
        for (int i = 0; i < tokens.Length - 1; i++)
            if (string.Equals(tokens[i], flag, StringComparison.OrdinalIgnoreCase))
                return tokens[i + 1];

        return null;
    }

    /// <summary>
    /// The first token that looks like an option but is not one this command takes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A token counts as an option if it opens with two dashes. A single dash is left
    /// alone deliberately: <c>-</c> is a perfectly good workspace name, and one of the
    /// shipped examples uses it.
    /// </para>
    /// <para>
    /// The value of a recognised flag is skipped, so <c>--name --hide</c> reports
    /// nothing - the user asked for a slot literally called <c>--hide</c>, which is
    /// odd but is what they wrote, and inventing an error for it would be guessing.
    /// </para>
    /// </remarks>
    /// <param name="tokens">The tokens after the verb.</param>
    /// <param name="known">The options this command accepts, each taking one value.</param>
    private static string? UnknownFlag(ReadOnlySpan<string> tokens, params string[] known)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];

            if (!token.StartsWith("--", StringComparison.Ordinal)) continue;

            bool recognised = false;

            foreach (string flag in known)
            {
                if (!string.Equals(token, flag, StringComparison.OrdinalIgnoreCase)) continue;

                recognised = true;

                // Its value is an argument, not an option, whatever it looks like.
                i++;
                break;
            }

            if (!recognised) return token;
        }

        return null;
    }

    private static bool Flag(ReadOnlySpan<string> tokens, string flag)
    {
        foreach (string token in tokens)
            if (string.Equals(token, flag, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static string? Positional(ReadOnlySpan<string> tokens)
    {
        foreach (string token in tokens)
            if (!token.StartsWith("--", StringComparison.Ordinal)) return token;

        return null;
    }

    /// <summary>
    /// Spells a value so that the tokeniser reads it back as exactly one token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For anything that builds a command from a name it did not choose - the palette
    /// writing <c>focus --workspace</c> for the workspace a row stands for, the bar for
    /// the one that was clicked, the command line for whatever was typed. A name with
    /// a space in it, written bare, is two tokens; a name that is a quote character,
    /// written bare, opens a quotation that never closes and the command is refused
    /// with nothing on screen to say why.
    /// </para>
    /// <para>
    /// Left alone when nothing in it needs quoting, so a command built from a plain
    /// name reads as it always did. Double quotes unless the value has one, single
    /// quotes otherwise. A value with both cannot be written in this command language
    /// - the tokeniser has no escape - and <see cref="CanQuote"/> says so ahead of time;
    /// the loader refuses such a name (SHB0454) so it never reaches here.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The value contains both kinds of quote.</exception>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0) return "\"\"";

        bool needsQuoting = false;

        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || c is '"' or '\'')
            {
                needsQuoting = true;
                break;
            }
        }

        if (!needsQuoting) return value;
        if (!value.Contains('"')) return $"\"{value}\"";
        if (!value.Contains('\'')) return $"'{value}'";

        throw new ArgumentException(
            "The value contains both a double and a single quote, and the command language has no way to write that.",
            nameof(value));
    }

    /// <summary>Whether <see cref="Quote"/> can spell a value at all.</summary>
    public static bool CanQuote(string value) =>
        value is not null && !(value.Contains('"') && value.Contains('\''));

    /// <summary>
    /// Splits on whitespace, honouring quotes.
    /// </summary>
    /// <remarks>
    /// Quotes matter for workspace names: the author's config has workspaces called
    /// <c>-</c>, <c>\</c> and <c>'</c>, and without quoting support
    /// <c>focus --workspace "'"</c> would be unwritable. <see cref="Quote"/> is the
    /// inverse, for code that builds a command from a name.
    /// </remarks>
    private static string[] Tokenise(string text)
    {
        List<string> tokens = [];
        var current = new System.Text.StringBuilder();
        char quote = '\0';

        foreach (char c in text)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) tokens.Add(current.ToString());

        return [.. tokens];
    }

    /// <summary>
    /// Reads a window handle, decimal or hexadecimal.
    /// </summary>
    /// <remarks>
    /// Hexadecimal is accepted because that is how every tool that shows a window
    /// handle prints it - Spy++, <c>shubbak inspect</c>, and Shubbak's own log lines
    /// all say <c>0x1D0076</c>. Requiring the decimal form would mean the user
    /// converting a number by hand to paste it back into the program that printed it.
    /// </remarks>
    private static bool TryHandle(string value, out long handle)
    {
        ReadOnlySpan<char> text = value.AsSpan();

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(
                text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handle);
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out handle);
    }

    /// <summary>Suggests a correction for a misspelled command.</summary>
    /// <remarks>
    /// The verb list comes from <see cref="CommandCatalogue"/>, which is the single
    /// description of the command set. It used to be a second, hand-maintained array
    /// here, and it had fallen behind - which is invisible, because a missing entry
    /// only means one command never gets suggested.
    /// <para>
    /// This is also the only place on the parsing path that touches the catalogue,
    /// and it runs after a command has already failed to parse. A configuration with
    /// no mistakes in it never builds the table at all.
    /// </para>
    /// </remarks>
    private static string? Suggest(string verb)
    {
        // Only suggests when the guess is close enough to be plausible; a wild guess
        // is worse than no guess, because it sends the user down the wrong path.
        return Suggestion.Closest(verb, CommandCatalogue.Verbs) is { } best
            ? $"Did you mean '{best}'?"
            : null;
    }
}
