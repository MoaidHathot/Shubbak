using System.Text.Json;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Wm;
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

        // Answered here, before anything can reach the daemon.
        //
        // It used to fall through to the switch below, be treated as a window manager
        // command and sent down the pipe - so asking a stopped Shubbak its version
        // was answered with "no window manager is running", which is both untrue and
        // unrelated. A version query is about this binary and must work whether or
        // not anything else does.
        if (Array.Exists(args, a => a is "--version" or "-v" or "version"))
        {
            Console.WriteLine(ShubbakVersion.Banner);
            return 0;
        }

        // Asking for help must never do anything else.
        //
        // Checked here rather than in each handler, because the handlers read their
        // flags by scanning for the ones they know: an argument none of them
        // recognises is silently the default. `restore --help` therefore matched
        // neither --dry-run nor --cloaked nor --all, fell through to the ordinary
        // path, and un-concealed every window the session could identify. Asking a
        // destructive command how to use it performed it.
        if (Array.Exists(args, a => a is "--help" or "-h"))
        {
            PrintUsage();
            return 0;
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
            "config" => ConfigCommand.Run(args),
            "rule" or "rules" => await RuleCommand.RunAsync(args, ConnectAsync).ConfigureAwait(false),
                "autostart" => Autostart.Run(args),
                "layouts" => await LayoutsAsync().ConfigureAwait(false),
                "monitors" => await MonitorsAsync().ConfigureAwait(false),
                "contexts" => await ContextsAsync().ConfigureAwait(false),
                "arrangements" => await ArrangementsAsync().ConfigureAwait(false),
                "status" => await StatusAsync().ConfigureAwait(false),
                "diagnose" => await DiagnoseAsync(args).ConfigureAwait(false),
                "restore" => Restore(args),
            "taj-exit" => CloseWindowsOfClass("TajBarWindow", "bar"),
            "dalil-exit" => CloseWindowsOfClass("DalilPaletteWindow", "palette"),
            "ayn-exit" => StopWatcher(),
            "stop" => await StopEverythingAsync().ConfigureAwait(false),
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

    /// <summary>
    /// Closes the bar, or the palette, without going through the window manager.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not over IPC. The case that most needs this is one left behind by a
    /// window manager that is already gone - and a command routed through the window
    /// manager cannot reach it.
    /// </para>
    /// <para>
    /// So it asks the windows themselves. <c>WM_CLOSE</c> is a request rather than a
    /// kill: Taj answers it by leaving its message loop, which unregisters the appbar
    /// and gives back the strip of screen it reserved. Terminating the process instead
    /// skips that, and the shell can be left holding space for a bar that no longer
    /// exists. Dalil answers it by shutting down, which lets go of its own single
    /// instance claim.
    /// </para>
    /// <para>
    /// Both matter more now that each refuses to start twice: without a way to stop the
    /// one that is running, a wedged bar or palette could only be cleared through Task
    /// Manager - which is a worse position than the double-start the guard prevents.
    /// </para>
    /// </remarks>
    /// <param name="windowClass">The class the program registers for its windows.</param>
    /// <param name="what">What to call it when there is none, or several.</param>
    private static int CloseWindowsOfClass(string windowClass, string what)
    {
        int closed = CloseWindowsOfClassQuietly(windowClass);

        if (closed == 0)
        {
            Console.Error.WriteLine($"shubbak: no {what} is running.");
            return 2;
        }

        Console.WriteLine($"closed {closed} {what} window(s)");
        return 0;
    }

    /// <summary>Asks every window of a class to close, and says how many were asked.</summary>
    private static int CloseWindowsOfClassQuietly(string windowClass)
    {
        int closed = 0;

        foreach (nint handle in Win32Window.EnumerateTopLevel())
        {
            if (!string.Equals(Win32Window.GetClassName(handle), windowClass, StringComparison.Ordinal))
                continue;

            WindowActions.Close(handle);
            closed++;
        }

        return closed;
    }

    /// <summary>
    /// Stops the watcher, which has no window to close.
    /// </summary>
    /// <remarks>
    /// It holds a named event open and waits on it; setting the event is the request.
    /// Then the mutex it holds is watched until it lets go, so that a script running
    /// <c>ayn-exit</c> and then starting a new one does not race the old one out
    /// of the door. Not over IPC, for the same reason the other two are not: this has
    /// to work when the window manager is gone.
    /// </remarks>
    private static int StopWatcher()
    {
        if (!SignalWatcherToStop())
        {
            Console.Error.WriteLine("shubbak: no watcher is running.");
            return 2;
        }

        if (WaitUntilReleased(IpcProtocol.InstanceMutexNameFor("ayn"), TimeSpan.FromSeconds(5)))
        {
            Console.WriteLine("stopped the watcher");
            return 0;
        }

        Console.Error.WriteLine("shubbak: asked the watcher to stop, but it is still running after 5 s.");
        return 1;
    }

    /// <summary>Sets the watcher's stop event. False when there is no watcher to set it for.</summary>
    private static bool SignalWatcherToStop()
    {
        if (!EventWaitHandle.TryOpenExisting(IpcProtocol.StopEventNameFor("ayn"), out EventWaitHandle? stop))
            return false;

        using (stop) stop.Set();
        return true;
    }

    /// <summary>
    /// Waits for a single-instance mutex to be let go of, which is the one reliable
    /// sign that the process holding it has actually exited.
    /// </summary>
    private static bool WaitUntilReleased(string mutex, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (true)
        {
            // Uncertain counts as gone: if the mutex cannot be examined, waiting on it
            // longer will not make it examinable.
            if (SingleInstanceLock.IsHeldByAnyone(mutex) is not true) return true;
            if (Environment.TickCount64 >= deadline) return false;

            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Stops everything: the window manager, the bar, the palette and the watcher.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four programs, three of which deliberately outlive the fourth - the palette and
    /// the watcher reconnect when the window manager comes back, and the bar waits a
    /// while for the same reason. That is right for a restart and wrong for "stop":
    /// somebody replacing the executables, or just wanting their desktop back, wants
    /// all of them gone and wants to be told when they are. Before this the answer
    /// was four commands and a look at Task Manager.
    /// </para>
    /// <para>
    /// <c>exit-all</c> - the tray's Exit - asks the same thing from inside, over the
    /// pipe. This is still the command for a script, and the only one of the two that
    /// works when the window manager is already gone: it does not need the pipe, and
    /// it waits until each program has actually left rather than having been asked.
    /// </para>
    /// <para>
    /// The window manager goes first and over the pipe, because <c>wm-exit</c> is what
    /// restores every concealed window and saves the session, and because the bar
    /// closes itself when it hears the window manager leave. The others are asked
    /// directly, the same way their own <c>-exit</c> commands ask, so this works with
    /// no window manager at all. Then each one's single-instance mutex is watched
    /// until it is released - the process being gone, rather than having been asked.
    /// </para>
    /// <para>
    /// The exit code says whether everything stopped. A script that stops Shubbak in
    /// order to replace it must be able to tell.
    /// </para>
    /// </remarks>
    private static async Task<int> StopEverythingAsync()
    {
        bool allStopped = true;
        var waitingFor = new List<(string Mutex, string What)>();

        if (IpcClient.IsServerRunning())
        {
            try
            {
                await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
                IpcResponse response = await client.SendAsync("command", "wm-exit").ConfigureAwait(false);

                if (response.Ok)
                {
                    Console.WriteLine("asked the window manager to exit");
                    waitingFor.Add((IpcProtocol.InstanceMutexName, "window manager"));
                }
                else
                {
                    Console.Error.WriteLine($"shubbak: the window manager refused to exit: {response.Error}");
                    allStopped = false;
                }
            }
            catch (TimeoutException)
            {
                // Its pipe is there but it did not answer in time. Its mutex is still
                // worth waiting on below, in case it was merely busy.
                Console.Error.WriteLine("shubbak: the window manager did not answer; waiting to see whether it leaves anyway.");
                waitingFor.Add((IpcProtocol.InstanceMutexName, "window manager"));
            }
        }
        else if (SingleInstanceLock.IsHeldByAnyone(IpcProtocol.InstanceMutexName) is true)
        {
            // A daemon holds the mutex but serves no pipe by our name: a different
            // protocol version, most likely. The tray menu and the bar can still stop it;
            // this command cannot reach it, and says so rather than pretending.
            Console.Error.WriteLine("shubbak: a window manager is running that this version cannot talk to (an older or newer build).");
            Console.Error.WriteLine("hint: use Exit in its tray menu, then run this again.");
            allStopped = false;
        }
        else
        {
            Console.WriteLine("no window manager is running");
        }

        int bars = CloseWindowsOfClassQuietly("TajBarWindow");
        Console.WriteLine(bars == 0 ? "no bar is running" : "asked the bar to close");
        if (bars > 0) waitingFor.Add((IpcProtocol.InstanceMutexNameFor("taj"), "bar"));

        int palettes = CloseWindowsOfClassQuietly("DalilPaletteWindow");
        Console.WriteLine(palettes == 0 ? "no palette is running" : "asked the palette to close");
        if (palettes > 0) waitingFor.Add((IpcProtocol.InstanceMutexNameFor("dalil"), "palette"));

        bool watcher = SignalWatcherToStop();
        Console.WriteLine(watcher ? "asked the watcher to stop" : "no watcher is running");
        if (watcher) waitingFor.Add((IpcProtocol.InstanceMutexNameFor("ayn"), "watcher"));

        // Generous, because the window manager un-conceals every window on its way out
        // and a desktop with many of them takes a moment; ten seconds is also what
        // --replace allows it.
        foreach ((string mutex, string what) in waitingFor)
        {
            if (WaitUntilReleased(mutex, TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine($"the {what} has stopped");
            }
            else
            {
                Console.Error.WriteLine($"shubbak: the {what} is still running after 10 s.");
                allStopped = false;
            }
        }

        if (!allStopped) return 1;

        Console.WriteLine("everything has stopped");
        return 0;
    }

    /// <summary>Brings back windows that some earlier run left concealed.</summary>
    /// <remarks>
    /// Runs entirely locally and never contacts the window manager. The situation this
    /// exists for is the one where the window manager is gone - killed, crashed, or an
    /// older build that concealed windows in a way it could not undo - so depending on
    /// it would defeat the purpose.
    /// </remarks>
    private static int Restore(string[] args)
    {
        bool dryRun = args.Contains("--dry-run") || args.Contains("-n");
        bool all = args.Contains("--all");
        bool cloakedOnly = args.Contains("--cloaked");

        List<WindowRecovery.Candidate> candidates;

        if (cloakedOnly)
        {
            candidates = WindowRecovery.FindCloaked();

            Console.WriteLine(
                "Windows that were cloaked rather than hidden. Applications hide their");
            Console.WriteLine(
                "own helper windows; Shubbak cloaks. That usually separates them, but the");
            Console.WriteLine(
                "shell also cloaks windows on other virtual desktops. Check the list.");
            Console.WriteLine();
        }
        else if (all)
        {
            candidates = WindowRecovery.FindAll();

            Console.WriteLine(
                "Warning: --all cannot tell a window Shubbak concealed from one an");
            Console.WriteLine(
                "application hid on purpose. Most of the list below is likely to be");
            Console.WriteLine(
                "background helper windows that should stay hidden. Read it carefully.");
            Console.WriteLine();
        }
        else
        {
            Session? session = SessionStore.Load();

            if (session is null)
            {
                Console.Error.WriteLine("shubbak: no saved session was found.");
                Console.Error.WriteLine(
                    "hint: without one there is no way to prove which concealed windows were");
                Console.Error.WriteLine(
                    "      Shubbak's. Try 'shubbak restore --cloaked --dry-run', which lists");
                Console.Error.WriteLine(
                    "      only windows that were cloaked rather than hidden - usually just");
                Console.Error.WriteLine(
                    "      the real application windows. Or --all to see everything.");
                return 1;
            }

            candidates = WindowRecovery.FindRemembered(session);
        }

        if (candidates.Count == 0)
        {
            Console.WriteLine("No concealed windows found.");
            return 0;
        }

        Console.WriteLine(dryRun
            ? $"Would restore {candidates.Count} window(s):"
            : $"Restoring {candidates.Count} window(s):");

        Console.WriteLine();

        foreach (WindowRecovery.Candidate candidate in candidates)
        {
            Console.WriteLine(
                $"  0x{candidate.Handle:X8}  {candidate.Reason,-16}  " +
                $"{Truncate(candidate.ProcessName, 20),-20}  {Truncate(candidate.Title, 50)}");
        }

        Console.WriteLine();

        if (dryRun)
        {
            Console.WriteLine("Nothing was changed. Re-run without --dry-run to restore them.");
            return 0;
        }

        int revived = WindowRecovery.Revive(candidates);

        Console.WriteLine($"Restored {revived} window(s).");

        if (revived < candidates.Count)
            Console.WriteLine($"{candidates.Count - revived} had already closed.");

        return 0;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "\u2026");

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

        // A lease dies with the connection that made it, and this connection closes the
        // moment the reply arrives - so the pin would be gone before the prompt came
        // back, having done nothing visible. Refused here, where the alternative can be
        // named, rather than accepted by the daemon and released a millisecond later.
        if (args.Length > 0 && string.Equals(args[0], "context", StringComparison.OrdinalIgnoreCase) &&
            Array.Exists(args, a => string.Equals(a, "--lease", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("shubbak: --lease needs a connection that stays open, and the command line's closes at once.");
            Console.Error.WriteLine("hint: use --ttl 5s and repeat, or hold a pipe connection open from your own process.");
            return 1;
        }

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

    /// <summary>Describes each display, with a definition ready to paste into the config.</summary>
    /// <remarks>
    /// A convenience over <c>query monitors</c> in the same way <c>layouts</c> is over
    /// <c>query layouts</c>, and the counterpart of <c>inspect</c>'s "write a rule for
    /// it": the fact that tells two identical monitors apart is a hundred characters of
    /// hexadecimal, and this is the step that used to be left as a transcription job.
    /// </remarks>
    private static async Task<int> MonitorsAsync()
    {
        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
        IpcResponse response = await client.SendAsync("query", "monitors").ConfigureAwait(false);

        if (!response.Ok || response.Data is null)
        {
            Console.Error.WriteLine($"shubbak: {response.Error}");
            return 1;
        }

        IReadOnlyList<MonitorInfoDto>? monitors = System.Text.Json.JsonSerializer.Deserialize(
            response.Data, IpcJsonContext.Default.IReadOnlyListMonitorInfoDto);

        Console.Write(MonitorReportText.Format(monitors ?? []));

        return 0;
    }

    /// <summary>Says which contexts hold and why, condition by condition.</summary>
    /// <remarks>
    /// The answer to "why is this context on" or "why is it not", which is the first
    /// question anybody asks a context that did not do what they expected - the same
    /// question <c>inspect</c> answers for a window that did not tile.
    /// </remarks>
    private static async Task<int> ContextsAsync()
    {
        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
        IpcResponse response = await client.SendAsync("query", "contexts").ConfigureAwait(false);

        if (!response.Ok || response.Data is null)
        {
            Console.Error.WriteLine($"shubbak: {response.Error}");
            return 1;
        }

        IReadOnlyList<ContextReport>? contexts = System.Text.Json.JsonSerializer.Deserialize(
            response.Data, IpcJsonContext.Default.IReadOnlyListContextReport);

        Console.Write(ContextReportText.Format(contexts ?? []));

        return 0;
    }

    private static async Task<int> ArrangementsAsync()
    {
        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
        IpcResponse response = await client.SendAsync("query", "arrangements").ConfigureAwait(false);

        if (!response.Ok || response.Data is null)
        {
            Console.Error.WriteLine($"shubbak: {response.Error}");
            return 1;
        }

        IReadOnlyList<ArrangementInfo> arrangements = System.Text.Json.JsonSerializer.Deserialize(
            response.Data, IpcJsonContext.Default.IReadOnlyListArrangementInfo) ?? [];

        if (arrangements.Count == 0)
        {
            Console.WriteLine("no arrangements saved");
            Console.WriteLine("hint: arrangement --save <name> records the focused workspace's tree of windows");
            return 0;
        }

        int width = arrangements.Max(a => a.Name.Length);

        foreach (ArrangementInfo arrangement in arrangements)
        {
            Console.WriteLine(
                $"{arrangement.Name.PadRight(width)}  {arrangement.Windows} window(s) on workspace \"{arrangement.Workspace}\", " +
                $"saved {DateTimeOffset.FromUnixTimeMilliseconds(arrangement.SavedAtUnixMs).ToLocalTime():yyyy-MM-dd HH:mm}");
        }

        return 0;
    }

    private static async Task<int> StatusAsync()
    {
        await using IpcClient client = await ConnectAsync().ConfigureAwait(false);
        IpcResponse response = await client.SendAsync("ping").ConfigureAwait(false);

        if (!response.Ok)
        {
            Console.WriteLine("not responding");
            return 1;
        }

        // "running" alone is not enough once suspending exists. A suspended window
        // manager is running and deliberately doing nothing, which from the outside is
        // indistinguishable from one that has stopped working - and somebody who
        // suspended it before a game and then wondered why their keys do nothing needs
        // to be told which of the two they are looking at.
        IpcResponse state = await client.SendAsync("query", "state").ConfigureAwait(false);

        if (state.Ok && state.Data is { Length: > 0 } payload)
        {
            StateSnapshot? snapshot = JsonSerializer.Deserialize(
                payload, IpcJsonContext.Default.StateSnapshot);

            if (snapshot is { Suspended: true })
            {
                Console.WriteLine("running, suspended");
                Console.WriteLine("hint: shubbak wm-resume takes the keyboard back");
                return 0;
            }

            if (snapshot is { Paused: true })
            {
                Console.WriteLine("running, paused");
                Console.WriteLine("hint: shubbak wm-toggle-pause starts arranging windows again");
                return 0;
            }

            // A context changes what the desktop does - the gaps, the keys, where a
            // workspace lives - and from the outside looks like a setting having changed
            // by itself. Named here so "why are my gaps gone" has a first place to look.
            if (snapshot?.Contexts is { Count: > 0 } contexts)
            {
                Console.WriteLine($"running, in context {string.Join(", ", contexts)}");
                Console.WriteLine("hint: shubbak contexts says why");
                return 0;
            }
        }

        Console.WriteLine("running");
        return 0;
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

        // Stops one short of the end so the value can be read, which meant a trailing
        // -o with nothing after it was skipped entirely and the report went to stdout
        // instead - the one place the user was certain it would not.
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is not ("--output" or "-o")) continue;

            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"shubbak: {args[i]} needs a file to write to.");
                Console.Error.WriteLine("hint: shubbak diagnose -o report.md");
                return 1;
            }

            output = args[i + 1];
        }

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
    /// <summary>Reads a window handle written as decimal or as 0x hex.</summary>
    /// <remarks>
    /// Hex is accepted because it is the form this tool prints, and the form the log
    /// prints. Only decimal parsed, so pasting back a handle the tool had just shown
    /// silently fell through to "wait three seconds and take the foreground window" -
    /// which answers a different question and looks like the handle was accepted.
    /// </remarks>
    private static bool TryParseHandle(string text, out nint handle)
    {
        handle = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        bool hex = span.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        if (hex) span = span[2..];

        if (!long.TryParse(
                span,
                hex ? System.Globalization.NumberStyles.HexNumber
                    : System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out long value))
        {
            return false;
        }

        handle = (nint)value;
        return handle != 0;
    }

    /// <summary>
    /// Lists every top-level window with the verdict the filter reaches for it.
    /// </summary>
    /// <remarks>
    /// The answer to "why is that application not being tiled?", which otherwise
    /// requires knowing the window's handle before you can ask about it - and the
    /// windows worth asking about are exactly the ones that are hard to point at.
    /// Runs entirely locally, so it works with nothing running.
    /// </remarks>
    private static int InspectAll()
    {
        Console.WriteLine($"{"handle",-10}  {"process",-24}  {"class",-34}  verdict");
        Console.WriteLine(new string('-', 110));

        int manageable = 0;
        int total = 0;

        foreach (nint handle in Win32Window.EnumerateTopLevel())
        {
            ManageDecision decision = WindowFilter.Evaluate(handle);
            string title = Win32Window.GetTitle(handle);

            // Untitled and unmanageable together means a helper window nobody has any
            // interest in, and there are hundreds. Anything with a title is worth
            // listing even when it is rejected: that is the case being investigated.
            if (title.Length == 0 && !decision.Manageable) continue;

            total++;
            if (decision.Manageable) manageable++;

            Console.WriteLine(
                $"0x{handle:X8}  {Trim(Win32Window.BuildIdentity(handle).ProcessName, 24),-24}  " +
                $"{Trim(Win32Window.GetClassName(handle), 34),-34}  " +
                $"{(decision.Manageable ? "MANAGEABLE" : decision.Explain())}");

            if (title.Length > 0) Console.WriteLine($"{"",-10}  {Trim(title, 90)}");
        }

        Console.WriteLine();
        Console.WriteLine($"{total} window(s) listed, {manageable} manageable.");
        Console.WriteLine("Override a verdict with a rule:  shubbak rule add <handle> --manage");
        Console.WriteLine("  (or by hand:  rules { rule \"x\" { match { process = \"...\" } do { manage } } })");

        return 0;

        static string Trim(string text, int width) =>
            text.Length <= width ? text : text[..(width - 1)] + "\u2026";
    }

    private static async Task<int> InspectAsync(string[] args)
    {
        if (args.Length > 1 && args[1] is "--all" or "-a")
            return InspectAll();

        nint handle = 0;

        if (args.Length > 1)
        {
            if (!TryParseHandle(args[1], out handle))
            {
                Console.Error.WriteLine($"shubbak: '{args[1]}' is not a window handle.");
                Console.Error.WriteLine("hint: decimal, hex (0x20A44), or --all to list every window.");
                return 1;
            }
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

        // With no window manager there is no tree and no configuration, so only the
        // attributes and the filter's verdict can be answered - but they can be
        // answered entirely locally, which is the point: inspect still works when the
        // thing being diagnosed is that nothing is running.
        if (!IpcClient.IsServerRunning())
        {
            Console.WriteLine();
            Console.Write(WindowReportText.Format(LocalReport(handle), complete: false));
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

        WindowReport? report = response.Data is { Length: > 0 } json
            ? JsonSerializer.Deserialize(json, IpcJsonContext.Default.WindowReport)
            : null;

        if (report is null)
        {
            // Not a version mismatch: the protocol version is in the pipe name, so a
            // daemon from another release is not found at all rather than misread.
            // Reaching here means the report itself was empty or malformed.
            Console.Error.WriteLine("shubbak: the window manager sent a report that could not be read.");
            Console.Error.WriteLine("hint: shubbak diagnose -o report.md, and please open an issue.");
            return 1;
        }

        Console.WriteLine();
        Console.Write(WindowReportText.Format(report));
        return 0;
    }

    /// <summary>
    /// As much of a report as can be worked out without the window manager.
    /// </summary>
    /// <remarks>
    /// The attributes and the filter's verdict, which are properties of the window and
    /// of Win32 rather than of anything Shubbak is running. The tree and the rules are
    /// left empty and the caller says so, rather than being reported as absent - "no
    /// rules configured" and "no rules visible from here" are different answers and
    /// only one of them is true.
    /// </remarks>
    private static WindowReport LocalReport(nint handle)
    {
        ManageDecision decision = WindowFilter.Evaluate(handle);

        uint processId = Win32Window.GetProcessId(handle);
        string? path = Win32Window.GetProcessPath(processId);
        Core.Geometry.Rect bounds = Win32Window.GetBounds(handle);

        return new WindowReport(
            handle,
            Win32Window.GetTitle(handle),
            Win32Window.GetClassName(handle),
            path is null ? "(unreadable)" : Path.GetFileNameWithoutExtension(path),
            path,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            Win32Window.GetStyleBits(handle),
            Win32Window.GetExStyleBits(handle),
            Win32Window.IsVisible(handle),
            Win32Window.GetCloakState(handle).ToString(),
            Win32Window.IsMinimised(handle),
            decision.Manageable,
            decision.Explain(),
            decision.Summarise(),
            Managed: false,
            ExcludedByRule: false,
            Node: null,
            Rules: [],
            Apps: []);
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

        // The bar's section lives in the same file and was checked by nothing. A
        // mistyped bar setting produced no diagnostic here and no diagnostic there -
        // the only symptom was a setting that appeared to do nothing.
        (Taj.Core.TajConfig bar, IReadOnlyList<Diagnostic> barDiagnostics) =
            Taj.Core.TajConfigLoader.Load(source);

        foreach (Diagnostic diagnostic in barDiagnostics)
            Console.Error.Write(diagnostic.Render(source, path));

        // And the palette's, for exactly the same reason. Its section name was on the
        // allow-list and its contents were on nobody's, so `dalil { with-icons #true }`
        // was accepted in silence and did nothing for ever.
        Dalil.Core.DalilConfigLoad paletteLoad = Dalil.Core.DalilConfigLoader.Validate(source);
        IReadOnlyList<Diagnostic> paletteDiagnostics = paletteLoad.Diagnostics;

        foreach (Diagnostic diagnostic in paletteDiagnostics)
            Console.Error.Write(diagnostic.Render(source, path));

        // The watcher's section, for the same reason. Warnings only; nothing in it is
        // fatal, since every setting has a default.
        IReadOnlyList<Diagnostic> watcherDiagnostics = Ayn.Core.AynConfigLoader.Validate(source).Diagnostics;

        foreach (Diagnostic diagnostic in watcherDiagnostics)
            Console.Error.Write(diagnostic.Render(source, path));

        int errors = result.Errors.Count() +
            barDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) +
            paletteDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) +
            watcherDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

        int warnings = result.Warnings.Count() +
            barDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning) +
            paletteDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning) +
            watcherDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        Console.WriteLine(
            errors == 0 && warnings == 0
                ? $"{path}: ok - {result.Config.Keybindings.Count} keybindings, " +
                  $"{result.Config.Workspaces.Count} workspaces, {result.Config.Rules.Count} rules, " +
                  (result.Config.Contexts.Count == 0 ? "" : $"{result.Config.Contexts.Count} context(s), ") +
                  $"{bar.Profiles.Count} bar profile(s)" +
                  (paletteLoad.Config.Macros.Count == 0
                      ? " "
                      : $", {paletteLoad.Config.Macros.Count} palette action(s) ") +
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

        GETTING STARTED
          config init          Write a starter config - keys, the bar, the palette and
                               the watcher - where the window manager will find it.
          shubbak-wm           Start the window manager (a separate program). Add
                               --foreground to keep it attached to this terminal.
          autostart enable     Have it start at logon from now on.
          status               Is it running? paused? suspended?
          stop                 Stop it and everything it started. Also the thing to
                               run before replacing the executables by hand; winget
                               and the MSI do it themselves.

          The full guide: https://github.com/MoaidHathot/Shubbak/blob/main/docs/getting-started.md

        WINDOW MANAGER COMMANDS
          Anything not listed below is sent straight to the window manager, using
          exactly the same syntax as a keybinding:

            shubbak focus --direction left
            shubbak focus --workspace 3
            shubbak move --workspace 3
            shubbak move --workspace 3 --focus
            shubbak resize --width +5%
            shubbak layout --set fibonacci
            shubbak layout --cycle
            shubbak toggle-floating
            shubbak wm-reload-config
            shubbak wm-exit
            shubbak exit-all

          wm-exit stops the window manager properly: it saves the session, brings
          every concealed window back, and takes the bar down with it. The palette
          and the watcher stay and reconnect when it returns, which is what you want
          when restarting it. exit-all takes all four down, and is what the tray's
          Exit runs; 'stop' does the same from outside, and also works when the
          window manager is already gone. Terminating the process instead strands
          windows off screen - use 'restore' if that has already happened.

        GETTING OUT OF THE WAY
          wm-toggle-suspend    Release the keyboard hook and the window event hooks,
          wm-suspend           leaving every window where it is. For a game: a chord
          wm-resume            Shubbak swallows is a chord the game never sees.

                               Resume with the same key that suspended - the system
                               watches for that one chord while suspended, which is
                               not a hook and costs nothing per keystroke - or with
                               `shubbak wm-resume`.

          wm-toggle-pause      Different, and worth not confusing: stops windows
                               being rearranged but keeps the keyboard, so every
                               binding still works.

        PROCESSES
          stop                 Stop the window manager, the bar, the palette and the
                               watcher, and wait until each is actually gone. The
                               window manager goes first, over the pipe, so it saves
                               the session and brings every concealed window back;
                               the others are asked directly, so this also works when
                               no window manager is running. Exits non-zero if
                               anything is still running after ten seconds.

          taj-exit             Close the bar, leaving the window manager running.
                               Asks its windows to close rather than terminating
                               it, so the strip of screen it reserved is given
                               back. Works when no window manager is running,
                               which is the case that most needs it.

          dalil-exit           Close the command palette, the same way. Both of
                               these refuse to start a second copy of themselves,
                               so this is how you stop the one that is running.

          ayn-exit             Stop the watcher. It has no window, so it is asked
                               through a named event and waited for; its leases on
                               the window manager are released the moment its
                               connection closes.

          autostart <action>   Whether the window manager starts when you log in.
                               enable | disable | status

                               enable records the full path of the shubbak-wm.exe
                               sitting beside this binary, so moving the install
                               means enabling it again - which status will tell
                               you, along with whether the registered copy still
                               exists at all.

                               Arguments after 'enable' are passed to the daemon:

                                 shubbak autostart enable --config D:\dots\shubbak.kdl

        DIAGNOSTICS
          diagnose [reason]    Write a self-contained report: environment, config,
                               the live window tree, and the recent log. This is the
                               one command to run when something is wrong.
                    -o <path>  Write to a file instead of stdout.

          restore              Bring back windows left concealed by a window manager
                               that exited without restoring them - after a crash, a
                               kill, or an older build. Works with nothing running.
                               By default only restores windows the saved session
                               proves Shubbak was managing.
                    --dry-run  List what would be restored, and change nothing.
                    --cloaked  Restore windows that were cloaked rather than hidden.
                               Usually just the real application windows, since
                               applications hide their own helpers. Use when the
                               session is missing.
                    --all      Restore everything concealed. Includes background
                               helper windows that should stay hidden - read first.


          log-level <level>    Change the log level of the running window manager
                               without restarting it.
                               trace | debug | info | warn | error | none

          inspect [handle]     Describe a window and explain how Shubbak sees it:
                               every matchable attribute, whether it is manageable
                               and why not, and which rules matched.
                               With no handle, waits 3 seconds then inspects the
                               foreground window. Handles may be decimal or hex.
                    --all      List every top-level window with its verdict. Use
                               this to find out why an app is not being tiled.

          check-config [path]  Validate a config file, with carets under any
                               problems. Exits non-zero only for errors.

          config init          Write a starter config, if there is not one already.
                    --path <p> Write it somewhere specific.
                    --force    Overwrite an existing file.

          config-path          Print which config file is in effect, or list
                               everywhere that was searched if none was found.

          rule list            The rules in force, with the line each is on and
                               what each does.
          rule add [handle]    Write a rule for a window into the config and
                               reload. Says what the rule does with one or more of
                               --ignore, --manage, --float, --tile or
                               --workspace <name>. With no handle, waits 3 seconds
                               then uses the foreground window. The window manager
                               appends the rule as a block of its own, validates
                               the file first, and refuses rather than leaving it
                               broken.
                    --print    Print the rule instead of adding it.
                    --file <p> Add the rules in a KDL file instead (- for stdin).
          rule remove <name>   Take a rule out of the config and reload. Prints
                               the rule so it can be put back.
                    --line <n> Which one, when two rules share a name.

          status               Report whether a window manager is running.

          --version            Print the version and exit. Answered by this binary
                               without contacting the window manager, so it works
                               when nothing is running.

        QUERIES
          query [what]         Print state as JSON.
                               what: state (default), windows, all-windows,
                                     every-window, workspaces, monitors, focused,
                                     layouts, commands, bindings, contexts,
                                     arrangements, rules
          layouts              List the available layouts.
          monitors             Describe each display, with a `monitor` definition
                               ready to paste into the config. Displays are named
                               by what they are, so a workspace bound to one stays
                               there however Windows renumbers them.
          contexts             Say which contexts hold and why, condition by
                               condition, and who pinned what.
          arrangements         List the saved arrangements.

          all-windows lists every window on the desktop, not only the managed
          ones, with the reason each unmanaged window was passed over. It is the
          place to look for a window that has gone missing.

        CONTEXTS
          context --set <name>      Hold a context on, whatever its conditions say.
          context --clear <name>    Hold it off.
          context --toggle <name>   Flip it.
          context --auto <name>     Take the pin off; its conditions decide again.
            --ttl 5s                The pin comes off by itself after that long.
                                    500ms, 5s, 2m, 1h; a bare number is seconds.
            --lease                 The pin comes off when the connection that made
                                    it closes. Not from the command line, whose
                                    connection closes at once; for a process that
                                    watches something Shubbak does not.

          A context with no `when` block is external: nothing on the desktop
          decides it, and setting it over the pipe is how another program tells
          Shubbak about a meeting, a call, a recording. The config says what the
          context does; the program never needs to know.

        ARRANGEMENTS
          arrangement --save <name>     Record the focused workspace's tree of windows -
                                        containers, layouts, ratios, and which window
                                        sits where - under that name.
          arrangement --restore <name>  Put the windows that are on the workspace back
                                        into that tree. Windows not in it stay, at the
                                        end; windows in it that are not open are left
                                        out. Refused when none of them are open.
          arrangement --delete <name>   Forget it.

          Windows are recorded by process and class, never by title, and matched the
          same way; the session file records which workspace a window belongs to, and
          an arrangement records the shape - the two things a demo needs after its
          windows have been dragged about.

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
