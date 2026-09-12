using Shubbak.Config;
using Shubbak.Core.Diagnostics;

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
/// is a thousand lines and exists to document every setting with the reasoning behind
/// it, which is the right thing to read and the wrong thing to inherit: a starter
/// config should be short enough that a newcomer can hold all of it in their head and
/// delete the parts they disagree with.
/// </para>
/// <para>
/// It does, however, turn everything on. The bar, the palette and the watcher are
/// started from it and each has a small section, because a window manager with no bar
/// and no palette is not the thing the readme describes, and a newcomer who has to
/// discover three more programs and how to start them before the desktop looks like
/// the pictures has been handed a worse first hour than necessary. Deleting a section
/// and its <c>startup-command</c> line is one edit.
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
        // The same answer the window manager gives for "where would a new config go",
        // so the file written here is one the resolver finds without a second step.
        string path = Value(args, "--path") ?? ConfigPathResolver.DefaultWriteLocation();
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
        Console.WriteLine("It starts the window manager's bar, palette and watcher too, and binds the");
        Console.WriteLine("keys listed at the top of the file. Then:");
        Console.WriteLine();
        Console.WriteLine("  shubbak-wm                 start the window manager now");
        Console.WriteLine("  shubbak autostart enable   and have it start at logon");
        Console.WriteLine("  shubbak check-config       validate the file after editing");
        Console.WriteLine("  shubbak wm-reload-config   apply it without restarting");
        Console.WriteLine();
        Console.WriteLine(ExampleConfigHint());
        return 0;
    }

    /// <summary>
    /// Where the annotated example config actually is, for the closing line.
    /// </summary>
    /// <remarks>
    /// "Beside this binary" was the previous answer, and it is wrong for the install
    /// most people will have: winget puts a symlink to the executable on PATH and the
    /// example beside the executable itself, a directory away. So the link is
    /// followed before looking, and when the file is still not there - a copy of the
    /// binary on its own somewhere - the answer is the one place it is always kept.
    /// </remarks>
    private static string ExampleConfigHint()
    {
        const string Name = "shubbak.example.kdl";

        try
        {
            if (Environment.ProcessPath is { Length: > 0 } processPath)
            {
                var binary = new FileInfo(processPath);
                string target = binary.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? binary.FullName;

                if (Path.GetDirectoryName(target) is { Length: > 0 } directory)
                {
                    string example = Path.Combine(directory, Name);
                    if (File.Exists(example))
                        return $"The fully annotated reference is {example}";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing about a hint is worth failing over.
        }

        return "The fully annotated reference is docs/shubbak.example.kdl in the repository:\n" +
               $"https://github.com/MoaidHathot/Shubbak/blob/v{ShubbakVersion.Current}/docs/{Name}";
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
    /// Held to account by a test that loads it through the real parsers - the window
    /// manager's, the bar's, the palette's and the watcher's - and asserts none of
    /// them produce a diagnostic. A starter config that does not parse would be a
    /// uniquely bad first impression, and it is the kind of thing that rots quietly
    /// when a setting is renamed.
    /// </remarks>
    internal const string Starter =
        """
        // Shubbak configuration.
        //
        // Everything here has a default, so anything you delete keeps working. The
        // fully annotated reference - every setting, and why it is there - is
        // shubbak.example.kdl, beside the executables and in the repository.
        //
        //   shubbak check-config       validate this file, with carets
        //   shubbak wm-reload-config   apply it without restarting
        //   shubbak config-path        which file is actually in effect
        //
        // The keys, all on Alt:
        //
        //   alt + h j k l              focus left / down / up / right
        //   alt + shift + h j k l      move the window
        //   alt + u p i o              resize: narrower, wider, shorter, taller
        //   alt + 1..5                 go to workspace;  alt + shift + 1..5  send the window there
        //   alt + space                the command palette (Dalil)
        //   alt + shift + space        cycle the layout;  alt + m  monocle
        //   alt + shift + m            float / tile the window
        //   alt + shift + q            close the window
        //   alt + shift + r            reload this file;  alt + shift + e  exit Shubbak

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

            // The bar, the palette and the watcher are separate programs, started
            // here when the window manager starts. Each reads its own section of
            // this file. Delete a line and its section to do without one.
            startup-command "taj"
            startup-command "dalil"
            startup-command "ayn"
        }

        gaps {
            inner 6

            // Around the work area, which the bar has already taken its strip from -
            // so nothing here needs to make room for it.
            outer {
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

            // The palette. Shubbak does not know what a palette is: this raises a
            // named signal, and Dalil is the program listening for it. (PowerToys Run
            // also defaults to alt+space; change one of them if you use both.)
            bind "alt+space" { signal "palette" }

            // Layout. --cycle walks a short list ordered so that each entry looks
            // obviously different from the one before it.
            bind "alt+shift+space" { layout --cycle }
            bind "alt+m" { layout --set monocle }
            bind "alt+shift+m" { toggle-floating }

            bind "alt+shift+q" { close }

            // One key re-reads this file for the window manager and tells the bar
            // and the palette to re-read it too.
            bind "alt+shift+r" { wm-reload-config }

            // Everything goes: the window manager, the bar, the palette and the
            // watcher. `wm-exit` stops the window manager alone and the others wait
            // for it to come back, which is for restarting it.
            bind "alt+shift+e" { exit-all }

            // One pair of bindings per workspace declared above, generated rather
            // than written out - so they cannot drift out of sync with the list.
            for-each "workspace" {
                bind "alt+{name}"       { focus --workspace "{name}" }
                bind "alt+shift+{name}" { move --workspace "{name}" --focus }
            }
        }

        // Facts the watcher (ayn) supplies while they hold. Nothing in this file
        // decides them, which is why they have no `when`; declaring them is what
        // lets the bar below refer to them.
        contexts {
            context "camera-in-use" { }
            context "microphone-in-use" { }
            context "microphone-muted" { }
        }

        // Taj, the bar. One per monitor, each reserving its own strip of screen.
        bar {
            source "clock" kind="time" format="ddd d MMM HH:mm" interval=500

            profile "default" {
                height 32
                background "#1e1e2e"
                foreground "#cdd6f4"
                font "Segoe UI"
                font-size 14

                zone "left" justify="start" gap=4 {
                    workspaces hide-empty=#true active-background="#8dbcff" active-colour="#1e1e2e"
                }

                zone "centre" justify="center" grow=1 {
                    text template="{{ window.title | truncate:90 }}"
                }

                zone "right" justify="end" gap=12 {
                    // Empty, and therefore invisible, until something is unusual:
                    // the keyboard let go, tiling paused, this file unreadable.
                    // Each is clickable, because each is a state the keyboard may
                    // not be able to get you out of.
                    text template="{{ suspended }}" colour="#1e1e2e" background="#f38ba8" on-click="wm-resume"
                    text template="{{ paused }}"    colour="#1e1e2e" background="#f9e2af" on-click="wm-toggle-pause"
                    text template="{{ config }}"    colour="#1e1e2e" background="#fab387" on-click="wm-reload-config"

                    // A camera or microphone in use, as a glyph from the icon font
                    // Windows ships; muted shows the crossed-out one. Clicking the
                    // microphone asks the watcher to flip the system mute.
                    text template="{{ context.camera-in-use | then:\u{E722} }}" font="Segoe MDL2 Assets" colour="#a6e3a1"
                    text template="{{ context.microphone-in-use | then:\u{E720} }}" font="Segoe MDL2 Assets" colour="#a6e3a1" on-click="signal ayn microphone toggle-mute"
                    text template="{{ context.microphone-muted | then:\u{EC54} }}" font="Segoe MDL2 Assets" colour="#1e1e2e" background="#f38ba8" on-click="signal ayn microphone toggle-mute"

                    text template="{{ layout | icon }}" colour="#7f849c"
                    text template="{{ clock }}" colour="#8dbcff"
                }
            }
        }

        // Dalil, the palette. Opened by the signal the alt+space binding raises.
        dalil {
            open-on-signal "palette"
            show-unmanaged #true
            confirm-destructive #true
        }

        // Ayn, the watcher: the window manager's eyes on the rest of the machine.
        // Today that is the camera and the microphone: it holds the three contexts
        // above while a device is in use, or the microphone is muted.
        ayn {
            camera     { in-use "camera-in-use" }
            microphone { in-use "microphone-in-use"; muted "microphone-muted" }
            settle 500
        }
        """;
}
