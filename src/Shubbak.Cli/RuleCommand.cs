using System.Globalization;
using System.Text.Json;
using Dalil.Core;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.Cli;

/// <summary>
/// <c>shubbak rule</c>: the rules in force, and adding or removing one without opening
/// the file.
/// </summary>
/// <remarks>
/// <para>
/// The command-line half of what the palette does from a window's row. The window
/// manager does the editing over the pipe - it knows which file is in effect, it
/// validates as it would load, and it reloads - so this is a thin client: it composes
/// the rule, or reads one from a file, and prints what happened.
/// </para>
/// <para>
/// <c>add</c> composes from a window the way the palette does, with the same composer,
/// so a rule written from a terminal and one written from the palette are the same
/// rule. <c>--print</c> shows it without sending it, for the person who wants the text
/// and their own editor.
/// </para>
/// </remarks>
internal static class RuleCommand
{
    public static Task<int> RunAsync(string[] args, Func<Task<IpcClient>> connect)
    {
        string action = args.Length > 1 ? args[1] : "";

        return action switch
        {
            "list" or "ls" => ListAsync(connect),
            "add" => AddAsync(args, connect),
            "remove" or "rm" => RemoveAsync(args, connect),
            "" => Task.FromResult(Missing()),
            _ => Task.FromResult(Unknown(action)),
        };
    }

    private static int Missing()
    {
        Console.Error.WriteLine("shubbak: rule needs an action.");
        Console.Error.WriteLine("hint: shubbak rule list | add | remove");
        return 1;
    }

    private static int Unknown(string action)
    {
        Console.Error.WriteLine($"shubbak: unknown rule action '{action}'.");
        Console.Error.WriteLine("hint: list, add, remove");
        return 1;
    }

    /// <summary>Every rule in force, with where it lives and what it does.</summary>
    private static async Task<int> ListAsync(Func<Task<IpcClient>> connect)
    {
        await using IpcClient client = await connect().ConfigureAwait(false);

        IpcResponse response = await client.SendAsync("query", "rules").ConfigureAwait(false);

        if (!response.Ok)
        {
            Console.Error.WriteLine($"shubbak: {response.Error}");
            return 1;
        }

        IReadOnlyList<RuleInfo> rules = response.Data is { Length: > 0 } json
            ? JsonSerializer.Deserialize(json, IpcJsonContext.Default.IReadOnlyListRuleInfo) ?? []
            : [];

        Console.Write(Format(rules));
        return 0;
    }

