using System.Diagnostics;
using Microsoft.Win32;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.Cli;

/// <summary>
/// <c>shubbak doctor</c>: a checklist of the install, each line with its fix.
/// </summary>
/// <remarks>
/// <para>
/// <c>diagnose</c> already exists and is the right thing to attach to an issue: a
/// report of everything, for somebody else to read. This is the other thing, for the
/// person at the keyboard: a dozen yes-or-no questions about the install, answered in
/// order, with the command that fixes each no. The questions are the ones that have
/// actually gone wrong for people - a terminal with the old PATH, a config in a place
/// the resolver does not look, a Run key pointing at a copy that was uninstalled, a
/// palette key that PowerToys Run gets to first.
/// </para>
/// <para>
/// Nothing here changes anything. It exits non-zero when something is wrong, so a
/// script can ask too.
/// </para>
/// </remarks>
internal static class DoctorCommand
{
    private enum Verdict { Ok, Warn, Fail, Info }

    private sealed record Line(Verdict Verdict, string Subject, string Detail, string? Fix = null);

    public static int Run(string[] args)
    {
        var lines = new List<Line>();

        string? configArgument = Value(args, "--config");

        CheckBinaries(lines);
        ConfigLoadResult? config = CheckConfig(lines, configArgument, out string? configPath, out string? configText);
        CheckAutostart(lines);
        CheckRunning(lines, config);
        CheckCollisions(lines, config);
        CheckEnvironment(lines, configText);

        Console.WriteLine(ShubbakVersion.Banner);
        Console.WriteLine();

        foreach (Line line in lines)
        {
            string mark = line.Verdict switch
            {
                Verdict.Ok => " ok  ",
                Verdict.Warn => "warn ",
                Verdict.Fail => "FAIL ",
                _ => "info ",
            };

            Console.WriteLine($"{mark} {line.Subject,-12} {line.Detail}");

            if (line.Fix is { Length: > 0 })
                Console.WriteLine($"      {"",-12} -> {line.Fix}");
        }

        int failures = lines.Count(l => l.Verdict == Verdict.Fail);
        int warnings = lines.Count(l => l.Verdict == Verdict.Warn);

        Console.WriteLine();
        Console.WriteLine(
            failures == 0 && warnings == 0
                ? "Everything looks right."
                : $"{failures} problem(s), {warnings} warning(s).");

        if (configPath is not null && failures == 0)
            Console.WriteLine($"Config: {configPath}");

        return failures == 0 ? 0 : 1;
    }

    // ---- the binaries ------------------------------------------------------

    private static void CheckBinaries(List<Line> lines)
    {
        string? here = Path.GetDirectoryName(Environment.ProcessPath);

        if (here is null)
        {
            lines.Add(new(Verdict.Warn, "binaries", "cannot tell where this program is running from"));
            return;
        }

        // The five programs. shubbak-wm is a hard requirement; the others are only a
        // problem if the config starts them and they are not there.
        foreach (string name in new[] { "shubbak-wm", "taj", "dalil", "ayn" })
        {
            string? found = FindBeside(here, name) ?? FindOnPath(name);

            if (found is null)
            {
                lines.Add(new(
                    name == "shubbak-wm" ? Verdict.Fail : Verdict.Warn,
                    name,
                    $"not found beside {Path.GetFileName(Environment.ProcessPath)} or on PATH",
                    "reinstall, or put the install directory on PATH"));
            }
            else
            {
                lines.Add(new(Verdict.Ok, name, found));
            }
        }

        // The terminal's PATH, which is the thing that is stale right after an install.
        string resolvedHere = FinalTarget(here);
        bool onPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(entry => SamePath(entry, here) || SamePath(entry, resolvedHere));

        if (onPath)
            lines.Add(new(Verdict.Ok, "PATH", "the install directory is on this terminal's PATH"));
        else
            lines.Add(new(
                Verdict.Info,
                "PATH",
                "the install directory is not on this terminal's PATH",
                "open a new terminal if you have just installed; otherwise add it, or type the full path"));
    }

