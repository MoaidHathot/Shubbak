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
/// <para>
/// The file itself is <see cref="StarterConfig"/>, in the config project, so that the
/// window manager can write the same one on a first run with no config at all - the
/// case this command used to be the only answer to.
/// </para>
/// </remarks>
internal static class ConfigCommand
{
    /// <summary>The starter, for the tests that hold it to account.</summary>
    internal static string Starter => StarterConfig.Text;

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
        Console.WriteLine("It starts the bar, the palette and the watcher too, and binds the keys listed");
        Console.WriteLine("at the top of the file; alt+shift+space opens the palette. Then:");
        Console.WriteLine();
        Console.WriteLine("  shubbak setup              start it now and at every logon");
        Console.WriteLine("  shubbak-wm                 or just start it now");
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
    internal static string ExampleConfigHint()
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

}
