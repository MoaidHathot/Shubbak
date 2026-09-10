using Rasid.Core;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Rasid;

/// <summary>
/// Rasid: watches the camera and the microphone, and tells the window manager.
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
    private static string? s_configPath;

    private static int Main(string[] args)
    {
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

        s_configPath = Value(args, "--config") ?? Value(args, "-c");

        ConfigureLogging(args);

        // What Windows says right now, and nothing else. For a person wondering why a
        // meeting was or was not noticed: this is the same reading the watcher acts on.
        if (args.Contains("--report", StringComparer.Ordinal))
        {
            ConsoleHost.Ensure();
            return Report();
        }

        // One watcher per account. Two would hold the same pins twice, which is
        // harmless, and both write the log, which is not. Nothing strange has to
        // happen to end up with two: the watcher survives the window manager
        // restarting, and the restarted window manager runs its startup commands.
        using SingleInstanceLock instance = SingleInstanceLock.Claim(
            IpcProtocol.InstanceMutexNameFor("rasid"));

        if (!instance.Held && instance.Certain)
        {
            ConsoleHost.Ensure();
            Console.Error.WriteLine("rasid: a watcher is already running.");
            Console.Error.WriteLine("hint: `shubbak rasid-exit` stops it.");

            Log.Info(LogCategory.Wm, "another watcher is already running; leaving it to it");
            return 1;
        }

        RasidConfig config = LoadConfig();

        if (!config.WatchesAnything)
        {
            Log.Info(LogCategory.Wm, "both the camera and the microphone are turned off in the rasid section; nothing to watch");
            ConsoleHost.Ensure();
            Console.Error.WriteLine("rasid: the rasid section turns off both the camera and the microphone; nothing to watch.");
            return 0;
        }

        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, IpcProtocol.StopEventNameFor("rasid"));

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };

        using var store = new RegistryConsentStore();

        if (store.Count == 0)
        {
            Log.Error(LogCategory.Wm, "the consent store could not be opened under either hive; there is nothing to watch");
            ConsoleHost.Ensure();
            Console.Error.WriteLine("rasid: the consent store could not be opened; is this Windows 10 1903 or later?");
            return 1;
        }

        var connection = new WmConnection();
        connection.Start();

        Log.Info(LogCategory.Wm,
            $"rasid is watching {Describe(config)} across {store.Count} key(s); " +
            $"a change is believed after {config.EffectiveSettle.TotalMilliseconds:F0} ms");

        try
        {
            Run(config, store, connection, stop);
        }
        finally
        {
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Log.Info(LogCategory.Wm, "rasid stopped");
            Log.CloseFile();
        }

        return 0;
    }

    /// <summary>
    /// The loop: sleep until something happens, tell the provider, send what is due.
    /// </summary>
    /// <remarks>
    /// The registry is re-armed before it is read, or a change landing between the two
    /// would be missed until the next one. The provider is asked what is due after
    /// every wake, including a wake for nothing - a timeout - since a timeout is
    /// precisely a settle time expiring.
    /// </remarks>
    private static void Run(RasidConfig config, RegistryConsentStore store, WmConnection connection, WaitHandle stop)
    {
        var provider = new Provider(config);

        const int StopIndex = 0;
        const int LostIndex = 1;
        const int ReloadedIndex = 2;
        const int FirstRegistryIndex = 3;

        WaitHandle[] handles = [stop, connection.Lost, connection.Reloaded, .. store.Changed];

        store.Arm();
        provider.Observe(Reading.From(store), Environment.TickCount64);

        // While the window manager cannot be reached and there is something to tell it,
        // try again about once a second; otherwise sleep until the registry speaks.
        static TimeSpan RetryWait() => TimeSpan.FromSeconds(1);
        bool retrying = false;

        while (true)
        {
            long now = Environment.TickCount64;

            retrying = Flush(provider, connection, now);

            TimeSpan? pending = provider.Pending(now);
            TimeSpan wait = pending ?? Timeout.InfiniteTimeSpan;

            if (retrying && (pending is null || RetryWait() < pending))
                wait = RetryWait();

            int woke = WaitHandle.WaitAny(handles, wait);
            now = Environment.TickCount64;

            switch (woke)
            {
                case StopIndex:
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
                    Reconfigure(provider, connection);
                    break;

                case WaitHandle.WaitTimeout:
                    break;

                default:
                    store.Arm(woke - FirstRegistryIndex);
                    provider.Observe(Reading.From(store), now);
                    break;
            }
        }
    }

    /// <summary>Sends everything due. True if something could not be sent and should be retried.</summary>
    private static bool Flush(Provider provider, WmConnection connection, long now)
    {
        foreach (ProviderAction action in provider.Due(now))
        {
            SendOutcome outcome = connection.Send(action.Command);

            switch (outcome)
            {
                case SendOutcome.Accepted:
                    Log.Info(LogCategory.Wm, $"{action.Because}: {action.Command}");
                    break;

                case SendOutcome.Refused:
                    // Logged by the connection, once. Treated as sent: the context is
                    // not declared, and asking again on every change would say the
                    // same thing a hundred times. A reload or a reconnect asks again.
                    break;

                case SendOutcome.Unreachable:
                    provider.Forget();
                    return true;
            }
        }

        return false;
    }

    private static void Reconfigure(Provider provider, WmConnection connection)
    {
        RasidConfig config = LoadConfig();

        foreach (ProviderAction release in provider.Reconfigure(config))
        {
            if (connection.Send(release.Command) == SendOutcome.Accepted)
                Log.Info(LogCategory.Wm, $"{release.Because}: {release.Command}");
        }

        // The holds under any new names follow from the next flush, which the caller
        // runs at the top of the loop.
        Log.Info(LogCategory.Config, $"reloaded; watching {Describe(config)}");
    }

    private static RasidConfig LoadConfig()
    {
        try
        {
            ConfigLocation location = ConfigPathResolver.Resolve(s_configPath);

            if (!location.Found || location.Path is not { } path) return new RasidConfig();

            RasidConfigLoad load = RasidConfigLoader.Validate(File.ReadAllText(path));
            ConfigDiagnostics.Report(load.Diagnostics, path, "the watcher's settings");

            return load.Config;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogCategory.Config, $"could not read the configuration: {ex.Message}; using the defaults");
            return new RasidConfig();
        }
    }

    private static int Report()
    {
        using var store = new RegistryConsentStore();

        if (store.Count == 0)
        {
            Console.Error.WriteLine("rasid: the consent store could not be opened under either hive.");
            return 1;
        }

        foreach (DeviceKind device in new[] { DeviceKind.Camera, DeviceKind.Microphone })
        {
            IReadOnlyList<ConsentEntry> entries = store.Read(device);
            string[] inUse = [.. entries.Where(e => e.InUse).Select(e => e.App).Distinct(StringComparer.OrdinalIgnoreCase)];

            Console.WriteLine(inUse.Length > 0
                ? $"{device.ToString().ToLowerInvariant()}: in use by {string.Join(", ", inUse)}"
                : $"{device.ToString().ToLowerInvariant()}: not in use ({entries.Count} program(s) have used it)");
        }

        return 0;
    }

    private static string Describe(RasidConfig config)
    {
        List<string> parts = [];

        if (config.Camera is { } camera) parts.Add($"the camera as \"{camera}\"");
        if (config.Microphone is { } microphone) parts.Add($"the microphone as \"{microphone}\"");

        return parts.Count == 0 ? "nothing" : string.Join(" and ", parts);
    }

    private static void ConfigureLogging(string[] args)
    {
        string file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shubbak",
            "rasid.log");

        try
        {
            if (ConfigPathResolver.Resolve(s_configPath).Path is { } path && File.Exists(path))
            {
                ShubbakConfig shared = ConfigLoader.LoadFile(path).Config;
                Log.Level = shared.LogLevel;

                if (shared.LogFile is { Length: > 0 } configured)
                    file = Path.Combine(Path.GetDirectoryName(configured) ?? string.Empty, "rasid.log");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A watcher that cannot read the config still has defaults to run on.
        }

        if (Value(args, "--log-level") is { } level && Log.TryParseLevel(level, out LogLevel parsed))
            Log.Level = parsed;

        Log.ToConsole = ConsoleHost.HasOutput && !args.Contains("--quiet", StringComparer.Ordinal);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Log.OpenFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the log is not worth losing the watcher over.
        }
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(ShubbakVersion.Banner);
        Console.WriteLine();
        Console.WriteLine("Rasid watches the camera and the microphone and holds a context on the");
        Console.WriteLine("window manager while a program has either open.");
        Console.WriteLine();
        Console.WriteLine("usage: rasid [options]");
        Console.WriteLine();
        Console.WriteLine("options:");
        Console.WriteLine("  --config <path>     the shubbak.kdl to read the rasid section from");
        Console.WriteLine("  --log-level <level> trace, debug, info, warn, error");
        Console.WriteLine("  --quiet             do not echo the log to the console");
        Console.WriteLine("  --report            print what Windows says is using each device, and exit");
        Console.WriteLine("  --version           print the version");
        Console.WriteLine("  --help              this");
        Console.WriteLine();
        Console.WriteLine("`shubbak rasid-exit` stops a running watcher.");
    }
}
