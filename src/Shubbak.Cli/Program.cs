using Shubbak.Config;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.Cli;

/// <summary>
/// The <c>shubbak</c> command line client.
/// </summary>
/// <remarks>
/// Every command routes through the same IPC surface, which in turn routes through
/// the same CommandExecutor that keybindings use. A key press and a CLI invocation
/// therefore cannot diverge in behaviour.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0] switch
            {
                "inspect" => await InspectAsync(args).ConfigureAwait(false),
                "query" => await QueryAsync(args).ConfigureAwait(false),
                "sub" or "subscribe" => await SubscribeAsync(args).ConfigureAwait(false),
                "check-config" => CheckConfig(args),
                "config-path" => ShowConfigPath(args),
                "layouts" => await LayoutsAsync().ConfigureAwait(false),
                "status" => await StatusAsync().ConfigureAwait(false),
                "diagnose" => await DiagnoseAsync(args).ConfigureAwait(false),
                "log-level" => await LogLevelAsync(args).ConfigureAwait(false),
                _ => await CommandAsync(args).ConfigureAwait(false),
            };
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("shubbak: no window manager is running.");
            Console.Error.WriteLine("hint: start it with shubbak-wm");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"shubbak: {ex.Message}");
            return 1;
        }
    }

    private static async Task<IpcClient> ConnectAsync()
    {
        if (!IpcClient.IsServerRunning()) throw new TimeoutException();

        var client = new IpcClient();
        await client.ConnectAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        return client;
    }

    /// <summary>Sends the remaining arguments as a command string.</summary>
    private static async Task<int> CommandAsync(string[] args)
    {
        string command = string.Join(' ', args);

        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
        IpcResponse response = await client.SendAsync("command", command).ConfigureAwait(false);

        if (response.Ok) return 0;

        Console.Error.WriteLine($"shubbak: {response.Error}");
        return 1;
    }

    private static async Task<int> QueryAsync(string[] args)
    {
        string what = args.Length > 1 ? args[1] : "state";

        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
        IpcResponse response = await client.SendAsync("query", what).ConfigureAwait(false);

        if (!response.Ok)
        {
            Console.Error.WriteLine($"shubbak: {response.Error}");
            return 1;
        }

        Console.WriteLine(response.Data);
        return 0;
    }

    /// <summary>Lists the available layouts, one per line.</summary>
    /// <remarks>
    /// A convenience over <c>query layouts</c>: the query returns JSON for scripts,
    /// whereas this is for a human at a prompt deciding what to type next.
    /// </remarks>
    private static async Task<int> LayoutsAsync()
    {
        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
        IpcResponse response = await client.SendAsync("query", "layouts").ConfigureAwait(false);

        if (!response.Ok || response.Data is null)
        {
            Console.Error.WriteLine($"shubbak: {response.Error}");
            return 1;
        }

        IReadOnlyList<string>? layouts = System.Text.Json.JsonSerializer.Deserialize(
            response.Data, IpcJsonContext.Default.IReadOnlyListString);

        foreach (string layout in layouts ?? []) Console.WriteLine(layout);

        return 0;
    }

    private static async Task<int> StatusAsync()
    {
        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
        IpcResponse response = await client.SendAsync("ping").ConfigureAwait(false);

        Console.WriteLine(response.Ok ? "running" : "not responding");
        return response.Ok ? 0 : 1;
    }

    /// <summary>Tails the event stream.</summary>
    private static async Task<int> SubscribeAsync(string[] args)
    {
        string? topics = args.Length > 1 ? string.Join(',', args[1..]) : null;

        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            await foreach (IpcEvent notification in
                client.SubscribeAsync(topics, cancellation.Token).ConfigureAwait(false))
            {
                Console.WriteLine($"{notification.Topic}\t{notification.Data}");
            }
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    /// <summary>
    /// Writes a self-contained diagnostic report.
    /// </summary>
    /// <remarks>
    /// The command to run when something is wrong. One file, attachable to a bug
    /// report as-is, containing the environment, the config, the live window tree
    /// and the recent log - the last of which is captured whether or not file
    /// logging was ever switched on, which is what makes it useful for problems
    /// nobody expected.
    /// </remarks>
    private static async Task<int> DiagnoseAsync(string[] args)
    {
        string? output = null;

        for (int i = 1; i < args.Length - 1; i++)
            if (args[i] is "--output" or "-o") output = args[i + 1];

        string reason = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "manual";

        if (!IpcClient.IsServerRunning())
        {
            Console.Error.WriteLine("shubbak: no window manager is running.");
            Console.Error.WriteLine("hint: a report needs the running window manager to describe its state.");
            return 2;
        }

        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
        IpcResponse response = await client.SendAsync("diagnose", reason).ConfigureAwait(false);

        if (!response.Ok)
        {
            Console.Error.WriteLine($"shubbak: {response.Error}");
            return 1;
        }

        string report = response.Data ?? string.Empty;

        if (output is null)
        {
            Console.WriteLine(report);
            return 0;
        }

        string full = Path.GetFullPath(output);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(full, report).ConfigureAwait(false);

        Console.WriteLine($"Report written to {full}");
        Console.WriteLine($"({report.Length:N0} characters - attach this file to the bug report.)");

        return 0;
    }

    /// <summary>Reads or changes the log level of a running window manager.</summary>
    private static async Task<int> LogLevelAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: shubbak log-level <trace|debug|info|warn|error|none>");
            return 1;
        }

        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
        IpcResponse response = await client.SendAsync("log-level", args[1]).ConfigureAwait(false);

        if (!response.Ok)
        {
            Console.Error.WriteLine($"shubbak: {response.Error}");
            return 1;
        }

        Console.WriteLine($"log level is now {response.Data}");
        return 0;
    }

    /// <summary>
    /// Describes a window and explains how Shubbak sees it.
    /// </summary>
    /// <remarks>
    /// The single most useful diagnostic in the whole project. It answers "why is
    /// this window not being tiled?" - which neither GlazeWM nor komorebi can - by
    /// printing every matchable attribute, the manageability verdict with its
    /// reason, and which rules and app definitions matched.
    /// </remarks>
    private static async Task<int> InspectAsync(string[] args)
    {
        nint handle;

        if (args.Length > 1 && long.TryParse(args[1], out long explicitHandle))
        {
            handle = (nint)explicitHandle;
        }
        else
        {
            Console.WriteLine("Click the window to inspect, or press Escape to cancel.");
            Console.WriteLine("(Waiting 3 seconds, then inspecting the foreground window.)");

            // A proper crosshair picker needs a capture window and a mouse hook.
            // Sampling the foreground window after a pause gets the same answer for
            // the overwhelmingly common case - "the thing I just clicked on" -
            // without any of that machinery.
            await Task.Delay(3000).ConfigureAwait(false);

            handle = Win32Window.GetForeground();

            if (handle == 0)
            {
                Console.Error.WriteLine("shubbak: could not determine the foreground window.");
                return 1;
            }
        }

        // Local detail first, so inspect still works with no window manager running.
        PrintLocalReport(handle);

        if (!IpcClient.IsServerRunning())
        {
            Console.WriteLine();
            Console.WriteLine("(no window manager running - rule matching not shown)");
            return 0;
        }

        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);

        IpcResponse response = await client
            .SendAsync("inspect", ((long)handle).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            Console.Error.WriteLine($"shubbak: {response.Error}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine(response.Data);
        return 0;
    }

    private static void PrintLocalReport(nint handle)
    {
        ManageDecision decision = WindowFilter.Evaluate(handle);

        Console.WriteLine();
        Console.WriteLine($"handle       0x{handle:X}");
        Console.WriteLine($"title        {Win32Window.GetTitle(handle)}");
        Console.WriteLine($"class        {Win32Window.GetClassName(handle)}");

        uint processId = Win32Window.GetProcessId(handle);
        string? path = Win32Window.GetProcessPath(processId);

        Console.WriteLine($"process      {(path is null ? "(unreadable)" : Path.GetFileNameWithoutExtension(path))}");
        Console.WriteLine($"path         {path ?? "(unreadable - elevated process?)"}");
        Console.WriteLine($"rect         {Win32Window.GetBounds(handle)}");
        Console.WriteLine($"manageable   {(decision.Manageable ? "yes" : "no")} - {decision.Explain()}");
    }

    private static int CheckConfig(string[] args)
    {
        ConfigLocation location = ConfigPathResolver.Resolve(args.Length > 1 ? args[1] : null);

        if (!location.Found)
        {
            Console.Error.Write(location.DescribeSearch());
            return 1;
        }

        string path = location.Path!;

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"shubbak: config file not found: {path}");
            return 1;
        }

        ConfigLoadResult result = ConfigLoader.LoadFile(path);
        string source = File.ReadAllText(path);

        foreach (Diagnostic diagnostic in result.Diagnostics)
            Console.Error.Write(diagnostic.Render(source, path));

        int errors = result.Errors.Count();
        int warnings = result.Warnings.Count();

        Console.WriteLine(
            errors == 0 && warnings == 0
                ? $"{path}: ok - {result.Config.Keybindings.Count} keybindings, " +
                  $"{result.Config.Workspaces.Count} workspaces, {result.Config.Rules.Count} rules " +
                  $"(found via {location.Origin})"
                : $"{path}: {errors} error(s), {warnings} warning(s)");

        return errors == 0 ? 0 : 1;
    }

    /// <summary>Reports where the config was found, and everywhere that was tried.</summary>
    private static int ShowConfigPath(string[] args)
    {
        ConfigLocation location = ConfigPathResolver.Resolve(args.Length > 1 ? args[1] : null);

        if (!location.Found)
        {
            Console.Error.Write(location.DescribeSearch());
            return 1;
        }

        Console.WriteLine(location.Path);
        Console.Error.WriteLine($"(found via {location.Origin})");

        return 0;
    }

    private static void PrintUsage() => Console.WriteLine("""
        shubbak - control the Shubbak window manager

        USAGE
          shubbak <command> [args]

        WINDOW MANAGER COMMANDS
          Anything not listed below is sent straight to the window manager, using
          exactly the same syntax as a keybinding:

            shubbak focus --direction left
            shubbak focus --workspace 3
            shubbak move --workspace 3
            shubbak resize --width +5%
            shubbak layout --set fibonacci
            shubbak layout --cycle
            shubbak toggle-floating
            shubbak wm-reload-config

        DIAGNOSTICS
          diagnose [reason]    Write a self-contained report: environment, config,
                               the live window tree, and the recent log. This is the
                               one command to run when something is wrong.
                    -o <path>  Write to a file instead of stdout.

          log-level <level>    Change the log level of the running window manager
                               without restarting it.
                               trace | debug | info | warn | error | none

          inspect [handle]     Describe a window and explain how Shubbak sees it:
                               every matchable attribute, whether it is manageable
                               and why not, and which rules matched.
                               With no handle, waits 3 seconds then inspects the
                               foreground window.

          check-config [path]  Validate a config file, with carets under any
                               problems. Exits non-zero only for errors.

          config-path          Print which config file is in effect, or list
                               everywhere that was searched if none was found.

          status               Report whether a window manager is running.

        QUERIES
          query [what]         Print state as JSON.
                               what: state (default), windows, workspaces,
                                     monitors, focused, layouts
          layouts              List the available layouts.

        EVENTS
          sub [topics]         Tail the event stream. Comma-separated topics, or
                               omit for everything.

            shubbak sub window.focused,window.title_changed

        REPORTING A PROBLEM
          1. shubbak log-level trace
          2. reproduce the problem
          3. shubbak diagnose -o report.md

          Recent log entries are kept in memory even at the default level, so step 3
          alone is often enough for something that has already happened.
        """);
}
