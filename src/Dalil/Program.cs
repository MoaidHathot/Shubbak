using System.Collections.Concurrent;
using Dalil.Core;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;
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

    [STAThread]
    private static int Main(string[] args)
    {
        // Before any window is created: without it Windows reports virtualised
        // coordinates on scaled displays and the palette lands in the wrong place.
        // The cast is how the context handles are spelled - they are sentinel values,
        // not an enum CsWin32 can name.
        PInvoke.SetProcessDpiAwarenessContext((DPI_AWARENESS_CONTEXT)(nint)(-4));

        s_threadId = PInvoke.GetCurrentThreadId();
        s_configPath = PathFrom(args);

        ConfigureLogging(args);
        s_config = LoadConfig();

        s_palette = new PaletteWindow(s_config);

        if (!s_palette.Create())
        {
            Console.Error.WriteLine("dalil: could not create the palette window");
            return 1;
        }

        s_palette.CommandRequested += command => _ = s_connection!.SendAsync(command);

        // Typed commands become a row of their own, parsed by the same parser the
        // config file uses. Without this, every verb that takes an argument was a
        // dead end: the term outgrew the verb it named, matched nothing, and Enter
        // had no row to act on.
        s_palette.Augment(term => CommandComposer.Compose(term, s_completions));

        // Every route into a mode refills the list: Tab, typing a prefix, deleting
        // one, or choosing a mode from the help list. Without this, typing ">" left
        // the box saying "commands" while the rows underneath were still windows.
        s_palette.ModeChanged += mode => s_palette!.SetEntries(s_sources.For(mode));

        // Asked on a background thread and shown on the palette's own, because the
        // window manager has to assemble the report and the message loop cannot wait
        // for it without freezing the very window that is meant to display it.
        s_palette.ExplainRequested += (handle, title) => _ = Task.Run(async () =>
        {
            IReadOnlyList<string> report = await s_connection!.InspectAsync(handle).ConfigureAwait(false);

            Post(() => s_palette?.ShowReport(title, report));
        });

        PaletteWindow.RequestShutdown += () => s_running = false;

        s_connection = new WmConnection();
        s_connection.Signalled += OnSignal;
        s_connection.Stale += () => Post(MarkStale);
        s_connection.Reloaded += () => Post(ReloadConfig);
        s_connection.ShuttingDown += () => Post(() => s_running = false);
        s_connection.Start();

        Log.Info(LogCategory.Wm,
            $"dalil is listening for signal \"{s_config.OpenOnSignal}\"");

        RunMessageLoop();

        s_palette.Dispose();
        s_connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

        return 0;
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

            if (!s_running) break;

            PInvoke.MsgWaitForMultipleObjectsEx(
                [],
                250,
                QUEUE_STATUS_FLAGS.QS_ALLINPUT,
                MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
        }
    }

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

        PaletteMode mode = ModeFrom(signal.Arguments);
        Log.Debug(LogCategory.Wm, $"opening in {PaletteModel.NameOf(mode)} mode");

        Post(() => Open(mode));
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
    private static void Open(PaletteMode mode)
    {
        if (s_palette is not { } palette || s_connection is not { } connection) return;

        palette.SetEntries(s_sources.For(mode));

        if (!palette.Open(mode)) ScheduleForegroundRepair(palette);

        _ = Task.Run(async () =>
        {
            PaletteSources read = await connection.ReadAsync(s_config.ShowUnmanaged).ConfigureAwait(false);

            Post(() =>
            {
                s_sources = read;
                s_completions = read.Completions;
                palette.SetStatus(read.Status);

                // Same reasoning as MarkStale: the user may have pressed Tab while
                // this was in flight.
                if (palette.IsOpen) palette.SetEntries(read.For(palette.Mode));
            });
        });
    }

    /// <summary>
    /// Tries once more, shortly, to get the keyboard - and gives up rather than
    /// leaving a window nobody can reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Taking the foreground fails for reasons that pass: a menu still closing, a drag
    /// still finishing, an application still starting. A second attempt a few frames
    /// later usually succeeds, and doing it after a delay rather than immediately is
    /// the entire point - an immediate retry hits the same lock.
    /// </para>
    /// <para>
    /// If it still fails the palette is hidden. That is the unobvious half: a window
    /// that never activated will never be told it has been deactivated, so
    /// close-on-blur cannot dismiss it, Escape never reaches it, and it sits on top of
    /// everything looking perfectly normal and answering nothing. Vanishing is a
    /// worse outcome than working and a much better one than that.
    /// </para>
    /// </remarks>
    private static void ScheduleForegroundRepair(PaletteWindow palette)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(140)).ConfigureAwait(false);

            Post(() =>
            {
                if (!palette.IsOpen || palette.EnsureForeground()) return;

                Log.Warn(LogCategory.Wm, "giving up on the keyboard; putting the palette away");
                palette.Close();
            });
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
        if (s_palette is not { IsOpen: true } palette || s_connection is not { } connection) return;

        _ = Task.Run(async () =>
        {
            PaletteSources read = await connection.ReadAsync(s_config.ShowUnmanaged).ConfigureAwait(false);

            Post(() =>
            {
                s_sources = read;
                s_completions = read.Completions;
                palette.SetStatus(read.Status);

                // The mode is read here rather than captured, because the user may
                // have changed it while the query was in flight. Capturing it would
                // replace the list they are now looking at with the one they were
                // looking at when the event arrived.
                if (palette.IsOpen) palette.SetEntries(read.For(palette.Mode));
            });
        });
    }

    private static void ReloadConfig()
    {
        s_config = LoadConfig();
        s_palette?.Reconfigure(s_config);

        Log.Info(LogCategory.Config, $"reloaded; listening for signal \"{s_config.OpenOnSignal}\"");
    }

    /// <summary>
    /// Reads the <c>dalil</c> section of the shared configuration.
    /// </summary>
    /// <remarks>
    /// A missing section is not an error: the defaults are a working palette, and
    /// requiring configuration before a feature does anything is a good way to have it
    /// never tried.
    /// </remarks>
    private static DalilConfig LoadConfig()
    {
        try
        {
            ConfigLocation location = ConfigPathResolver.Resolve(s_configPath);

            if (!location.Found || location.Path is not { } path) return new DalilConfig();

            return DalilConfigLoader.LoadFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogCategory.Config, $"could not read the configuration: {ex.Message}");
            return new DalilConfig();
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
}
