using System.Globalization;
using System.Text;

namespace Shubbak.Core.Diagnostics;

/// <summary>
/// Builds a single self-contained report describing what Shubbak is doing.
/// </summary>
/// <remarks>
/// <para>
/// The output of <c>shubbak diagnose</c>. It exists because the usual bug report -
/// "windows sometimes go to the wrong place" - is unactionable, and because asking
/// someone to reproduce a problem <i>after</i> enabling logging usually fails: the
/// problem does not recur on demand.
/// </para>
/// <para>
/// The report therefore bundles everything needed to reason about a failure that
/// has <b>already happened</b>: the environment, the config as loaded, the live
/// window tree, and the last few thousand log entries from the ring buffer, which
/// is populated whether or not file logging was ever switched on.
/// </para>
/// </remarks>
public sealed class DiagnosticReport
{
    private readonly StringBuilder _output = new();

    /// <summary>Starts a report.</summary>
    public DiagnosticReport(string title)
    {
        _output.AppendLine("# Shubbak diagnostic report");
        _output.AppendLine();
        _output.Append("Generated: ").AppendLine(
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        _output.Append("Reason:    ").AppendLine(title);
        _output.AppendLine();
    }

    /// <summary>Adds the environment section.</summary>
    public DiagnosticReport AddEnvironment()
    {
        Section("Environment");

        Line("OS", Environment.OSVersion.VersionString);
        Line("64-bit OS", Environment.Is64BitOperatingSystem.ToString());
        Line("64-bit process", Environment.Is64BitProcess.ToString());
        Line("Processors", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        Line("Runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Line("AOT", (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported).ToString());
        Line("Version", typeof(DiagnosticReport).Assembly.GetName().Version?.ToString() ?? "unknown");

        // Which binary is actually running, and when it was built. A stale executable
        // on PATH produces bug reports that contradict the source, and there is no way
        // to tell from the outside - so it is stated rather than inferred.
        AddBinaryIdentity();

        // Elevation is the single most common explanation for "some windows just
        // will not move", so it is reported unconditionally.
        Line("Elevated", IsElevated().ToString());

        Line("Log level", Log.Level.ToString());
        Line("Log file", Log.FilePath ?? "(none)");
        Line("Entries recorded", Log.TotalEntries.ToString(CultureInfo.InvariantCulture));

        _output.AppendLine();
        return this;
    }

    /// <summary>Records the running executable and when it was built.</summary>
    /// <remarks>
    /// A stale binary earlier on <c>PATH</c> than the freshly built one produces bug
    /// reports that flatly contradict the source, and neither side can tell. Reporting
    /// the path and its timestamp settles it in one line.
    /// </remarks>
    private void AddBinaryIdentity()
    {
        string path;

        try
        {
            // ProcessPath, not Assembly.Location: the latter is empty under NativeAOT
            // and single-file, which is exactly how Shubbak ships.
            path = Environment.ProcessPath ?? "(unknown)";
        }
        catch (Exception)
        {
            path = "(unavailable)";
        }

        Line("Executable", path);

        try
        {
            if (File.Exists(path))
            {
                Line("Built", File.GetLastWriteTimeUtc(path)
                    .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));

                Line("Size", (new FileInfo(path).Length / 1024.0 / 1024.0)
                    .ToString("0.00 'MB'", CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Line("Built", "(unreadable)");
        }
    }

    /// <summary>Adds an arbitrary named section.</summary>
    public DiagnosticReport AddSection(string title, string content)
    {
        Section(title);
        _output.AppendLine(content.TrimEnd());
        _output.AppendLine();
        return this;
    }

    /// <summary>Adds a fenced code block.</summary>
    public DiagnosticReport AddCodeSection(string title, string content, string language = "")
    {
        Section(title);
        _output.Append("```").AppendLine(language);
        _output.AppendLine(content.TrimEnd());
        _output.AppendLine("```");
        _output.AppendLine();
        return this;
    }

    /// <summary>
    /// Adds the recent log entries.
    /// </summary>
    /// <remarks>
    /// Comes last, because it is the longest section and everything above it is
    /// context for reading it.
    /// </remarks>
    public DiagnosticReport AddRecentLog(int maxEntries = 2000)
    {
        IReadOnlyList<LogEntry> entries = Log.RecentEntries(maxEntries);

        Section($"Recent log ({entries.Count} entries)");

        if (entries.Count == 0)
        {
            _output.AppendLine("(nothing recorded)");
            _output.AppendLine();
            return this;
        }

        _output.AppendLine("```");
        foreach (LogEntry entry in entries) _output.AppendLine(entry.Format());
        _output.AppendLine("```");
        _output.AppendLine();

        return this;
    }

    /// <summary>Adds guidance on what to do with the report.</summary>
    public DiagnosticReport AddFooter()
    {
        Section("Reproducing");

        _output.AppendLine("""
            To capture more detail, restart with trace logging and reproduce the problem:

            ```
            shubbak-wm --log-level trace --log-file %LOCALAPPDATA%\Shubbak\trace.log
            ```

            Trace level records every window event and every command, which is verbose
            but is what makes a misbehaviour reproducible from the log alone. Then run
            `shubbak diagnose --output report.md` again and attach both files.

            Useful filters once you have a trace:

            ```
            shubbak diagnose | Select-String "Window "     # window lifecycle only
            shubbak diagnose | Select-String "Command "    # what commands ran
            shubbak diagnose | Select-String "Rule "       # why a rule did or did not match
            ```
            """);

        _output.AppendLine();
        return this;
    }

    /// <summary>The finished report.</summary>
    public override string ToString() => _output.ToString();

    /// <summary>Writes the report to a file and returns its full path.</summary>
    public string WriteTo(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(full, ToString());
        return full;
    }

    private void Section(string title) => _output.Append("## ").AppendLine(title).AppendLine();

    private void Line(string label, string value) =>
        _output.Append("- **").Append(label).Append("**: ").AppendLine(value);

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
