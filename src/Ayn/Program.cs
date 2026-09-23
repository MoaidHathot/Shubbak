using Ayn.Core;
using Shubbak.Companion;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Ayn;

/// <summary>
/// Ayn: watches the camera and the microphone, and tells the window manager.
/// </summary>
/// <remarks>
/// <para>
/// Started from the user's <c>startup-command</c>, like the bar and the palette.
/// Shubbak does not launch it and does not know it exists: the file declares a
/// context called <c>camera</c> with no conditions, and this is whatever happens to
/// set it.
/// </para>
/// <para>
/// One thread, no message loop. It sleeps on a handful of events - the registry
/// changed, the window manager left, the file was reloaded, somebody asked it to stop
/// - and wakes to do a few hundred microseconds of work. Between wakes it holds no
/// timer, unless a change is waiting out its settle time, in which case it sleeps
/// exactly that long.
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly CompanionIdentity Identity = new(
        Name: "ayn",
        Noun: "a watcher",
        Usage: UsageText,
        HasWindow: false);

    private static string? s_configPath;

    private static int Main(string[] args) =>
        CompanionBootstrap.Run(args, Identity, Start, preflight: Preflight);

    /// <summary>
    /// <c>--report</c>: what Windows says right now, and nothing else. For a person
    /// wondering why a meeting was or was not noticed: this is the same reading the
    /// watcher acts on.
    /// </summary>
    /// <remarks>
    /// Before the single-instance lock, because it runs beside a watcher that is
    /// already running - and with no log file, because opening the file would truncate
    /// the one that watcher is writing to.
    /// </remarks>
    private static int? Preflight(CompanionContext context)
    {
        if (!Arguments.Has(context.Args, "--report")) return null;

        s_configPath = context.ConfigPath;
        CompanionBootstrap.ConfigureLogging(context, Identity, toFile: false);
        ConsoleHost.Ensure();
        return Report();
    }

    private static int Start(CompanionContext context)
    {
        s_configPath = context.ConfigPath;

        AynConfig config = LoadConfig();

        if (!config.WatchesAnything)
        {
            Log.Info(LogCategory.Wm, "every fact is turned off in the ayn section; nothing to watch");
            ConsoleHost.Ensure();
            Console.Error.WriteLine("ayn: the ayn section turns off every fact; nothing to watch.");
            return 0;
        }

        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, IpcProtocol.StopEventNameFor("ayn"));

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };

        // Each source is opened only if a fact needs it: a file that says
        // `microphone { muted #false }` never touches Core Audio until a signal asks.
        using var store = new RegistryConsentStore();

        if (config.NeedsConsentStore && store.Count == 0)
        {
            Log.Error(LogCategory.Wm, "the consent store could not be opened under either hive; there is nothing to watch");
            ConsoleHost.Ensure();
            Console.Error.WriteLine("ayn: the consent store could not be opened; is this Windows 10 1903 or later?");
            return 1;
        }

        using var endpoint = new AudioEndpoint();

        if (config.NeedsAudioEndpoint && !endpoint.Open())
            Log.Warn(LogCategory.Wm, "Core Audio is not available; the microphone's mute will not be reported");

        var connection = new WmConnection();
        connection.Start();

        Log.Info(LogCategory.Wm,
            $"ayn is watching {Describe(config)}; " +
            $"a change of use is believed after {config.EffectiveSettle.TotalMilliseconds:F0} ms, a mute at once");

        try
        {
            Run(config, store, endpoint, connection, stop);
        }
        finally
        {
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Log.Info(LogCategory.Wm, "ayn stopped");
        }

        return 0;
    }

    /// <summary>
    /// The loop: sleep until something happens, tell the provider, send what is due.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything that can wake it is a handle: a stop request, the window manager
    /// leaving - alone, or taking everything with it - a reload, a signal, the
    /// microphone's mute or default device changing, and one event per watched
    /// registry key. Between wakes it waits with no timeout, unless a change is
    /// waiting out its settle time or the window manager is unreachable with
    /// something to say, in which case it waits exactly that long. No thread of ours
    /// spins, and no timer ticks.
    /// </para>
    /// <para>
    /// The registry is re-armed before it is read, or a change landing between the two
    /// would be missed until the next one. The provider is asked what is due after
    /// every wake, including a wake for nothing - a timeout - since a timeout is
    /// precisely a settle time expiring.
    /// </para>
    /// </remarks>
    private static void Run(
        AynConfig config, RegistryConsentStore store, AudioEndpoint endpoint, WmConnection connection, WaitHandle stop)
    {
        var provider = new Provider(config);

        const int StopIndex = 0;
        const int DismissedIndex = 1;
        const int LostIndex = 2;
        const int ReloadedIndex = 3;
        const int SignalledIndex = 4;
        const int AudioIndex = 5;
        const int FirstRegistryIndex = 6;

        // Dismissed sits before Lost on purpose. WaitAny answers with the lowest
        // index that is set, and exit-all raises both within a moment of each other -
        // the notice, then the pipe closing behind it - so the order decides whether
        // the watcher leaves or reconnects to nothing.
        WaitHandle[] handles =
            [stop, connection.Dismissed, connection.Lost, connection.Reloaded, connection.Signalled, AudioEndpoint.Changed, .. store.Changed];

        store.Arm();
        provider.Observe(Reading.From(store, endpoint.IsMuted()), Environment.TickCount64);

        // While the window manager cannot be reached and there is something to tell it,
        // try again about once a second; otherwise sleep until the registry speaks.
        TimeSpan retry = TimeSpan.FromSeconds(1);
        bool retrying = false;

        while (true)
        {
            long now = Environment.TickCount64;

            retrying = Flush(provider, connection, now);

            TimeSpan wait = Provider.NextWait(retrying, provider.Pending(now), retry);

            int woke = WaitHandle.WaitAny(handles, wait);
            now = Environment.TickCount64;

            switch (woke)
            {
                case StopIndex:
                    return;

                case DismissedIndex:
                    // exit-all: the user is done with Shubbak, not restarting it. The
                    // leases go with the connection, which Main closes on the way out.
                    return;

                case LostIndex:
                    // Every lease died with the connection that held it. Nothing is
                    // asserted any more; the next flush holds again whatever is still
                    // in use, on the window manager that comes back.
                    Log.Info(LogCategory.Ipc, "the window manager went away; holding nothing until it is back");
                    connection.Drop();
                    provider.Forget();
                    break;

                case ReloadedIndex:
                    Reconfigure(provider, connection, endpoint);
                    provider.Observe(Reading.From(store, endpoint.IsMuted()), now);
                    break;

                case SignalledIndex:
                    // Somebody asked for the mute to change. Done, not observed: the
                    // endpoint answers with its own change notification, which is the
                    // next wake, and the provider learns the new state from that - so
                    // a request and a change made in the Sound settings take the same
                    // path and there is one copy of the truth.
                    while (connection.Requests.TryDequeue(out SignalRequest? request))
                        Act(request, endpoint);
                    break;

                case AudioIndex:
                    if (AudioEndpoint.TakeDefaultDeviceChanged())
                    {
                        Log.Info(LogCategory.Wm, "the default microphone changed; asking the new one");
                        endpoint.Resolve();
                    }

                    provider.Observe(Reading.From(store, endpoint.IsMuted()), now);
                    break;

                case WaitHandle.WaitTimeout:
                    break;

                default:
                    store.Arm(woke - FirstRegistryIndex);
                    provider.Observe(Reading.From(store, endpoint.IsMuted()), now);
                    break;
            }
        }
    }

    /// <summary>Does what a signal asked: mutes, unmutes or flips the microphone.</summary>
    /// <remarks>
    /// Core Audio is opened here if the file never asked for the mute to be reported.
    /// The fact and the action are two different things: <c>microphone { muted #false }</c>
    /// says the bar does not want a muted pill, not that the bar's mute button should
    /// stop working, and a person who pressed it has asked for the one thing that
    /// justifies touching Core Audio in a file that said not to.
    /// </remarks>
    private static void Act(SignalRequest request, AudioEndpoint endpoint)
    {
        if (!endpoint.IsOpen && !endpoint.Open())
        {
            Log.Warn(LogCategory.Wm, $"signal {request.Subject} {request.Verb}: Core Audio is not available");
            return;
        }

        if (!endpoint.HasDevice)
        {
            Log.Warn(LogCategory.Wm, $"signal {request.Subject} {request.Verb}: there is no microphone to act on");
            return;
        }

        bool? muted = endpoint.IsMuted();

        bool wanted = request.Verb switch
        {
            "mute" => true,
            "unmute" => false,
            _ => muted != true,
        };

        if (muted == wanted)
        {
            Log.Debug(LogCategory.Wm, $"signal {request.Subject} {request.Verb}: already {(wanted ? "muted" : "unmuted")}");
            return;
        }

        if (endpoint.SetMuted(wanted))
            Log.Info(LogCategory.Wm, $"signal {request.Subject} {request.Verb}: microphone {(wanted ? "muted" : "unmuted")}");
    }

    /// <summary>Sends everything due. True if something could not be sent and should be retried.</summary>
    private static bool Flush(Provider provider, WmConnection connection, long now)
    {
        foreach (ProviderAction action in provider.Due(now))
        {
            SendOutcome outcome = connection.Send(action.Command);
            provider.Sent(action, outcome);

            switch (outcome)
            {
                case SendOutcome.Accepted:
                    Log.Info(LogCategory.Wm, $"{action.Because}: {action.Command}");
                    break;

                case SendOutcome.Refused:
                    // Logged by the connection, once. The provider does not ask again
                    // until a reload or a reconnect, which are the two things that can
                    // change the answer.
                    break;

                case SendOutcome.Unreachable:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The file was reloaded: re-read the section, release what is no longer wanted
    /// under its old name, and open the sources the new settings need.
    /// </summary>
    /// <remarks>
    /// Core Audio is opened if the mute is newly asked for, and left open if it is not:
    /// the callbacks cost nothing while idle, and the mute <em>signal</em> still needs
    /// it whatever the file says about the mute <em>fact</em>. The reading that follows
    /// in the caller is what turns a newly opened endpoint into a reported fact.
    /// </remarks>
    private static void Reconfigure(Provider provider, WmConnection connection, AudioEndpoint endpoint)
    {
        AynConfig config = LoadConfig();

        foreach (ProviderAction release in provider.Reconfigure(config))
        {
            SendOutcome outcome = connection.Send(release.Command);
            provider.Sent(release, outcome);

            if (outcome == SendOutcome.Accepted)
                Log.Info(LogCategory.Wm, $"{release.Because}: {release.Command}");
        }

        if (config.NeedsAudioEndpoint && !endpoint.IsOpen && !endpoint.Open())
            Log.Warn(LogCategory.Wm, "Core Audio is not available; the microphone's mute will not be reported");

        // The holds under any new names follow from the next flush, which the caller
        // runs at the top of the loop.
        Log.Info(LogCategory.Config, $"reloaded; watching {Describe(config)}");
    }

    private static AynConfig LoadConfig()
    {
        try
        {
            ConfigLocation location = ConfigPathResolver.Resolve(s_configPath);

            if (!location.Found || location.Path is not { } path) return new AynConfig();

            AynConfigLoad load = AynConfigLoader.Validate(File.ReadAllText(path));
            ConfigDiagnostics.Report(load.Diagnostics, path, "the watcher's settings");

            // The parser's complaint is the window manager's to report; this is the one
            // line the watcher owes for running on defaults while the file is broken.
            if (load.SyntaxErrors)
                Log.Warn(LogCategory.Config, $"{path} does not parse; the watcher runs on its defaults until it does (shubbak check-config says what is wrong)");

            return load.Config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogCategory.Config, $"could not read the configuration: {ex.Message}; using the defaults");
            return new AynConfig();
        }
    }

    private static int Report()
    {
        using var store = new RegistryConsentStore();

        if (store.Count == 0)
        {
            Console.Error.WriteLine("ayn: the consent store could not be opened under either hive.");
            return 1;
        }

        foreach (DeviceKind device in new[] { DeviceKind.Camera, DeviceKind.Microphone })
        {
            IReadOnlyList<ConsentEntry> entries = store.Read(device);
            string[] inUse = [.. entries.Where(e => e.InUse).Select(e => e.App).Distinct(StringComparer.OrdinalIgnoreCase)];

            Console.WriteLine(inUse.Length > 0
                ? $"{device.ToString().ToLowerInvariant()}: in use by {string.Join(", ", inUse)}"
                : $"{device.ToString().ToLowerInvariant()}: not in use ({entries.Count} program(s) have used it)");

            // The two times behind each verdict, for the one question a report is
            // usually asked: why does the watcher think this program still has the
            // device? A start with no stop from a program that crashed weeks ago reads
            // as "in use" here exactly as it does in the Settings app, and the date says
            // so.
            foreach (ConsentEntry entry in entries.OrderByDescending(e => e.Started))
            {
                Console.WriteLine(
                    $"  {(entry.InUse ? "open  " : "closed")}  {entry.App,-40}  " +
                    $"opened {Describe(entry.Started)}  closed {Describe(entry.Stopped)}");
            }
        }

        using var endpoint = new AudioEndpoint();

        if (!endpoint.Open())
        {
            Console.WriteLine("microphone mute: Core Audio is not available");
            return 0;
        }

        Console.WriteLine(endpoint.IsMuted() switch
        {
            true => "microphone mute: muted",
            false => "microphone mute: not muted",
            null => "microphone mute: no microphone",
        });

        return 0;
    }

    internal static string Describe(AynConfig config)
    {
        List<string> parts = [];

        foreach (Fact fact in FactNames.All)
        {
            if (config.ContextFor(fact) is { } context)
                parts.Add(string.Equals(context, fact.Wire(), StringComparison.Ordinal) ? context : $"{fact.Wire()} as \"{context}\"");
        }

        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// <summary>A FILETIME from the consent store as a local date and time, or "never".</summary>
    internal static string Describe(long fileTime)
    {
        if (fileTime <= 0) return "never";

        try
        {
            return DateTime.FromFileTime(fileTime).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return $"0x{fileTime:X}";
        }
    }

    private const string UsageText = """
        Ayn - the watcher for Shubbak

        Ayn watches the camera and the microphone and holds a context on the
        window manager for each fact while it is true: camera-in-use,
        microphone-in-use, microphone-muted.

        It also answers `signal "ayn" "microphone" "mute" | "unmute" | "toggle-mute"`
        from a keybinding, the bar or the palette, by flipping the system mute.

        USAGE
          ayn [options]

        OPTIONS
          --config <path>      Config file. Ayn reads the `ayn` section of the same
                               file Shubbak uses and resolves it the same way,
                               including $XDG_CONFIG_HOME.
          --log-level <level>  trace | debug | info | warn | error | none
          --log-file [path]    Write the log to a file of your choosing.
          --quiet              Do not write to the console.
          --report             Print what Windows says about each device now, and exit.
          --version            Print the version and exit.
          --help               Show this message.

        `shubbak ayn-exit` stops a running watcher.
        """;
}