    // ---- the config --------------------------------------------------------

    private static ConfigLoadResult? CheckConfig(List<Line> lines, string? explicitPath, out string? path, out string? text)
    {
        path = null;
        text = null;

        ConfigLocation location = ConfigPathResolver.Resolve(explicitPath);

        if (!location.Found || !File.Exists(location.Path))
        {
            lines.Add(new(
                Verdict.Fail,
                "config",
                "no config file was found, so the window manager would bind no keys",
                $"shubbak setup   (writes the starter to {ConfigPathResolver.DefaultWriteLocation()})"));
            return null;
        }

        path = location.Path!;

        ConfigLoadResult result;

        try
        {
            text = File.ReadAllText(path);
            result = ConfigLoader.Load(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lines.Add(new(Verdict.Fail, "config", $"{path}: {ex.Message}"));
            return null;
        }

        int errors = result.Errors.Count()
            + Taj.Core.TajConfigLoader.Load(text).Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)
            + Dalil.Core.DalilConfigLoader.Validate(text).Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)
            + Ayn.Core.AynConfigLoader.Validate(text).Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

        int warnings = result.Warnings.Count()
            + Taj.Core.TajConfigLoader.Load(text).Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning)
            + Dalil.Core.DalilConfigLoader.Validate(text).Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning)
            + Ayn.Core.AynConfigLoader.Validate(text).Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        if (errors > 0)
            lines.Add(new(Verdict.Fail, "config", $"{path} has {errors} error(s) (via {location.Origin})", "shubbak check-config   shows each with a caret"));
        else if (warnings > 0)
            lines.Add(new(Verdict.Warn, "config", $"{path} has {warnings} warning(s) (via {location.Origin})", "shubbak check-config   shows each with a caret"));
        else
            lines.Add(new(Verdict.Ok, "config", $"{path} (via {location.Origin})"));

        if (result.Config.Keybindings.Count == 0)
            lines.Add(new(Verdict.Warn, "keys", "the config binds no keys", "add a keybindings { } block, or start from `shubbak config init --path <elsewhere>`"));
        else
            lines.Add(new(Verdict.Ok, "keys", $"{result.Config.Keybindings.Count} keybindings, {result.Config.Workspaces.Count} workspaces, {result.Config.Rules.Count} rules"));

        return result;
    }

    // ---- autostart ---------------------------------------------------------

    private static void CheckAutostart(List<Line> lines)
    {
        string? command = Autostart.Registered();

        if (command is null)
        {
            lines.Add(new(Verdict.Info, "autostart", "not set to start at logon", "shubbak autostart enable"));
            return;
        }

        string registered = Autostart.ExecutableFrom(command);

        if (!File.Exists(registered))
        {
            lines.Add(new(Verdict.Fail, "autostart", $"points at {registered}, which does not exist", "shubbak autostart enable   (re-registers this copy)"));
            return;
        }

        if (Autostart.FindDaemon() is { } current && !Autostart.SameFile(current, registered))
        {
            lines.Add(new(Verdict.Warn, "autostart", $"points at a different copy: {registered}", "shubbak autostart enable   (to point it at this one)"));
            return;
        }

        lines.Add(new(Verdict.Ok, "autostart", command));
    }

    // ---- what is running ---------------------------------------------------

    private static void CheckRunning(List<Line> lines, ConfigLoadResult? config)
    {
        bool wm = IpcClient.IsServerRunning();

        lines.Add(wm
            ? new(Verdict.Ok, "running", "the window manager is running")
            : new(Verdict.Info, "running", "the window manager is not running", "shubbak-wm   (or `shubbak setup`)"));

        // The companions only matter if the config asks for them.
        IReadOnlyList<string> startup = config is { } loaded ? loaded.Config.StartupCommands : [];

        CheckStartupCommands(lines, startup);

        foreach ((string component, string what) in new[] { ("taj", "bar"), ("dalil", "palette"), ("ayn", "watcher") })
        {
            bool wanted = startup.Any(c => c.Contains(component, StringComparison.OrdinalIgnoreCase));
            bool running = SingleInstanceLock.IsHeldByAnyone(IpcProtocol.InstanceMutexNameFor(component)) == true;

            if (running)
                lines.Add(new(Verdict.Ok, what, $"{component} is running"));
            else if (wanted && wm)
                lines.Add(new(Verdict.Warn, what, $"{component} is not running, though the config starts it", $"%LOCALAPPDATA%\\Shubbak\\{component}.log says why; or run `{component}` by hand"));
            else if (!wanted)
                lines.Add(new(Verdict.Info, what, $"{component} is not started by the config", $"add `startup-command \"{component}\"` under general {{ }} to have one"));
        }
    }

    /// <summary>
    /// How each companion's <c>startup-command</c> will resolve, the way the window
    /// manager resolves it: a bare name beside <c>shubbak-wm.exe</c>, then PATH.
    /// </summary>
    /// <remarks>
    /// Two things this catches. A bare <c>taj</c> that is nowhere - a zip unpacked
    /// with a file missing, or a copy of the window manager on its own - which the
    /// window manager reports only in its log. And an absolute path to one of the
    /// three, which works on the machine it was written on and on no other: the
    /// author's own config had <c>W:\...\dist\taj.exe</c> for months, and it took a
    /// second machine to notice. A bare name is the portable spelling, and the fix.
    /// </remarks>
    private static void CheckStartupCommands(List<Line> lines, IReadOnlyList<string> startup)
    {
        string? daemonDirectory = Autostart.FindDaemon() is { } daemon ? Path.GetDirectoryName(daemon) : null;

        foreach (string command in startup)
        {
            string file = FirstToken(command.StartsWith("shell-exec ", StringComparison.OrdinalIgnoreCase) ? command[11..] : command);
            string bare = Path.GetFileNameWithoutExtension(file);

            if (bare is not ("taj" or "dalil" or "ayn")) continue;

            bool isPath = file.AsSpan().IndexOfAny(@"\/:") >= 0;

            if (isPath)
            {
                string verdict = File.Exists(file) ? "exists here" : "does not exist here";
                lines.Add(new(
                    Verdict.Warn,
                    "startup",
                    $"`{command}` names {bare} by absolute path, which {verdict} and is specific to this machine",
                    $"write `startup-command \"{bare}\"`; a bare name is found beside shubbak-wm.exe, then on PATH"));
                continue;
            }

            string? beside = daemonDirectory is null ? null : FindBeside(daemonDirectory, bare);
            string? onPath = beside is null ? FindOnPath(bare) : null;

            if (beside is not null)
                lines.Add(new(Verdict.Ok, "startup", $"`{bare}` resolves beside the window manager: {beside}"));
            else if (onPath is not null)
                lines.Add(new(Verdict.Warn, "startup", $"`{bare}` is not beside shubbak-wm.exe; PATH would start {onPath}", "keep the five executables together, so the bar that starts is the one that shipped with the window manager"));
            else
                lines.Add(new(Verdict.Fail, "startup", $"`{bare}` is not beside shubbak-wm.exe or on PATH, so it will not start", "reinstall, or put the install directory on PATH"));
        }
    }

    private static string FirstToken(string commandLine)
    {
        commandLine = commandLine.Trim();

        if (commandLine.StartsWith('"'))
        {
            int closing = commandLine.IndexOf('"', 1);
            if (closing > 0) return commandLine[1..closing];
        }

        int space = commandLine.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? commandLine : commandLine[..space];
    }

    // ---- things that fight -------------------------------------------------

    private static void CheckCollisions(List<Line> lines, ConfigLoadResult? config)
    {
        if (config is not { } loaded) return;

        // PowerToys Run takes alt+space before any hook below it sees the key.
        bool bindsAltSpace = loaded.Config.Keybindings.Any(b =>
            string.Equals(b.Key.Display.Replace(" ", ""), "alt+space", StringComparison.OrdinalIgnoreCase));

        if (bindsAltSpace && IsProcessRunning("PowerToys.PowerLauncher"))
        {
            lines.Add(new(
                Verdict.Warn,
                "alt+space",
                "bound in the config, but PowerToys Run is running and takes it first",
                "move one of them; the starter uses alt+shift+space for the palette for this reason"));
        }

        // Another window manager holding the same hooks makes for two opinions about
        // where every window goes.
        foreach (string rival in new[] { "glazewm", "komorebi", "bug.n", "workspacer", "FancyWM" })
        {
            if (IsProcessRunning(rival))
                lines.Add(new(Verdict.Warn, "rival", $"{rival} is running too; two tiling window managers will fight", $"stop {rival} while using Shubbak"));
        }
    }

    // ---- the machine -------------------------------------------------------

    private static void CheckEnvironment(List<Line> lines, string? configText)
    {
        // Windows 11 is build 22000. Acrylic and the Fluent icon font are its.
        int build = Environment.OSVersion.Version.Build;

        if (configText is not null)
        {
            if (build < 22000 && configText.Contains("\"acrylic\"", StringComparison.OrdinalIgnoreCase))
                lines.Add(new(Verdict.Info, "windows", $"build {build} has no acrylic; the bar shows its background colour instead"));

            // Face name in the config, and what the registry lists the file under -
            // the same except for the variable font, whose named instances ("Text",
            // "Display", "Small") are all one registered file.
            foreach ((string face, string registered) in new[]
                     {
                         ("Segoe Fluent Icons", "Segoe Fluent Icons"),
                         ("Segoe MDL2 Assets", "Segoe MDL2 Assets"),
                         ("Segoe UI Variable", "Segoe UI Variable"),
                     })
            {
                if (configText.Contains(face, StringComparison.OrdinalIgnoreCase) && !FontInstalled(registered))
                    lines.Add(new(Verdict.Warn, "font", $"the config uses \"{face}\", which is not installed", face == "Segoe Fluent Icons" ? "use \"Segoe MDL2 Assets\", which every Windows 10 and 11 has" : "pick an installed font"));
            }
        }

        // Where the install is decides whether elevated windows can be moved.
        string? here = Path.GetDirectoryName(Environment.ProcessPath);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (here is not null && !FinalTarget(here).StartsWith(programFiles, StringComparison.OrdinalIgnoreCase))
            lines.Add(new(Verdict.Info, "elevated", "a portable install: windows of elevated programs are seen but cannot be moved", "the MSI (winget install shubbak, without --scope user) can, from Program Files"));

        // A crash report waiting to be read is worth a line.
        string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shubbak");

        if (Directory.Exists(state))
        {
            string? latest = Directory.EnumerateFiles(state, "crash-*.md").OrderByDescending(f => f).FirstOrDefault();

            if (latest is not null && File.GetLastWriteTimeUtc(latest) > DateTime.UtcNow.AddDays(-7))
                lines.Add(new(Verdict.Warn, "crash", $"a crash report from the last week: {latest}", "read it, and attach it to an issue if it is not obvious"));
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static string? FindBeside(string directory, string name)
    {
        string candidate = Path.Combine(directory, name + ".exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindOnPath(string name)
    {
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(entry, name + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid characters. Someone else's problem.
            }
        }

        return null;
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string FinalTarget(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            Process[] found = Process.GetProcessesByName(name);
            foreach (Process p in found) p.Dispose();
            return found.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Whether a font is installed, by the registry's list rather than GDI's.</summary>
    private static bool FontInstalled(string face)
    {
        foreach (RegistryKey root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key is null) continue;

                foreach (string value in key.GetValueNames())
                    if (value.StartsWith(face, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Try the other root.
            }
        }

        return false;
    }

    private static string? Value(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
