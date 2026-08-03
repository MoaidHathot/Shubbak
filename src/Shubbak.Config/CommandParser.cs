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
                command = new ToggleFullscreenCommand();
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

            case "wm-reload-config":
                command = new ReloadConfigCommand();
                return true;

            case "wm-redraw":
                command = new RedrawCommand();
                return true;

            case "wm-exit":
                command = new ExitCommand();
                return true;

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
            command = new MoveDirectionCommand(direction);
            return true;
        }

        if (Value(rest, "--workspace") is { } workspace)
        {
            command = new MoveToWorkspaceCommand(workspace);
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

    /// <summary>Suggests a correction for a misspelled command.</summary>
    private static string? Suggest(string verb)
    {
        string[] known =
        [
            "focus", "move", "move-workspace", "resize", "split", "layout", "close",
            "tag", "sticky", "scratchpad",
            "toggle-tiling-direction", "toggle-floating", "toggle-fullscreen",
            "toggle-minimized", "toggle-managed", "float", "tile",
            "equalise", "ignore", "manage", "shell-exec",
            "wm-enable-binding-mode", "wm-disable-binding-mode", "wm-toggle-pause",
            "wm-reload-config", "wm-redraw", "wm-exit",
        ];

        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in known)
        {
            int distance = Levenshtein(verb, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        // Only suggest when the guess is close enough to be plausible; a wild guess
        // is worse than no guess, because it sends the user down the wrong path.
        return best is not null && bestDistance <= 3 ? $"Did you mean '{best}'?" : null;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        Span<int> previous = new int[b.Length + 1];
        Span<int> current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            current.CopyTo(previous);
        }

        return previous[b.Length];
    }
}
