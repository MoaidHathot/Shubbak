using System.Diagnostics;
using Shubbak.Config;
using Shubbak.Ipc;

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
/// </remarks>
internal static class SetupCommand
{
    public static int Run(string[] args)
    {
        bool noAutostart = args.Contains("--no-autostart", StringComparer.Ordinal);
        bool noStart = args.Contains("--no-start", StringComparer.Ordinal);

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
                Console.Error.WriteLine($"shubbak: could not write {configPath}: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"config      {configPath} (written)");
        }

        // 2. The daemon, which both remaining steps need.
        string? daemon = Autostart.FindDaemon();

        if (daemon is null)
        {
            Console.Error.WriteLine("shubbak: could not find shubbak-wm.exe beside this program or on PATH.");
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

            if (Autostart.Register(daemon, extra) is null) return 1;

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
        else
        {
            try
            {
                var start = new ProcessStartInfo(daemon) { UseShellExecute = false };

                if (Value(args, "--config") is { } explicit_)
                {
                    start.ArgumentList.Add("--config");
                    start.ArgumentList.Add(explicit_);
                }

                using Process? process = Process.Start(start);

                if (process is null)
                {
                    Console.Error.WriteLine($"shubbak: {daemon} did not start.");
                    return 1;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Console.Error.WriteLine($"shubbak: could not start {daemon}: {ex.Message}");
                return 1;
            }

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

    private static string? Value(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