    /// <summary>The rule list as text: one line per rule, columns aligned.</summary>
    internal static string Format(IReadOnlyList<RuleInfo> rules)
    {
        if (rules.Count == 0) return "(no rules configured)\n";

        int nameWidth = Math.Max(4, rules.Max(r => r.Name.Length));
        int lineWidth = Math.Max(4, rules.Max(r => r.Line.ToString(CultureInfo.InvariantCulture).Length));

        var text = new System.Text.StringBuilder();

        text.Append($"{"line".PadLeft(lineWidth)}  {"name".PadRight(nameWidth)}  does\n");

        foreach (RuleInfo rule in rules)
        {
            string does = string.Join(", ", rule.Does);
            string when = rule.Trigger is "manage" ? string.Empty : $"  (on {rule.Trigger})";
            string context = rule.Context is { Length: > 0 } ? $"  [context {rule.Context}]" : string.Empty;

            text.Append($"{rule.Line.ToString(CultureInfo.InvariantCulture).PadLeft(lineWidth)}  {rule.Name.PadRight(nameWidth)}  {does}{when}{context}\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Composes a rule for a window, or reads one from a file, and adds it.
    /// </summary>
    /// <remarks>
    /// The handle is optional the way it is for <c>inspect</c>: with none, the
    /// foreground window three seconds from now, which is "the thing I just clicked
    /// on" without a picker.
    /// </remarks>
    private static async Task<int> AddAsync(string[] args, Func<Task<IpcClient>> connect)
    {
        RuleAddArguments? parsed = RuleAddArguments.Parse(args.Skip(2).ToArray(), out string? problem);

        if (parsed is null)
        {
            Console.Error.WriteLine($"shubbak: {problem}");
            Console.Error.WriteLine("hint: shubbak rule add [handle] --ignore | --manage | --float | --tile | --workspace <name> [--print]");
            Console.Error.WriteLine("      shubbak rule add --file <rules.kdl>   (or --file - for stdin)");
            return 1;
        }

        string kdl;
        long? subject = null;

        if (parsed.File is { } file)
        {
            try
            {
                kdl = file == "-"
                    ? await Console.In.ReadToEndAsync().ConfigureAwait(false)
                    : await File.ReadAllTextAsync(file).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"shubbak: could not read {file}: {ex.Message}");
                return 1;
            }
        }
        else
        {
            nint handle = parsed.Handle;

            if (handle == 0)
            {
                Console.WriteLine("Click the window the rule is for, or press Escape to cancel.");
                Console.WriteLine("(Waiting 3 seconds, then using the foreground window.)");

                await Task.Delay(3000).ConfigureAwait(false);

                handle = Win32Window.GetForeground();

                if (handle == 0)
                {
                    Console.Error.WriteLine("shubbak: could not determine the foreground window.");
                    return 1;
                }
            }

            if (!Win32Window.Exists(handle))
            {
                Console.Error.WriteLine($"shubbak: 0x{handle:X} is not a window.");
                return 1;
            }

            // Read locally, as inspect does when nothing is running: the attributes are
            // the window's, not the window manager's. The verbs decide the name.
            uint processId = Win32Window.GetProcessId(handle);
            string? path = Win32Window.GetProcessPath(processId);

            kdl = RuleComposer.Rule(
                null,
                Win32Window.GetClassName(handle),
                path is null ? null : Path.GetFileNameWithoutExtension(path),
                Win32Window.GetTitle(handle),
                parsed.Does,
                path);

            subject = handle;
        }

        if (parsed.Print)
        {
            Console.WriteLine(kdl);
            return 0;
        }

        await using IpcClient client = await connect().ConfigureAwait(false);

        string payload = JsonSerializer.Serialize(
            new RuleAddition(kdl, subject, "the command line"), IpcJsonContext.Default.RuleAddition);

        IpcResponse response = await client.SendAsync("add-rule", payload).ConfigureAwait(false);

        return Report(response, added: true);
    }

    /// <summary>Removes a rule by name, and by line when the name is not enough.</summary>
    /// <remarks>
    /// The line is what the window manager insists on - a report is a snapshot and the
    /// file may have moved on - so when the caller gives a name alone, the list is
    /// asked for the line, and refused when the name is not unique in it.
    /// </remarks>
    private static async Task<int> RemoveAsync(string[] args, Func<Task<IpcClient>> connect)
    {
        string? name = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
        string? lineText = Value(args, "--line");

        if (name is null)
        {
            Console.Error.WriteLine("shubbak: rule remove needs the rule's name.");
            Console.Error.WriteLine("hint: shubbak rule remove \"ignore teams\" [--line 412]; shubbak rule list shows both.");
            return 1;
        }

        int line = 0;

        if (lineText is not null && (!int.TryParse(lineText, NumberStyles.Integer, CultureInfo.InvariantCulture, out line) || line <= 0))
        {
            Console.Error.WriteLine($"shubbak: '{lineText}' is not a line number.");
            return 1;
        }

        await using IpcClient client = await connect().ConfigureAwait(false);

        if (line == 0)
        {
            IpcResponse listed = await client.SendAsync("query", "rules").ConfigureAwait(false);

            IReadOnlyList<RuleInfo> rules = listed.Ok && listed.Data is { Length: > 0 } json
                ? JsonSerializer.Deserialize(json, IpcJsonContext.Default.IReadOnlyListRuleInfo) ?? []
                : [];

            List<RuleInfo> named = [.. rules.Where(r => string.Equals(r.Name, name, StringComparison.Ordinal))];

            if (named.Count == 0)
            {
                Console.Error.WriteLine($"shubbak: no rule is called '{name}'.");
                Console.Error.WriteLine("hint: shubbak rule list");
                return 1;
            }

            if (named.Count > 1)
            {
                Console.Error.WriteLine($"shubbak: {named.Count} rules are called '{name}'; say which with --line.");
                Console.Error.Write(Format(named));
                return 1;
            }

            line = named[0].Line;
        }

        string payload = JsonSerializer.Serialize(
            new RuleRemoval(name, line, null), IpcJsonContext.Default.RuleRemoval);

        IpcResponse response = await client.SendAsync("remove-rule", payload).ConfigureAwait(false);

        return Report(response, added: false);
    }

    /// <summary>Prints what the window manager did, or why it would not.</summary>
    private static int Report(IpcResponse response, bool added)
    {
        if (!response.Ok)
        {
            Console.Error.WriteLine($"shubbak: {response.Error}");
            return 1;
        }

        RuleChange? change = response.Data is { Length: > 0 } json
            ? JsonSerializer.Deserialize(json, IpcJsonContext.Default.RuleChange)
            : null;

        if (change is null)
        {
            Console.Error.WriteLine("shubbak: the window manager sent an answer that could not be read.");
            return 1;
        }

        Console.Write(Describe(change, added));
        return 0;
    }

    /// <summary>What happened, as the terminal says it.</summary>
    internal static string Describe(RuleChange change, bool added)
    {
        string names = string.Join(", ", change.Names.Select(n => $"\"{n}\""));

        var text = new System.Text.StringBuilder();

        text.Append(added
            ? $"Added {names} at line {change.Line} of {change.Path}"
            : $"Removed {names} from line {change.Line} of {change.Path}");

        text.Append(change.Reloaded
            ? " and reloaded.\n"
            : ".\nThe reload was refused, so the running configuration is unchanged; shubbak check-config says why.\n");

        if (change.Outcome is { Length: > 0 } outcome) text.Append(outcome).Append('\n');

        if (!added)
        {
            text.Append("\nTo put it back:\n\n");

            foreach (string line in change.RuleText.Split('\n'))
                text.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
        }

        return text.ToString();
    }

    private static string? Value(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>What <c>shubbak rule add</c> was asked for.</summary>
/// <param name="Handle">The window, or zero for the foreground window in a moment.</param>
/// <param name="Does">The commands the rule runs, one per line.</param>
/// <param name="File">A file of rules to add instead, or <c>-</c> for stdin.</param>
/// <param name="Print">Whether to print the rule rather than add it.</param>
internal sealed record RuleAddArguments(nint Handle, IReadOnlyList<string> Does, string? File, bool Print)
{
    /// <summary>Reads the arguments after <c>rule add</c>, or says what is wrong with them.</summary>
    /// <remarks>
    /// Pure, so the shapes a person can type are tested without a window or a pipe.
    /// </remarks>
    public static RuleAddArguments? Parse(string[] args, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);

        nint handle = 0;
        List<string> does = [];
        string? file = null;
        bool print = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "--ignore":
                    does.Add("ignore");
                    break;

                case "--manage":
                    does.Add("manage");
                    break;

                case "--float":
                    does.Add("float");
                    break;

                case "--tile":
                    does.Add("tile");
                    break;

                case "--workspace":
                case "--move":
                    if (i + 1 >= args.Length)
                    {
                        problem = $"{arg} needs a workspace name.";
                        return null;
                    }

                    does.Add($"move --workspace \"{RuleComposer.Escape(args[++i])}\"");
                    break;

                case "--file":
                    if (i + 1 >= args.Length)
                    {
                        problem = "--file needs a path, or - for stdin.";
                        return null;
                    }

                    file = args[++i];
                    break;

                case "--print":
                case "--dry-run":
                    print = true;
                    break;

                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        problem = $"unknown option '{arg}'.";
                        return null;
                    }

                    if (handle != 0)
                    {
                        problem = $"one window at a time; '{arg}' is a second handle.";
                        return null;
                    }

                    if (!TryParseHandle(arg, out handle))
                    {
                        problem = $"'{arg}' is not a window handle.";
                        return null;
                    }

                    break;
            }
        }

        if (file is not null && (does.Count > 0 || handle != 0))
        {
            problem = "--file adds rules from a file; it takes no window and no verbs.";
            return null;
        }

        if (file is null && does.Count == 0)
        {
            problem = "say what the rule should do: --ignore, --manage, --float, --tile or --workspace <name>.";
            return null;
        }

        if (does.Contains("ignore") && does.Contains("manage"))
        {
            problem = "--ignore and --manage contradict each other.";
            return null;
        }

        problem = null;
        return new RuleAddArguments(handle, does, file, print);
    }

    private static bool TryParseHandle(string text, out nint handle)
    {
        handle = 0;

        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        string digits = hex ? text[2..] : text;

        if (!long.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            return false;

        handle = (nint)value;
        return handle != 0;
    }
}
