using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Dalil.Core;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using Shubbak.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dalil;

/// <summary>
/// The palette host.
/// </summary>
/// <remarks>
/// <para>
/// One thread owns the window and the model; everything from the pipe is marshalled
/// onto it. That is the same discipline the daemon uses and for the same reason - a
/// UI updated from two threads is a source of faults that only appear under load.
/// </para>
/// <para>
/// Started from the user's <c>startup-commands</c>, like the bar. Shubbak does not
/// launch it and does not know it exists: the keybinding says <c>signal "palette"</c>
/// and this is whatever happens to be listening.
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly ConcurrentQueue<Action> s_inbox = new();

    private static PaletteWindow? s_palette;
    private static WmConnection? s_connection;
    private static DalilConfig s_config = new();
    private static PaletteSources s_sources = PaletteSources.Empty;
    private static CompletionSources s_completions = CompletionSources.None;
    private static uint s_threadId;
    private static bool s_running = true;
    private static string? s_configPath;

    /// <summary>
    /// The window that had the keyboard when the palette was asked for.
    /// </summary>
    /// <remarks>
    /// Read before the palette takes the foreground, because a moment later the answer
    /// is the palette itself. It is what the contextual first row is about: the window
    /// somebody was just looking at is the one they are about to ask a question
    /// concerning.
    /// </remarks>
    private static long s_foreground;

    /// <summary>
    /// Whether the inspect list is showing every window the filter turned down, rather
    /// than the ones worth listing by default.
    /// </summary>
    /// <remarks>
    /// Flipped by a row in that list and forgotten when the palette closes. Held here
    /// because it decides which query the read asks, and the read is the host's.
    /// </remarks>
    private static bool s_everyWindow;

    /// <summary>
    /// What was wrong with the palette's own section, last time it was read.
    /// </summary>
    /// <remarks>
    /// Kept so the <c>config</c> row in the command list can say how many problems
    /// there are without re-reading the file, and so the row can be absent entirely
    /// when there are none - which is the ordinary case and should cost nothing to
    /// look at.
    /// </remarks>
    private static DiagnosticCounts s_problems;

    /// <summary>The diagnostics themselves, for the frame that lists them.</summary>
    private static IReadOnlyList<Diagnostic> s_diagnostics = [];

    [STAThread]
    private static int Main(string[] args)
    {
        // Dalil is a GUI-subsystem binary, so it has no console until one is asked
        // for. It had no --help at all before this; the palette was the only thing it
        // could be told to do.
        if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
        {
            ConsoleHost.Ensure();
            PrintUsage();
            return 0;
        }

        if (Array.Exists(args, a => a is "--version" or "-v" or "version"))
        {
            ConsoleHost.Ensure();
            Console.WriteLine(ShubbakVersion.Banner);
            return 0;
        }

        // Before any window is created: without it Windows reports virtualised
        // coordinates on scaled displays and the palette lands in the wrong place.
        // The cast is how the context handles are spelled - they are sentinel values,
        // not an enum CsWin32 can name.
        PInvoke.SetProcessDpiAwarenessContext((DPI_AWARENESS_CONTEXT)(nint)(-4));

        s_threadId = PInvoke.GetCurrentThreadId();
        s_configPath = PathFrom(args);

        // Before the config is read, so a theme colour written `accent` is this
        // machine's rather than the stock blue.
        SystemColours.Adopt();

        ConfigureLogging(args);

        // One palette per account, for the same reason there is one window manager.
        //
        // Dalil is opened by a signal rather than by being started, so two of them are
        // both subscribed and both answer: one keypress raises two windows, stacked and
        // both topmost, with the keyboard going to whichever won the race and Escape
        // dismissing one of them to reveal the other underneath.
        //
        // Nothing strange has to happen to end up with two. The palette survives the
        // window manager restarting - deliberately, it reconnects - and the restarted
        // window manager then runs its startup commands, one of which starts a palette.
        using SingleInstanceLock instance = SingleInstanceLock.Claim(
            IpcProtocol.InstanceMutexNameFor("dalil"));

        // An uncertain answer starts anyway. Two palettes are confusing and can be
        // undone; no palette, because a mutex could not be opened, removes the only way
        // into half of what Shubbak can do.
        if (!instance.Held && instance.Certain)
        {
            ConsoleHost.Ensure();
            Console.Error.WriteLine("dalil: a palette is already running.");
            Console.Error.WriteLine("hint: `shubbak dalil-exit` stops it.");

            Log.Info(LogCategory.Wm, "another palette is already running; leaving it to it");
            return 1;
        }

        s_config = LoadConfig().Config;

        s_palette = new PaletteWindow(s_config);

        if (!s_palette.Create())
        {
            Console.Error.WriteLine("dalil: could not create the palette window");
            return 1;
        }

        s_palette.CommandRequested += OnCommand;

        // Typed commands become a row of their own, parsed by the same parser the
        // config file uses. Without this, every verb that takes an argument was a
        // dead end: the term outgrew the verb it named, matched nothing, and Enter
        // had no row to act on.
        //
        // The contextual row rides in the same place, because it is the same idea: a
        // row derived from the state rather than filtered out of a list. It appears
        // only on an untouched window list, so it can never sit above what somebody is
        // actually searching for.
        s_palette.Augment((mode, term) => mode switch
        {
            PaletteMode.Commands => CommandComposer.Compose(term, s_completions),

            PaletteMode.Windows when term.Length == 0 => s_sources.Context ?? [],

            _ => [],
        });

        // Every route into a mode refills the list: Tab, a jump key, typing a prefix,
        // deleting one, or choosing a mode from the help list. Without this, typing
        // ">" left the box saying "commands" while the rows underneath were still
        // windows.
        s_palette.ModeChanged += mode => s_palette!.SetEntries(s_sources.For(mode));

        // Asked on a background thread and shown on the palette's own, because the
        // window manager has to assemble the report and the message loop cannot wait
        // for it without freezing the very window that is meant to display it.
        s_palette.ExplainRequested += (handle, title) => _ = Task.Run(async () =>
        {
            string? failure = null;

            WindowReport? report = await s_connection!
                .InspectAsync(handle, reason => failure = reason)
                .ConfigureAwait(false);

            Post(() =>
            {
                if (report is not null) s_palette?.ShowReport(title, report);
                else s_palette?.ShowReportFailure(title, failure ?? "Nothing to report");
            });
        });

        // The same fetch, shown as the rules that could be written rather than as the
        // facts they rest on. The facts decide the rules - whether the filter could be
        // overruled, which rules already match - which is why the report is fetched
        // rather than the row's own attributes used.
        s_palette.ComposeRequested += (handle, title) => _ = Task.Run(async () =>
        {
            string? failure = null;

            WindowReport? report = await s_connection!
                .InspectAsync(handle, reason => failure = reason)
                .ConfigureAwait(false);

            Post(() =>
            {
                if (report is not null) s_palette?.ShowRuleChoices(title, report);
                else s_palette?.ShowReportFailure(title, failure ?? "Nothing to write a rule from");
            });
        });

        // The two requests that change the configuration file. The window manager does
        // the editing and answers with what happened, and the answer carries the undo,
        // so the frame that shows it is where a mistake is taken back.
        s_palette.ApplyRuleRequested += (rule, title) => _ = Task.Run(async () =>
        {
            string? failure = null;

            RuleChange? change = await s_connection!
                .AddRuleAsync(rule.Kdl, rule.Handle, reason => failure = reason)
                .ConfigureAwait(false);

            Post(() =>
            {
                if (change is not null) s_palette?.ShowRuleChange(title, change, added: true);
                else s_palette?.ShowReportFailure(title, failure ?? "The rule was not added.");
            });
        });

        s_palette.RemoveRuleRequested += (rule, title) => _ = Task.Run(async () =>
        {
            string? failure = null;

            RuleChange? change = await s_connection!
                .RemoveRuleAsync(rule.Name, rule.Line, rule.Handle, reason => failure = reason)
                .ConfigureAwait(false);

            Post(() =>
            {
                if (change is not null) s_palette?.ShowRuleChange(title, change, added: false);
                else s_palette?.ShowReportFailure(title, failure ?? "The rule was not removed.");
            });
        });

        // The widened inspect list is a moment, not a preference.
        s_palette.Closed += () => s_everyWindow = false;

        // A palette that lost the foreground moments after opening kept itself open and
        // wants it back; the same repairs a failed open gets.
        s_palette.ForegroundLost += () => ScheduleForegroundRepair(s_palette!);

        PaletteWindow.RequestShutdown += () => s_running = false;

        s_connection = new WmConnection();
        s_connection.Signalled += OnSignal;
        s_connection.Stale += () => Post(MarkStale);
        s_connection.Reloaded += () => Post(ReloadConfig);
        s_connection.ShuttingDown += everything => Post(() => OnWindowManagerLeaving(everything));

        // Read once on connecting, open or not, so the first open of a session has
        // lists to show rather than an empty box that grows into them; see
        // WmConnection.Connected. A reconnection reads again for the same reason -
        // the world moved while the window manager was away.
        s_connection.Connected += () => Post(() =>
        {
            if (s_palette is { } palette) Refresh(palette);
        });

        s_connection.Start();

        Log.Info(LogCategory.Wm,
            $"dalil is listening for signal \"{s_config.OpenOnSignal}\"");

        RunMessageLoop();

        s_palette.Dispose();
        s_connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

        return 0;
    }

    /// <summary>
    /// Acts on a chosen row - or answers it here, when it was one of the palette's own.
    /// </summary>
    /// <remarks>
    /// Marked with a scheme rather than guessed at, so a window title that happens to
    /// begin with the word "diagnose" cannot be mistaken for a request to write a
    /// report.
    /// </remarks>
    private static void OnCommand(string command)
    {
        if (!PaletteEntries.IsBuiltin(command))
        {
            _ = s_connection!.SendAsync(command);
            return;
        }

        if (string.Equals(command, PaletteEntries.BuiltinConfig, StringComparison.Ordinal))
        {
            // Answered from what the last load produced rather than by re-reading. The
            // list is about the configuration the palette is actually running on, which
            // after a refused reload is not what is currently on disk.
            string? path = ConfigPathResolver.Resolve(s_configPath).Path;

            if (s_palette is { } showing)
            {
                showing.Open(PaletteMode.Commands);
                showing.Push("config", PaletteEntries.ForDiagnostics(s_diagnostics, path));
            }

            return;
        }

        if (string.Equals(command, PaletteEntries.BuiltinConfigPath, StringComparison.Ordinal))
        {
            string path = ConfigPathResolver.Resolve(s_configPath).Path ??
                "no configuration file was found";

            if (s_palette is { } showing)
            {
                showing.Open(PaletteMode.Commands);

                // Copied and then shown, rather than copied in silence. A clipboard is
                // the one place a program can put something with no way for anybody to
                // tell whether it worked, and the row that says which path went there
                // is also the row that answers "which file is in effect".
                showing.CopyToClipboard(path);
                showing.ShowReportFailure("config path", path);
            }

            return;
        }

        if (string.Equals(command, PaletteEntries.BuiltinOpenConfig, StringComparison.Ordinal))
        {
            OpenConfig();
            return;
        }

        if (string.Equals(command, PaletteEntries.BuiltinEveryWindow, StringComparison.Ordinal))
        {
            // The palette stayed open for this - see PaletteWindow.Send - so the list it
            // is looking at is refilled in place once the wider read lands.
            s_everyWindow = !s_everyWindow;

            if (s_palette is { IsOpen: true } showing) Refresh(showing);

            return;
        }

        if (string.Equals(command, PaletteEntries.BuiltinReload, StringComparison.Ordinal))
        {
            ReloadConfig();

            if (s_palette is { } showing)
            {
                showing.Open(PaletteMode.Commands);

                showing.ShowReportFailure(
                    "reload palette",
                    s_problems.Errors + s_problems.Warnings == 0
                        ? "The dalil section was re-read and had nothing wrong with it."
                        : $"The dalil section was re-read; {Describe(s_problems)}. Choose 'config' to read them.");
            }

            return;
        }

        if (string.Equals(command, PaletteEntries.BuiltinActions, StringComparison.Ordinal))
        {
            if (s_palette is { } showing)
            {
                showing.Open(PaletteMode.Commands);

                // Built from the same sources the command list uses, so a prompting
                // action opened from here asks exactly what it asks from there.
                showing.Push("actions", PaletteEntries.ForMacros(
                    s_config.Macros, s_completions, s_sources.WorkspaceLabels));
            }

            return;
        }

        if (string.Equals(command, PaletteEntries.BuiltinDiagnose, StringComparison.Ordinal))
        {
            _ = Task.Run(async () =>
            {
                string? failure = null;

                string? path = await s_connection!
                    .DiagnoseAsync(reason => failure = reason)
                    .ConfigureAwait(false);

                // Announced in the palette rather than only in the log. The whole point
                // of moving this off the command line is that somebody who wanted a
                // report should not then have to go looking for where it went.
                Post(() =>
                {
                    if (s_palette is not { } palette) return;

                    palette.Open(PaletteMode.Commands);

                    if (path is not null)
                        palette.ShowReportFailure("diagnose", $"Report written to {path}");
                    else
                        palette.ShowReportFailure("diagnose", failure ?? "Could not write a report.");
                });
            });

            return;
        }

        Log.Warn(LogCategory.Wm, $"nothing here answers '{command}'");
    }

    /// <summary>How many things were wrong, in words rather than in two numbers.</summary>
    private static string Describe(DiagnosticCounts counts)
    {
        List<string> parts = [];

        if (counts.Errors > 0)
            parts.Add(counts.Errors == 1 ? "1 error" : $"{counts.Errors} errors");

        if (counts.Warnings > 0)
            parts.Add(counts.Warnings == 1 ? "1 warning" : $"{counts.Warnings} warnings");

        return string.Join(" and ", parts);
    }

    /// <summary>
    /// Opens the configuration file in whatever Windows opens it with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Handed to the shell, which is the only program entitled to decide what edits a
    /// <c>.kdl</c> file. The palette could have taken an <c>editor</c> setting and did
    /// not: that answer already exists once, in the file association, and a second
    /// copy of it in <c>shubbak.kdl</c> would be a second thing to keep right. A
    /// machine with no association gets Windows' own "How do you want to open this
    /// file?" prompt, which asks the question in the place the answer is kept.
    /// </para>
    /// <para>
    /// The palette has already closed by the time this runs - every command closes it
    /// first, because the thing asked for usually raises a window and a topmost palette
    /// would cover it. That is the right outcome here too; the editor is what was asked
    /// for. Only a failure reopens it, to say so where the request was made rather than
    /// in a log nobody is looking at.
    /// </para>
    /// <para>
    /// The exceptions are the ones the shell raises for a file that is not there, a
    /// verb nobody has registered and an association that points at a program that has
    /// been uninstalled. Anything else is a bug and stays one.
    /// </para>
    /// </remarks>
    private static void OpenConfig()
    {
        string? path = ConfigPathResolver.Resolve(s_configPath).Path;

        if (path is not { Length: > 0 })
        {
            Complain("No configuration file was found to open. `shubbak config init` writes one.");
            return;
        }

        try
        {
            using var editor = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            Log.Error(LogCategory.Config, $"could not open the configuration file {path}", ex);
            Complain($"Windows could not open {path}: {ex.Message}");
        }

        // Where the request was made, not where nobody is looking. The palette closed
        // when the row was chosen, so it has to be brought back to carry the answer.
        static void Complain(string reason)
        {
            if (s_palette is not { } showing) return;

            showing.Open(PaletteMode.Commands);
            showing.ShowReportFailure("open config", reason);
        }
    }

    /// <summary>
    /// Waits for input or for work, and does nothing at all in between.
    /// </summary>
    /// <remarks>
    /// <c>MsgWaitForMultipleObjectsEx</c> rather than a poll with a sleep. A palette
    /// that is closed should cost nothing, and a poll would also put up to its own
    /// interval in front of every keystroke - which on a 16 ms tick is most of the
    /// budget for feeling instant.
    /// <para>
    /// <c>MWMO_INPUTAVAILABLE</c> matters: without it, input that arrived between the
    /// last peek and the wait is not counted as new and the thread sleeps through it.
    /// </para>
    /// </remarks>
    private static void RunMessageLoop()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            s_running = false;
        };

        while (s_running)
        {
            while (PInvoke.PeekMessage(out MSG msg, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                if (msg.message == PInvoke.WM_QUIT)
                {
                    s_running = false;
                    break;
                }

                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }

            Drain();
            RescueStrandedPalette();

            if (!s_running) break;

            PInvoke.MsgWaitForMultipleObjectsEx(
                [],
                250,
                QUEUE_STATUS_FLAGS.QS_ALLINPUT,
                MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
        }
    }

    /// <summary>
    /// Puts away a palette that is on screen and cannot be reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A window that never became active is never told it has been deactivated, so
    /// close-on-blur cannot dismiss it, Escape never arrives, and it sits on top of
    /// everything looking perfectly normal and answering nothing. The only way out is
    /// to notice from the outside.
    /// </para>
    /// <para>
    /// <c>PaletteWindow.IsStranded</c> was written for exactly this, with a careful
    /// explanation of why it mattered, and was then referenced from nowhere at all -
    /// so the failure it describes has been unhandled ever since. It is checked here
    /// after every drain, which costs one <c>GetForegroundWindow</c> and only while
    /// the palette is open.
    /// </para>
    /// <para>
    /// After every drain is also immediately after <c>Open</c>, and that was the bug
    /// this used to have: a palette whose foreground switch had not landed by the end
    /// of the very call that showed it was hidden again in the same turn of the loop,
    /// a frame after appearing. So a palette still inside its opening grace is only
    /// ever retried here; the scheduled repairs keep the loop turning through the
    /// grace, and the first turn after it is where a palette that is still not in
    /// front is finally given up on - saying what stood in the way, which is the one
    /// fact anybody investigating will want.
    /// </para>
    /// <para>
    /// Past the grace, a palette that is not in front is one of two things, and the
    /// old code treated them alike. One that had the keyboard and lost it was
    /// <em>left</em>: the user clicked elsewhere or Alt+Tabbed away, and taking the
    /// foreground back - which this did, before closing - meant fighting the very
    /// switch the user was making, and with <c>close-on-blur #false</c> meant winning
    /// it, every quarter of a second, for as long as the palette stayed open. That is a
    /// blur, and close-on-blur decides it. One that never had the keyboard is
    /// <em>stranded</em>, and is put away whatever the setting, because nobody can
    /// reach it.
    /// </para>
    /// </remarks>
    private static void RescueStrandedPalette()
    {
        if (s_palette is not { IsOpen: true } palette || !palette.IsStranded) return;

        if (PaletteInput.IsSettlingIn(palette.TimeSinceShown))
        {
            // Paced. The loop wakes on every message, and asking for the foreground
            // joins and splits input queues each time; once every few frames is as
            // often as a switch that is going to land needs to be asked.
            if (Stopwatch.GetElapsedTime(s_lastRescueTicks) < PaletteInput.RetryInterval) return;

            s_lastRescueTicks = Stopwatch.GetTimestamp();
            _ = palette.EnsureForeground();
            return;
        }

        if (palette.HadForeground)
        {
            // Left, not stranded. WM_ACTIVATE has usually already answered this; this
            // is the same answer for the moment between the foreground moving and the
            // message arriving, and no answer at all when blur is not meant to close.
            if (!palette.ClosesOnBlur) return;

            // At the default level, naming who has it. A palette that closed because
            // the user clicked away is one line per dismissal; a palette that "closed
            // by itself" is a report whose only evidence is this line.
            Log.Info(LogCategory.Wm,
                $"put away: the palette lost the foreground to {Foreground.DescribeCurrent()} " +
                $"{palette.TimeSinceShown.TotalMilliseconds:F0} ms after opening");

            palette.Close();
            return;
        }

        Log.Warn(LogCategory.Wm,
            $"the palette is on screen but unreachable {palette.TimeSinceShown.TotalMilliseconds:F0} ms after opening " +
            $"({palette.LastForegroundObstacle}); putting it away. A window running higher than Dalil - an " +
            "elevated terminal, say - is the one thing this cannot take the keyboard from.");

        palette.Close();
    }

    /// <summary>When the loop last asked for the foreground on its own initiative.</summary>
    private static long s_lastRescueTicks;

    /// <summary>Queues work for the message loop and wakes it.</summary>
    private static void Post(Action work)
    {
        s_inbox.Enqueue(work);
        PInvoke.PostThreadMessage(s_threadId, PaletteWindow.WakeMessage, default, default);
    }

    private static void Drain()
    {
        while (s_inbox.TryDequeue(out Action? work))
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                // One failed update must not take the process down. A palette that
                // vanishes is worse than one that is briefly out of date.
                Log.Warn(LogCategory.Wm, $"deferred work failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The window manager raised a signal.
    /// </summary>
    /// <remarks>
    /// Signals that are not ours are ignored without complaint. The topic is a shared
    /// bus, so another client's signal reaching this process is ordinary rather than
    /// an error.
    /// </remarks>
    private static void OnSignal(SignalRaised signal)
    {
        if (!string.Equals(signal.Signal, s_config.OpenOnSignal, StringComparison.OrdinalIgnoreCase))
        {
            // Said out loud at debug, because "the key does nothing" is the whole
            // failure mode of a signal that does not match. Without this the trail
            // ends at the window manager, which correctly reports having published
            // something nobody acted on.
            Log.Debug(LogCategory.Ipc,
                $"signal \"{signal.Signal}\" is not mine (waiting for \"{s_config.OpenOnSignal}\")");

            return;
        }

        if (RunRequested(signal.Arguments)) return;

        PaletteMode mode = ModeFrom(signal.Arguments);
        Log.Debug(LogCategory.Wm, $"opening in {PaletteModel.NameOf(mode)} mode");

        Post(() => Open(mode));
    }

    /// <summary>
    /// Runs a named action outright, without showing anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bridge between the two halves of the configuration. An <c>action</c> is a
    /// sequence of commands with a name, and a keybinding is a sequence of commands
    /// with a key - so the same three lines had to be written twice by anybody who
    /// wanted both, and the two copies then drifted. There is no verb for this and
    /// deliberately never will be: the window manager has no idea what an action is,
    /// carries the name without reading it, and this is the process that knows.
    /// </para>
    /// <para>
    /// Nothing is shown on success. The point of putting an action on a key is that it
    /// happens; opening the palette to announce that it happened would put a window in
    /// front of the arrangement it just made.
    /// </para>
    /// <para>
    /// A name that matches nothing does open the palette, at the command list, for the
    /// same reason an unrecognised mode name does: the key has already been pressed and
    /// silence is indistinguishable from the palette being dead.
    /// </para>
    /// </remarks>
    private static bool RunRequested(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 ||
            !string.Equals(arguments[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (arguments.Count < 2 || arguments[1].Length == 0)
        {
            Log.Warn(LogCategory.Wm, "signal \"run\" did not say which action to run");
            Post(() => Open(PaletteMode.Commands));
            return true;
        }

        // Joined with a space, so a name written as several bare words works as well as
        // a quoted one. `signal "palette" "run" "Code layout"` and
        // `signal "palette" "run" Code layout` are the same request, and somebody who
        // forgot the quotes has not made a mistake worth punishing.
        string name = string.Join(' ', arguments.Skip(1));

        PaletteMacro? macro = s_config.Macros.FirstOrDefault(
            m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

        if (macro is null)
        {
            Log.Warn(LogCategory.Wm, $"no action is called \"{name}\"");
            Post(() => Open(PaletteMode.Commands));
            return true;
        }

        // Refused for the same reason its row carries no command: the sequence did not
        // parse, and sending it anyway would have the window manager reject it one line
        // at a time into a log nobody is reading.
        if (macro.Problem is { Length: > 0 } wrong)
        {
            Log.Warn(LogCategory.Wm, $"action \"{macro.Name}\" cannot run: {wrong}");
            Post(() => Open(PaletteMode.Commands));
            return true;
        }

        // A question cannot be asked without somewhere to ask it. The palette opens at
        // the command list with the name already typed, so the row is under the
        // selection and Enter is the next key either way.
        if (macro.Asks)
        {
            Log.Debug(LogCategory.Wm, $"action \"{macro.Name}\" asks first; opening the palette");
            Post(() => OpenAt(macro.Name));
            return true;
        }

        Log.Debug(LogCategory.Wm, $"running action \"{macro.Name}\"");
        Post(() => OnCommand(string.Join('\n', macro.Commands)));

        return true;
    }

    /// <summary>Opens the command list with something already typed into it.</summary>
    private static void OpenAt(string term)
    {
        Open(PaletteMode.Commands);
        s_palette?.Prefill(term);
    }

    /// <summary>
    /// The mode a signal asked for, defaulting to the window list.
    /// </summary>
    /// <remarks>
    /// The names come from the modes themselves, so a mode cannot exist without being
    /// addressable. An unrecognised name still opens the window list rather than
    /// refusing: the signal has already been raised and the key has already been
    /// pressed, and showing nothing would read as the palette being broken.
    /// </remarks>
    private static PaletteMode ModeFrom(IReadOnlyList<string> arguments) =>
        arguments.Count == 0
            ? PaletteMode.Windows
            : PaletteModel.ModeNamed(arguments[0]) ?? PaletteMode.Windows;

    /// <summary>
    /// Shows the palette, then fills it in.
    /// </summary>
    /// <remarks>
    /// In that order deliberately. The window is already created, so it can be on
    /// screen in the time it takes to show it; reading the window list is a round trip
    /// and lands while the user is still reaching for the first key. Waiting for the
    /// data first would make the whole thing feel as slow as the slowest part of it.
    /// </remarks>
    private static unsafe void Open(PaletteMode mode)
    {
        if (s_palette is not { } palette || s_connection is null) return;

        // Before the palette takes the keyboard, or the answer is the palette.
        if (!palette.IsOpen) s_foreground = (nint)PInvoke.GetForegroundWindow().Value;

        palette.SetEntries(s_sources.For(mode));

        if (!palette.Open(mode)) ScheduleForegroundRepair(palette);

        Refresh(palette);
    }

    /// <summary>
    /// Reads the world and hands it to the palette - to show, when it is open, and to
    /// have ready, when it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place a read happens, so opening and reacting to a change cannot drift
    /// apart - which they had, in the small way that matters: one of them primed the
    /// completions and the other did too, in slightly different words, and a third
    /// thing added later would have had to remember both.
    /// </para>
    /// <para>
    /// Safe on a closed palette, and called on one exactly once per connection: the
    /// lists are kept whatever the window is doing, and only the window itself is left
    /// alone. That is what gives the first open of a session something to show.
    /// </para>
    /// <para>
    /// The icons are fetched here, on this thread, before anything is posted. That is
    /// the whole of the icon performance story: they come from the window manager over
    /// the pipe, one request per window not seen before, and must never be waited for
    /// while a frame is being painted.
    /// </para>
    /// </remarks>
    private static void Refresh(PaletteWindow palette)
    {
        _ = Task.Run(async () =>
        {
            PaletteSources read = await s_connection!
                .ReadAsync(
                    s_config.ShowUnmanaged,
                    s_config.Macros,
                    s_foreground,
                    s_problems.Errors + s_problems.Warnings,
                    s_everyWindow)
                .ConfigureAwait(false);

            if (s_config.ShowIcons && read.WindowHandles is { Count: > 0 } handles)
                await WindowIcons.PrimeAsync(s_connection, handles).ConfigureAwait(false);

            Post(() =>
            {
                s_sources = read;
                s_completions = read.Completions;

                palette.SetStatus(read.Status);
                palette.SetContext(read.FocusedWorkspace, read.WorkspaceNames ?? []);

                // The mode is read here rather than captured, because the user may
                // have changed it while the query was in flight. Capturing it would
                // replace the list they are now looking at with the one they were
                // looking at when the request went out.
                if (palette.IsOpen) palette.SetEntries(read.For(palette.Mode));
            });
        });
    }

    /// <summary>
    /// Tries again for the keyboard, a few times over the opening grace, and leaves
    /// giving up to the loop's stranded check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Taking the foreground fails for reasons that pass: a menu still closing, a drag
    /// still finishing, an application still starting, and - the common one on a
    /// desktop that is itself still starting - a switch the system has accepted and
    /// not yet carried out. An attempt a few frames later usually succeeds, and doing
    /// it after a delay rather than immediately is the entire point: an immediate
    /// retry hits the same lock.
    /// </para>
    /// <para>
    /// Several attempts rather than one, spaced through the grace and one past it, so
    /// the loop is woken after the grace has run out and judges the palette then
    /// rather than at whatever quarter-second tick comes next. Each is a posted
    /// action; a palette that has meanwhile been reached, or closed, makes them no-ops.
    /// </para>
    /// </remarks>
    private static void ScheduleForegroundRepair(PaletteWindow palette)
    {
        _ = Task.Run(async () =>
        {
            TimeSpan elapsed = TimeSpan.Zero;

            foreach (TimeSpan at in PaletteInput.RepairSchedule)
            {
                await Task.Delay(at - elapsed).ConfigureAwait(false);
                elapsed = at;

                Post(() =>
                {
                    if (palette.IsOpen && palette.IsStranded) _ = palette.EnsureForeground();
                });
            }
        });
    }

    /// <summary>
    /// Something changed that the open list may be showing.
    /// </summary>
    /// <remarks>
    /// Only refreshed while the palette is on screen. A closed palette reads the world
    /// fresh when it opens, so keeping it current in the meantime would be work done
    /// for nobody - and window events on a busy desktop are frequent.
    /// </remarks>
    private static void MarkStale()
    {
        if (s_palette is not { IsOpen: true } palette || s_connection is null) return;

        Refresh(palette);
    }

    /// <summary>
    /// The window manager said it was leaving.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two answers, and the notice says which. After a plain <c>wm-exit</c> the
    /// palette stays: it used to stop with the window manager, which is the wrong half
    /// of the relationship - it reconnects when the daemon comes back, so shutting
    /// down meant a restarted window manager had no palette until somebody noticed and
    /// started one. Now it says so instead - the search box shows "offline" and the
    /// empty list explains itself - and carries on waiting.
    /// </para>
    /// <para>
    /// After <c>exit-all</c> it goes, the same way <c>WM_CLOSE</c> takes it out: the
    /// user is done with Shubbak, and a palette waiting for a window manager that is
    /// not coming back is exactly the stray process the tray's Exit used to leave.
    /// </para>
    /// </remarks>
    private static void OnWindowManagerLeaving(bool everything)
    {
        if (everything)
        {
            s_running = false;
            return;
        }

        MarkOffline();
    }

    /// <summary>Shows that the window manager cannot be reached, and waits.</summary>
    private static void MarkOffline()
    {
        s_sources = PaletteSources.Offline;
        s_completions = CompletionSources.None;

        s_palette?.SetStatus(WmStatus.Offline);
    }

    private static void ReloadConfig()
    {
        DalilConfigLoad load = LoadConfig();

        // Kept rather than applied, matching what the window manager does with the rest
        // of the same file. Defaults over a running palette is how a stray brace
        // mid-edit silently reset somebody's colours, their size, their prefixes and
        // their actions - a visible change with nothing to connect it to the keystroke
        // that caused it, which is a worse failure than staying as it was.
        if (!load.Usable)
        {
            Log.Error(LogCategory.Config,
                "the configuration has errors; keeping the palette as it is. " +
                "Run `shubbak check-config` to see them.");

            return;
        }

        s_config = load.Config;
        s_palette?.Reconfigure(s_config);

        Log.Info(LogCategory.Config, $"reloaded; listening for signal \"{s_config.OpenOnSignal}\"");
    }

    /// <summary>
    /// Reads the <c>dalil</c> section of the shared configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A missing section is not an error: the defaults are a working palette, and
    /// requiring configuration before a feature does anything is a good way to have it
    /// never tried.
    /// </para>
    /// <para>
    /// Everything wrong with the section goes to the log. Dalil is started detached and
    /// has no console, so the diagnostics it used to produce for nobody are now written
    /// where a detached process can be read from - and where <c>shubbak diagnose</c>
    /// will pick them up, which is the moment they matter most.
    /// </para>
    /// </remarks>
    private static DalilConfigLoad LoadConfig()
    {
        try
        {
            ConfigLocation location = ConfigPathResolver.Resolve(s_configPath);

            if (!location.Found || location.Path is not { } path)
                return new DalilConfigLoad(new DalilConfig(), [], Usable: true);

            DalilConfigLoad load = DalilConfigLoader.Validate(File.ReadAllText(path));

            s_diagnostics = load.Diagnostics;
            s_problems = ConfigDiagnostics.Report(load.Diagnostics, path, "the palette's settings");

            return load;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogCategory.Config, $"could not read the configuration: {ex.Message}");

            // Unreadable is not the same as wrong. Nothing is known about the file, so
            // nothing should be applied over a palette that is already running.
            return new DalilConfigLoad(new DalilConfig(), [], Usable: false);
        }
    }

    private static string? PathFrom(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] is "--config" or "-c")
                return args[i + 1];

        return null;
    }

    /// <summary>
    /// Opens a log file beside the window manager's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not optional, and its absence was found the hard way. Dalil is started
    /// detached from the window manager's <c>startup-command</c>, so it has no
    /// console and anything written to standard error goes nowhere at all. The first
    /// time the palette failed to appear there was simply nothing to read - the
    /// window manager's log showed the signal being published and then the trail
    /// stopped.
    /// </para>
    /// <para>
    /// Beside the window manager's log rather than into it. Two processes cannot
    /// share one file: the second to open it truncates the first, which is worse than
    /// not logging at all. Taj does the same thing for the same reason.
    /// </para>
    /// </remarks>
    private static void ConfigureLogging(string[] args)
    {
        string file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shubbak",
            "dalil.log");

        try
        {
            if (ConfigPathResolver.Resolve(s_configPath).Path is { } path && File.Exists(path))
            {
                ShubbakConfig shared = ConfigLoader.LoadFile(path).Config;
                Log.Level = shared.LogLevel;

                if (shared.LogFile is { Length: > 0 } configured)
                    file = Path.Combine(Path.GetDirectoryName(configured) ?? string.Empty, "dalil.log");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A palette that cannot read the config still has defaults to draw.
        }

        // The command line wins, so a one-off investigation needs no config edit.
        if (Value(args, "--log-level") is { } level && Log.TryParseLevel(level, out LogLevel parsed))
            Log.Level = parsed;

        // Off unless output genuinely leads somewhere. Dalil is resident and started
        // without a console, so this was formatting entries and discarding them.
        Log.ToConsole = ConsoleHost.HasOutput
            && !args.Contains("--quiet", StringComparer.Ordinal);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Log.OpenFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the log is not worth losing the palette over.
        }
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void PrintUsage() => Console.WriteLine("""
        Dalil - the command palette for Shubbak

        USAGE
          dalil [options]

        Dalil is resident. It creates its window hidden at startup and only shows it
        when signalled, because a palette has to open faster than the eye and starting
        a process does not. Launch it once, from startup-command or however you start
        things, and leave it running.

        OPTIONS
          --config <path>      Config file. Dalil reads the same file Shubbak uses and
                               resolves it the same way, including $XDG_CONFIG_HOME.
                               Run `shubbak config-path` to see which file is in
                               effect.
          --log-level <level>  trace | debug | info | warn | error | none
          --quiet              Do not write to the console.
          --version            Print the version and exit.
          --help               Show this message.

        NOTES
          Shubbak does not launch Dalil and does not know it exists. Bind a key to
          show it in your config, the same way you would any other command.
        """);
}
