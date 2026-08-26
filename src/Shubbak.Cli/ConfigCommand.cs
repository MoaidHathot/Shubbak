using Shubbak.Config;

namespace Shubbak.Cli;

/// <summary>
/// Writes a starter configuration file.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the config loader already told people to run it. A missing
/// config is reported with
/// <c>hint: Run 'shubbak config init' to write a starter config</c>, and that command
/// did not exist - so the first thing a new install said to a new user was an
/// instruction that fails. It fell through the CLI's dispatch, was sent down the pipe
/// as a window manager command, and answered with "no window manager is running".
/// </para>
/// <para>
/// The file written here is deliberately not <c>docs/shubbak.example.kdl</c>. That one
/// is 600 lines and exists to document every setting with the reasoning behind it,
/// which is the right thing to read and the wrong thing to inherit: a starter config
/// should be short enough that a newcomer can hold all of it in their head and delete
/// the parts they disagree with.
/// </para>
/// </remarks>
internal static class ConfigCommand
{
    public static int Run(string[] args)
    {
        string action = args.Length > 1 ? args[1] : "";

        return action switch
        {
            "init" => Init(args),
            "" => Missing(),
            _ => Unknown(action),
        };
    }

    private static int Missing()
    {
        Console.Error.WriteLine("shubbak: config needs an action.");
        Console.Error.WriteLine("hint: shubbak config init");
        return 1;
    }

    private static int Unknown(string action)
    {
        Console.Error.WriteLine($"shubbak: unknown config action '{action}'.");
        Console.Error.WriteLine("hint: init");
        return 1;
    }

    private static int Init(string[] args)
    {
        string path = Value(args, "--path") ?? DefaultPath();
        bool force = args.Contains("--force", StringComparer.Ordinal);

        // Refusing is the only safe default. This is the command a confused user
        // reaches for, and the config is the file they have spent the most time in.
        if (File.Exists(path) && !force)
        {
            Console.Error.WriteLine($"shubbak: {path} already exists.");
            Console.Error.WriteLine("hint: pass --force to overwrite it, or --path to write elsewhere.");
            return 1;
        }

        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, Starter);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"shubbak: could not write {path}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Wrote {path}");
        Console.WriteLine();
        Console.WriteLine("  shubbak check-config     validate it after editing");
        Console.WriteLine("  shubbak wm-reload-config apply it without restarting");
        Console.WriteLine();
        Console.WriteLine("The fully annotated reference is shubbak.example.kdl, beside this binary.");
        return 0;
    }

    /// <summary>
    /// Where a new config goes when nobody said.
    /// </summary>
    /// <remarks>
    /// <c>$XDG_CONFIG_HOME</c> first, because somebody who has set it has said where
    /// their configuration lives and writing elsewhere would ignore that. Otherwise
    /// <c>%USERPROFILE%\.config\shubbak</c>, which is the highest-priority location
    /// the resolver searches that does not depend on an environment variable being
    /// set - so the file is found by default rather than only after a second step.
    /// </remarks>
    private static string DefaultPath()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        string root = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;

        return Path.Combine(root, "shubbak", "shubbak.kdl");
    }

    private static string? Value(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// The starter config.
    /// </summary>
    /// <remarks>
    /// Held to account by a test that loads it through the real parser and asserts it
    /// produces no diagnostics. A starter config that does not parse would be a
    /// uniquely bad first impression, and it is the kind of thing that rots quietly
    /// when a setting is renamed.
    /// </remarks>
    internal const string Starter =
        """
        // Shubbak configuration.
        //
        // Everything here has a default, so anything you delete keeps working.
        // The fully annotated reference - every setting, and why it is there - is
        // shubbak.example.kdl, shipped beside the binaries.
        //
        //   shubbak check-config       validate this file, with carets
        //   shubbak wm-reload-config   apply it without restarting
        //   shubbak config-path        which file is actually in effect

        general {
            // New windows start tiled rather than floating.
            initial-window-state "tiling"

            // The layout a new workspace starts in. One of:
            //   splith  splitv  fibonacci  fibonacci-v  fibonacci-mirrored
            //   master-left  master-right  master-top  master-bottom  grid  monocle
            default-layout "splith"

            // Windows on inactive workspaces are cloaked, not hidden. A cloaked
            // window still reports as visible to Win32, so if Shubbak exits or is
            // killed the next run adopts it and brings it back. "hide" cannot be
            // recovered from: the filter rejects invisible windows, so they stay
            // stranded with their process still running.
            hide-method "cloak"

            focus-follows-cursor #false

            // Uncomment to start the bar along with the window manager.
            // startup-command "taj"
        }

        gaps {
            inner 6

            outer {
                // Raise the top gap to reserve room for a bar.
                top 4
                right 4
                bottom 4
                left 4
            }
        }

        workspaces {
            workspace "1"
            workspace "2"
            workspace "3"
            workspace "4"
            workspace "5"
        }

        keybindings {
            // Move focus around the tree.
            bind "alt+h" { focus --direction left }
            bind "alt+j" { focus --direction down }
            bind "alt+k" { focus --direction up }
            bind "alt+l" { focus --direction right }

            // Move the focused window instead of the focus.
            bind "alt+shift+h" { move --direction left }
            bind "alt+shift+j" { move --direction down }
            bind "alt+shift+k" { move --direction up }
            bind "alt+shift+l" { move --direction right }

            // Resize. Writes back to the tree's ratios, so the next layout pass
            // keeps it rather than undoing it.
            bind "alt+u" { resize --width -2% }
            bind "alt+p" { resize --width +2% }
            bind "alt+i" { resize --height -2% }
            bind "alt+o" { resize --height +2% }

            // Layout. --cycle walks a short list ordered so that each entry looks
            // obviously different from the one before it.
            bind "alt+space" { layout --cycle }
            bind "alt+m" { layout --set monocle }
            bind "alt+shift+m" { toggle-floating }

            bind "alt+shift+q" { close }

            // One key re-reads this file for the window manager and tells the bar
            // to re-read it too.
            bind "alt+shift+r" { wm-reload-config }
            bind "alt+shift+e" { wm-exit }

            // One pair of bindings per workspace declared above, generated rather
            // than written out - so they cannot drift out of sync with the list.
            for-each "workspace" {
                bind "alt+{name}"       { focus --workspace "{name}" }
                bind "alt+shift+{name}" { move --workspace "{name}"; focus --workspace "{name}" }
            }
        }
        """;
}
