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
                if (!TryDirection(rest, "--direction", out Direction direction))
                {
                    diagnostic = DirectionMissing("move-workspace", text, span);
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

    private static Diagnostic DirectionMissing(string verb, string text, TextSpan span) =>
        Diagnostic.Error(
            "SHB0310",
            $"'{text}' does not name a direction.",
            span,
            $"Write {verb} --direction left (or right, up, down).");

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
    /// Splits on whitespace, honouring quotes.
    /// </summary>
    /// <remarks>
    /// Quotes matter for workspace names: the author's config has workspaces called
    /// <c>-</c>, <c>\</c> and <c>'</c>, and without quoting support
    /// <c>focus --workspace "'"</c> would be unwritable.
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
