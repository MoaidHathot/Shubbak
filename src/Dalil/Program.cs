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

        Log.Level = LogLevel.Information;
        s_config = LoadConfig();

        s_palette = new PaletteWindow(s_config);

        if (!s_palette.Create())
        {
            Console.Error.WriteLine("dalil: could not create the palette window");
            return 1;
        }

        s_palette.CommandRequested += command => _ = s_connection!.SendAsync(command);
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
            return;

        PaletteMode mode = ModeFrom(signal.Arguments);
        Post(() => Open(mode));
    }

    private static PaletteMode ModeFrom(IReadOnlyList<string> arguments) =>
        arguments.Count == 0
            ? PaletteMode.Windows
            : arguments[0].ToLowerInvariant() switch
            {
                "commands" or "command" => PaletteMode.Commands,
                "workspaces" or "workspace" => PaletteMode.Workspaces,
                "layouts" or "layout" => PaletteMode.Layouts,
                _ => PaletteMode.Windows,
            };

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
        palette.Open(mode);

        _ = Task.Run(async () =>
        {
            PaletteSources read = await connection.ReadAsync(s_config.ShowUnmanaged).ConfigureAwait(false);

            Post(() =>
            {
                s_sources = read;

                // Same reasoning as MarkStale: the user may have pressed Tab while
                // this was in flight.
                if (palette.IsOpen) palette.SetEntries(read.For(palette.Mode));
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
}
