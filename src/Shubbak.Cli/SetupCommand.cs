using System.Diagnostics;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.Cli;

/// <summary>
/// <c>shubbak setup</c>: from a fresh install to a running desktop in one command.
/// </summary>
/// <remarks>
/// <para>
/// The three things a new install needs - a config, a registration to start at
/// logon, and to actually be started - were three commands across two executables,
/// and the notes printed after <c>winget install</c> listed all three. Each is still
/// available on its own, for the person who wants exactly one of them; this is for
/// the person who wants a window manager.
/// </para>
/// <para>
/// Every step is idempotent, so running it twice is harmless and running it after a
/// half-finished first attempt finishes the job: a config that exists is kept, a
/// registration that exists is refreshed to point at this copy, and a window manager
/// that is running is left running.
/// </para>
/// <para>
/// The installer's last page runs this too - hidden, with <c>--from-installer</c> -
/// when its checkbox is left ticked, so the two ways of installing end in the same
/// place by the same code. See <see cref="Start"/> for why it is this program that
/// starts the window manager and not the installer directly.
/// </para>
/// </remarks>
internal static class SetupCommand
{
    public static int Run(string[] args)
    {
        bool noAutostart = args.Contains("--no-autostart", StringComparer.Ordinal);
        bool noStart = args.Contains("--no-start", StringComparer.Ordinal);
        bool fromInstaller = args.Contains("--from-installer", StringComparer.Ordinal);

        int result = RunSteps(args, noAutostart, noStart, out string? failure);

        // Run by the installer, this process has no terminal and its output is in a
        // log nobody opens unless told to; a failure is said where a person is.
        if (result != 0 && fromInstaller && failure is not null)
        {
            UserNotice.ShowError(
                "Shubbak",
                $"Shubbak is installed, but could not be set up:{Environment.NewLine}{Environment.NewLine}{failure}{Environment.NewLine}{Environment.NewLine}" +
                "Start it from the Start Menu, or open a terminal and run:  shubbak setup");
        }

        return result;
    }

    private static int RunSteps(string[] args, bool noAutostart, bool noStart, out string? failure)
    {
        failure = null;

        // 1. The config.
        ConfigLocation location = ConfigPathResolver.Resolve(Value(args, "--config"));
        string configPath = location.Path ?? ConfigPathResolver.DefaultWriteLocation();

        if (location.Found && File.Exists(configPath))
        {
            Console.WriteLine($"config      {configPath} (kept)");
        }
        else
        {
            try
            {
                StarterConfig.WriteIfMissing(configPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failure = $"could not write {configPath}: {ex.Message}";
                Console.Error.WriteLine($"shubbak: {failure}");
                return 1;
            }

            Console.WriteLine($"config      {configPath} (written)");
        }

        // 2. The daemon, which both remaining steps need.
        string? daemon = Autostart.FindDaemon();

        if (daemon is null)
        {
            failure = "could not find shubbak-wm.exe beside this program or on PATH.";
            Console.Error.WriteLine($"shubbak: {failure}");
            Console.Error.WriteLine("hint: if you have just installed, open a new terminal - this one has the old PATH.");
            return 1;
        }

        // 3. Start at logon. The explicit --config, if any, goes into the Run key so
        //    the file chosen here is the one chosen at every logon too.
        if (noAutostart)
        {
            Console.WriteLine("autostart   skipped (--no-autostart)");
        }
        else
        {
            string[] extra = Value(args, "--config") is { } explicit_ ? ["--config", explicit_] : [];

            if (Autostart.Register(daemon, extra) is null)
            {
                failure = "could not register it to start at logon; `shubbak autostart enable` says why.";
                return 1;
            }

            Console.WriteLine("autostart   enabled");
        }

        // 4. Start it now.
        if (noStart)
        {
            Console.WriteLine("shubbak-wm  not started (--no-start)");
        }
        else if (IpcClient.IsServerRunning())
        {
            Console.WriteLine("shubbak-wm  already running");
        }
        else if (Start(daemon, Value(args, "--config")) is { } problem)
        {
            failure = problem;
            Console.Error.WriteLine($"shubbak: {problem}");
            return 1;
        }
        else
        {
            Console.WriteLine("shubbak-wm  started");
        }

        Console.WriteLine();
        Console.WriteLine("Your windows are being tiled, with a bar along the top of each monitor and a");
        Console.WriteLine("Shubbak icon in the tray. Everything is on Alt:");
        Console.WriteLine();
        Console.WriteLine("  alt+shift+space    the palette - every window, every command, and ? for every key");
        Console.WriteLine("  alt+h j k l        focus;  alt+shift+h j k l  move the window");
        Console.WriteLine("  alt+1..9 0         go to a workspace;  alt+shift+N  send the window there");
        Console.WriteLine("  alt+w              cycle the layout;  alt+shift+m  float or tile");
        Console.WriteLine("  alt+shift+p        pause Shubbak's keys;  alt+shift+e  exit everything");
        Console.WriteLine();
        Console.WriteLine($"The keys, and everything else, are in {configPath}.");
        Console.WriteLine("Edit it, then `shubbak check-config` to validate and alt+shift+r to apply.");
        Console.WriteLine("`shubbak doctor` checks the install if something seems off.");
        return 0;
    }

    /// <summary>
    /// Starts the window manager and waits for it to answer.
    /// </summary>
    /// <returns>Null when it is running, otherwise what went wrong, in a sentence.</returns>
    /// <remarks>
    /// <para>
    /// Through the shell, not <c>CreateProcess</c>. The window manager the installer
    /// puts under Program Files asks for <c>uiAccess</c> - the standing that lets it
    /// move windows of elevated programs - and Windows refuses to create such a process
    /// directly from one without it: <c>ERROR_ELEVATION_REQUIRED</c>, 740, however the
    /// caller is running. The shell's launch, which is what Explorer, the Start Menu
    /// and Windows Search use, goes through the Application Information service and
    /// succeeds. This used <c>CreateProcess</c>, so <c>shubbak setup</c> - the command
    /// the install notes end with - could not start the very build the installer
    /// installs; the portable build, with no uiAccess, never noticed. The installer's
    /// own launch action was an executable custom action, which Windows Installer runs
    /// with <c>CreateProcess</c> too, so its checkbox did nothing either. It now runs
    /// this.
    /// </para>
    /// <para>
    /// Then waits, briefly, for the pipe. A launch the shell accepts can still end at
    /// once - a uiAccess binary whose signature the machine does not trust, a config
    /// the window manager cannot read - and "started" would have been said of a
    /// process that was already gone. The window manager listens within a few hundred
    /// milliseconds of starting; five seconds is far more than it needs and far less
    /// than anybody would wait before looking.
    /// </para>
    /// </remarks>
    private static string? Start(string daemon, string? configPath)
    {
        try
        {
            var start = new ProcessStartInfo(daemon)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(daemon) ?? string.Empty,
            };

            if (configPath is not null)
            {
                start.ArgumentList.Add("--config");
                start.ArgumentList.Add(configPath);
            }

            // The shell may start the process without handing back a handle - a
            // uiAccess launch is made by a service on the caller's behalf - so a null
            // here is not a failure. Whether it is running is asked of the pipe.
            using Process? process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"could not start {daemon}: {ex.Message}";
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (IpcClient.IsServerRunning()) return null;
            Thread.Sleep(100);
        }

        return $"{daemon} was started but is not answering after five seconds. " +
               $"Its log is {Log.DefaultLogPath}.";
    }

    private static string? Value(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
