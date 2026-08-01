using System.Runtime.CompilerServices;
using System.Text;

namespace Shubbak.Core.Diagnostics;

/// <summary>
/// Shubbak's logger.
/// </summary>
/// <remarks>
/// <para>
/// Static rather than injected. A window manager has exactly one of these, it is
/// needed from the deepest parts of the platform layer, and threading an
/// <c>ILogger</c> through the hook callback would defeat the allocation rules that
/// keep those paths fast.
/// </para>
/// <para>
/// <b>Zero cost when a level is disabled.</b> Every call site is guarded by
/// <see cref="IsEnabled"/>, which reads one volatile field. Callers on hot paths
/// must additionally guard <i>construction</i> of the message, because interpolating
/// a string allocates whether or not the logger keeps it:
/// </para>
/// <code>
/// if (Log.IsEnabled(LogLevel.Trace)) Log.Trace(LogCategory.Window, $"...");
/// </code>
/// <para>
/// A <b>ring buffer</b> holds recent entries even when file logging is off, at a
/// level one step more verbose than the sink. That way <c>shubbak diagnose</c> can
/// report what just happened without the user having had the foresight to enable
/// logging before the problem occurred - which is the single most common reason bug
/// reports are unactionable.
/// </para>
/// </remarks>
public static class Log
{
    private static volatile LogLevel s_level = LogLevel.Information;
    private static volatile bool s_toConsole = true;

    private static readonly Lock s_gate = new();
    private static StreamWriter? s_file;
    private static string? s_filePath;

    // Kept small enough to be cheap and large enough to cover the seconds before a
    // failure, which is the window that actually matters.
    private const int RingCapacity = 2048;
    private static readonly LogEntry[] s_ring = new LogEntry[RingCapacity];
    private static int s_ringWrite;
    private static long s_totalEntries;

    /// <summary>The minimum level written to the sinks.</summary>
    public static LogLevel Level
    {
        get => s_level;
        set => s_level = value;
    }

    /// <summary>Whether entries also go to standard error.</summary>
    public static bool ToConsole
    {
        get => s_toConsole;
        set => s_toConsole = value;
    }

    /// <summary>The file being written to, if any.</summary>
    public static string? FilePath => s_filePath;

    /// <summary>Total entries recorded since start, including dropped ones.</summary>
    public static long TotalEntries => Interlocked.Read(ref s_totalEntries);

    /// <summary>
    /// Whether anything would be recorded at this level.
    /// </summary>
    /// <remarks>
    /// Compares against the ring buffer's threshold, not the sink's, because the
    /// ring records one level deeper than the sink writes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(LogLevel level) => level >= RingLevel();

    private static LogLevel RingLevel()
    {
        LogLevel level = s_level;
        return level == LogLevel.None ? LogLevel.None : (LogLevel)Math.Max(0, (int)level - 1);
    }

    /// <summary>Starts writing to a file, replacing any previous one.</summary>
    /// <param name="path">Where to write. Its directory is created if needed.</param>
    /// <param name="append">Whether to append rather than truncate.</param>
    public static void OpenFile(string path, bool append = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        lock (s_gate)
        {
            CloseFileCore();

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Rotate rather than grow without bound: a Trace-level session can
            // produce tens of megabytes in minutes.
            if (!append && File.Exists(path))
            {
                try
                {
                    string previous = path + ".1";
                    File.Delete(previous);
                    File.Move(path, previous);
                }
                catch (IOException)
                {
                    // A locked previous log is not worth failing startup over.
                }
            }

            s_file = new StreamWriter(
                new FileStream(path, append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            s_filePath = path;

            s_file.WriteLine($"# Shubbak log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            s_file.WriteLine($"# level={s_level}");
        }
    }

    /// <summary>Stops writing to the file.</summary>
    public static void CloseFile()
    {
        lock (s_gate) CloseFileCore();
    }

    private static void CloseFileCore()
    {
        s_file?.Dispose();
        s_file = null;
        s_filePath = null;
    }

    public static void Trace(LogCategory category, string message) =>
        Write(LogLevel.Trace, category, message);

    public static void Debug(LogCategory category, string message) =>
        Write(LogLevel.Debug, category, message);

    public static void Info(LogCategory category, string message) =>
        Write(LogLevel.Information, category, message);

    public static void Warn(LogCategory category, string message) =>
        Write(LogLevel.Warning, category, message);

    public static void Error(LogCategory category, string message) =>
        Write(LogLevel.Error, category, message);

    /// <summary>Records an exception with its stack.</summary>
    public static void Error(LogCategory category, string message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write(LogLevel.Error, category, $"{message}: {exception.GetType().Name}: {exception.Message}");
        Write(LogLevel.Debug, category, exception.StackTrace ?? "(no stack trace)");
    }

    private static void Write(LogLevel level, LogCategory category, string message)
    {
        if (level < RingLevel()) return;

        var entry = new LogEntry(DateTime.Now, level, category, message);

        // The ring always records; the sinks are more selective. This is what lets
        // `shubbak diagnose` show the moments before a failure even when the user
        // had not enabled logging in advance.
        int slot = Interlocked.Increment(ref s_ringWrite) - 1;
        s_ring[(int)((uint)slot % RingCapacity)] = entry;
        Interlocked.Increment(ref s_totalEntries);

        if (level < s_level) return;

        string line = entry.Format();

        if (s_toConsole) Console.Error.WriteLine(line);

        if (s_file is not null)
        {
            lock (s_gate)
            {
                try
                {
                    s_file?.WriteLine(line);
                }
                catch (IOException)
                {
                    // A full or disconnected disk must not take the window manager
                    // down with it.
                }
            }
        }
    }

    /// <summary>
    /// The most recent entries, oldest first.
    /// </summary>
    /// <remarks>
    /// Used by <c>shubbak diagnose</c> and by the crash handler.
    /// </remarks>
    public static IReadOnlyList<LogEntry> RecentEntries(int max = RingCapacity)
    {
        int written = Volatile.Read(ref s_ringWrite);
        int available = Math.Min(written, RingCapacity);
        int take = Math.Min(max, available);

        if (take <= 0) return [];

        var entries = new LogEntry[take];
        int start = written - take;

        for (int i = 0; i < take; i++)
            entries[i] = s_ring[(int)((uint)(start + i) % RingCapacity)];

        return entries;
    }

    /// <summary>Parses a level name, accepting common abbreviations.</summary>
    public static bool TryParseLevel(string? text, out LogLevel level)
    {
        switch (text?.ToLowerInvariant())
        {
            case "trace" or "trc" or "verbose": level = LogLevel.Trace; return true;
            case "debug" or "dbg": level = LogLevel.Debug; return true;
            case "info" or "information" or "inf": level = LogLevel.Information; return true;
            case "warn" or "warning" or "wrn": level = LogLevel.Warning; return true;
            case "error" or "err": level = LogLevel.Error; return true;
            case "none" or "off" or "silent": level = LogLevel.None; return true;
            default: level = LogLevel.Information; return false;
        }
    }

    /// <summary>The default log location.</summary>
    public static string DefaultLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shubbak", "shubbak.log");

    /// <summary>Resets everything. Test support.</summary>
    internal static void ResetForTests()
    {
        lock (s_gate)
        {
            CloseFileCore();
            s_level = LogLevel.Information;
            s_toConsole = true;
            Array.Clear(s_ring);
            Volatile.Write(ref s_ringWrite, 0);
            Interlocked.Exchange(ref s_totalEntries, 0);
        }
    }
}
