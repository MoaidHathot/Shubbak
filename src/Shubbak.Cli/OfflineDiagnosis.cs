using System.Globalization;
using System.Text;
using Shubbak.Config;
using Shubbak.Core.Diagnostics;

namespace Shubbak.Cli;

/// <summary>
/// The report <c>shubbak diagnose</c> writes when there is no window manager to ask.
/// </summary>
/// <remarks>
/// <para>
/// A report is most wanted when the window manager has died, and that was the one
/// case it refused: "a report needs the running window manager to describe its
/// state". True of the live tree and the log ring, and of nothing else - the
/// environment, which binary is installed, whether the config parses and what every
/// loader says about it, what the log files on disk recorded before the process went,
/// and whether the process wrote a crash report on its way out are all on disk, and
/// are exactly what a report about a crash is for.
/// </para>
/// <para>
/// Said plainly at the top that it was made without a window manager, so that a
/// reader does not go looking for the tree. What is missing is listed, and so is the
/// command that gives a full report once the window manager is back.
/// </para>
/// </remarks>
public static class OfflineDiagnosis
{
    /// <summary>How much of the end of each log file is worth carrying: enough to see the death, not the week.</summary>
    public const int LogTailLines = 400;

    /// <summary>How far back a crash report is worth mentioning.</summary>
    public static readonly TimeSpan CrashReportAge = TimeSpan.FromDays(7);

    /// <summary>Builds the report from what is on disk.</summary>
    /// <param name="reason">Why it was asked for, as the live report records it.</param>
    /// <param name="explicitConfigPath">A <c>--config</c> given on the command line, or null to resolve as the window manager does.</param>
    /// <param name="stateDirectory">Where the logs, the session and the crash reports live; <c>%LOCALAPPDATA%\Shubbak</c> unless a test says otherwise.</param>
    /// <param name="now">The clock, for the crash-report window; a test names one.</param>
    public static string Build(string reason, string? explicitConfigPath = null, string? stateDirectory = null, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(reason);

        string state = stateDirectory ?? Shubbak.Core.ShubbakPaths.StateDirectory;
        DateTime today = now ?? DateTime.Now;

        var report = new DiagnosticReport(reason);

        report.AddSection("Made without a window manager", """
            No window manager was running when this report was made, so it has no live
            window tree, no monitor list, no contexts in force and none of the log ring the
            running process keeps one level more verbose than its file. What it has is
            everything on disk: the binaries, the configuration as every loader reads it,
            the tails of the log files, the session file, and any crash report from the
            last week. Start the window manager and run `shubbak diagnose -o report.md`
            again for the rest, and attach both.
            """);

        report.AddEnvironment();

        AddInstalledBinaries(report);
        AddConfig(report, explicitConfigPath);
        AddCrashReports(report, state, today);
        AddSession(report, state);
        AddLogTails(report, state);

        return report.AddFooter().ToString();
    }

    /// <summary>Which programs are installed beside this one, and how old each is - the question a stale-binary report answers.</summary>
    private static void AddInstalledBinaries(DiagnosticReport report)
    {
        var text = new StringBuilder();
        string? here = Path.GetDirectoryName(Environment.ProcessPath);

        if (here is null)
        {
            report.AddSection("Installed binaries", "(cannot tell where this program is running from)");
            return;
        }

        foreach (string name in new[] { "shubbak-wm", "shubbak", "taj", "dalil", "ayn" })
        {
            string path = Path.Combine(here, name + ".exe");

            if (!File.Exists(path))
            {
                text.Append("- **").Append(name).AppendLine("**: not beside this program");
                continue;
            }

            try
            {
                var info = new FileInfo(path);

                text.Append("- **").Append(name).Append("**: ")
                    .Append(info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))
                    .Append(", ")
                    .Append((info.Length / 1024.0 / 1024.0).ToString("0.00 'MB'", CultureInfo.InvariantCulture))
                    .AppendLine();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                text.Append("- **").Append(name).AppendLine("**: (unreadable)");
            }
        }

        report.AddSection("Installed binaries", text.ToString());
    }

    /// <summary>
    /// The configuration as resolved and as every loader reads it. The file itself
    /// follows, so a reader can see the line a diagnostic points at.
    /// </summary>
    private static void AddConfig(DiagnosticReport report, string? explicitPath)
    {
        ConfigLocation location;

        try
        {
            location = ConfigPathResolver.Resolve(explicitPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            report.AddSection("Configuration", $"(could not resolve the configuration path: {ex.Message})");
            return;
        }

        if (!location.Found || location.Path is not { } path || !File.Exists(path))
        {
            report.AddSection("Configuration",
                "No configuration file was found, so the window manager would have started on its defaults: " +
                "every window tiled, no key bound, no bar, no palette. `shubbak setup` writes the starter.");
            return;
        }

        string text;

        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.AddSection("Configuration", $"- **Path**: {path} (via {location.Origin})\n- **Read**: failed: {ex.Message}");
            return;
        }

        var summary = new StringBuilder();
        summary.Append("- **Path**: ").Append(path).Append(" (via ").Append(location.Origin).AppendLine(")");
        summary.Append("- **Size**: ").Append(text.Length.ToString("N0", CultureInfo.InvariantCulture)).AppendLine(" characters");

        List<(string Loader, Diagnostic Diagnostic)> problems = [];

        try
        {
            ConfigLoadResult wm = ConfigLoader.Load(text);
            summary.Append("- **Window manager**: ")
                .Append(wm.Config.Keybindings.Count).Append(" keybindings, ")
                .Append(wm.Config.Workspaces.Count).Append(" workspaces, ")
                .Append(wm.Config.Rules.Count).Append(" rules, ")
                .Append(wm.Config.Contexts.Count).AppendLine(" contexts");

            problems.AddRange(wm.Diagnostics.Select(d => ("SHB", d)));
            problems.AddRange(Taj.Core.TajConfigLoader.Load(text).Diagnostics.Select(d => ("TAJ", d)));
            problems.AddRange(Dalil.Core.DalilConfigLoader.Validate(text).Diagnostics.Select(d => ("DAL", d)));
            problems.AddRange(Ayn.Core.AynConfigLoader.Validate(text).Diagnostics.Select(d => ("AYN", d)));
        }
        catch (Exception ex)
        {
            // A loader that throws on a file is itself the finding.
            summary.Append("- **Loading threw**: ").Append(ex.GetType().Name).Append(": ").AppendLine(ex.Message);
        }

        int errors = problems.Count(p => p.Diagnostic.Severity == DiagnosticSeverity.Error);
        int warnings = problems.Count(p => p.Diagnostic.Severity == DiagnosticSeverity.Warning);

        summary.Append("- **Diagnostics**: ").Append(errors).Append(" error(s), ").Append(warnings).AppendLine(" warning(s)");

        report.AddSection("Configuration", summary.ToString());

        if (problems.Count > 0)
        {
            var rendered = new StringBuilder();

            foreach ((string _, Diagnostic diagnostic) in problems)
                rendered.Append(diagnostic.Render(text, path));

            report.AddCodeSection("Configuration diagnostics", rendered.ToString());
        }

        report.AddCodeSection("Configuration file", text, "kdl");
    }

    /// <summary>Crash reports the window manager wrote on its way out, newest first, within the week.</summary>
    private static void AddCrashReports(DiagnosticReport report, string state, DateTime now)
    {
        if (!Directory.Exists(state))
        {
            report.AddSection("Crash reports", $"(no state directory at {state}; the window manager has never run as this user)");
            return;
        }

        List<FileInfo> recent;

        try
        {
            recent = [.. Directory.EnumerateFiles(state, "crash-*.md")
                .Select(f => new FileInfo(f))
                .Where(f => now - f.LastWriteTime <= CrashReportAge)
                .OrderByDescending(f => f.LastWriteTime)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.AddSection("Crash reports", $"(could not list {state}: {ex.Message})");
            return;
        }

        if (recent.Count == 0)
        {
            report.AddSection("Crash reports", $"None from the last {CrashReportAge.TotalDays:F0} days in {state}.");
            return;
        }

        var text = new StringBuilder();

        foreach (FileInfo crash in recent)
        {
            text.Append("- ").Append(crash.Name).Append(" (")
                .Append(crash.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .AppendLine(")");
        }

        report.AddSection($"Crash reports ({recent.Count} in the last {CrashReportAge.TotalDays:F0} days)", text.ToString());

        // The newest one whole, since it is the report the running process would have
        // written had it been asked, and a reader should not have to go and find it.
        try
        {
            report.AddCodeSection($"Newest crash report: {recent[0].Name}", File.ReadAllText(recent[0].FullName), "markdown");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.AddSection($"Newest crash report: {recent[0].Name}", $"(unreadable: {ex.Message})");
        }
    }

    /// <summary>The session file, which says which windows were where when the window manager last saved.</summary>
    private static void AddSession(DiagnosticReport report, string state)
    {
        string path = Path.Combine(state, "session.json");

        if (!File.Exists(path))
        {
            report.AddSection("Session", "(no session file; the window manager has never saved one, or it was deleted)");
            return;
        }

        try
        {
            var info = new FileInfo(path);

            report.AddCodeSection(
                $"Session (saved {info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)})",
                File.ReadAllText(path),
                "json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.AddSection("Session", $"(unreadable: {ex.Message})");
        }
    }

    /// <summary>
    /// The tail of each program's log, and of its most recent rotation, so a death
    /// that rotated the log on the way down is still in the report.
    /// </summary>
    private static void AddLogTails(DiagnosticReport report, string state)
    {
        foreach (string program in new[] { "shubbak", "taj", "dalil", "ayn" })
        {
            foreach (string suffix in new[] { "", ".1" })
            {
                string path = Path.Combine(state, program + ".log" + suffix);
                if (!File.Exists(path)) continue;

                string title = program == "shubbak" ? "shubbak-wm" : program;

                try
                {
                    var info = new FileInfo(path);
                    string[] lines = ReadSharedLines(path);
                    IEnumerable<string> tail = lines.Length > LogTailLines ? lines[^LogTailLines..] : lines;

                    report.AddCodeSection(
                        $"Log: {title}{suffix} (last {Math.Min(lines.Length, LogTailLines)} of {lines.Length} lines, written " +
                        $"{info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)})",
                        string.Join('\n', tail));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    report.AddSection($"Log: {title}{suffix}", $"(unreadable: {ex.Message})");
                }
            }
        }
    }

    /// <summary>
    /// Reads a file another process may be writing to.
    /// </summary>
    /// <remarks>
    /// <c>File.ReadAllLines</c> opens with <c>FileShare.Read</c>, which a writer that
    /// holds the file open for writing refuses - and the bar, the palette and the
    /// watcher hold their logs open for as long as they run, which is exactly when a
    /// report about the window manager dying is being written. Asking for
    /// <c>FileShare.ReadWrite</c> is what the writer allows; the first live report
    /// came back with two logs marked unreadable for want of it.
    /// </remarks>
    private static string[] ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        List<string> lines = [];

        while (reader.ReadLine() is { } line) lines.Add(line);

        return [.. lines];
    }
}
