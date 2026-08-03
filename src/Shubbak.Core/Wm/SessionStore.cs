using System.Text.Json;
using System.Text.Json.Serialization;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Wm;

/// <summary>One remembered window placement.</summary>
/// <param name="ProcessName">Executable name, without extension.</param>
/// <param name="ClassName">Window class.</param>
/// <param name="TitleHash">
/// A hash of the title rather than the title itself. Titles contain document names,
/// URLs and file paths; storing them would turn a convenience file into a record of
/// what the user was working on.
/// </param>
/// <param name="Workspace">Workspace the window was on.</param>
/// <param name="Tags">Workspaces it also belonged to.</param>
/// <param name="Sticky">Whether it followed every workspace.</param>
/// <param name="State">Tiling, floating, and so on.</param>
public sealed record RememberedWindow(
    string ProcessName,
    string ClassName,
    int TitleHash,
    string Workspace,
    IReadOnlyList<string> Tags,
    bool Sticky,
    string State);

/// <summary>Which workspace a monitor was showing.</summary>
/// <param name="DeviceId">The monitor's device id.</param>
/// <param name="ActiveWorkspace">The workspace it was displaying.</param>
/// <param name="Focused">Whether this was the monitor being worked on.</param>
/// <remarks>
/// Remembered because restarting otherwise dropped the user on whichever workspace
/// happened to sort first, with the one they were using still on screen somewhere
/// else. Restoring the windows but not the view is only half the job.
/// </remarks>
public sealed record RememberedMonitor(
    string DeviceId,
    string ActiveWorkspace,
    bool Focused);

/// <summary>A saved session.</summary>
/// <param name="Version">Format version, so an old file can be rejected cleanly.</param>
/// <param name="SavedAt">When it was written.</param>
/// <param name="Windows">Remembered placements.</param>
/// <param name="Monitors">
/// Which workspace each monitor was showing, and which monitor was in use. Optional,
/// so a file written before this existed still loads.
/// </param>
public sealed record Session(
    int Version,
    DateTimeOffset SavedAt,
    IReadOnlyList<RememberedWindow> Windows,
    IReadOnlyList<RememberedMonitor>? Monitors = null);

/// <summary>Source-generated serialisation for the session file.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(Session))]
public sealed partial class SessionJsonContext : JsonSerializerContext;

/// <summary>
/// Remembers which workspace each window was on, across restarts.
/// </summary>
/// <remarks>
/// <para>
/// Without this, restarting the window manager - or rebooting - scatters every
/// window onto whichever workspace it happens to be adopted into, and a carefully
/// arranged set of nineteen workspaces has to be rebuilt by hand. That is the
/// single most annoying thing about running a tiling window manager on Windows.
/// </para>
/// <para>
/// Windows are identified by <b>process name, class and a hash of the title</b>,
/// scored rather than matched exactly. A window handle does not survive a restart,
/// and a title alone is too volatile - a browser's title changes with every tab.
/// Scoring means a browser still lands on the right workspace even though its title
/// has changed since the session was saved.
/// </para>
/// </remarks>
public sealed class SessionStore
{
    private const int CurrentVersion = 1;

    /// <summary>Where sessions are kept by default.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shubbak", "session.json");

    /// <summary>Captures the current placement of every managed window.</summary>
    public static Session Capture(RootNode root, MonitorNode? focusedMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        List<RememberedWindow> windows = [];

        foreach (WindowNode window in root.DescendantWindows())
        {
            if (window.Workspace is not { } workspace) continue;

            // Scratchpad contents are deliberately not remembered: restoring them
            // would summon a hidden window into view on the next start, which is
            // the opposite of what stashing it meant.
            if (workspace.IsScratchpad) continue;

            windows.Add(new RememberedWindow(
                window.Identity.ProcessName,
                window.Identity.ClassName,
                HashTitle(window.Identity.Title),
                workspace.Name,
                [.. window.Tags],
                window.IsSticky,
                window.State.ToString()));
        }

        List<RememberedMonitor> monitors = [];

        foreach (MonitorNode monitor in root.Monitors)
        {
            if (monitor.ActiveWorkspace is not { } active) continue;

            monitors.Add(new RememberedMonitor(
                monitor.DeviceId,
                active.Name,
                ReferenceEquals(monitor, focusedMonitor)));
        }

        return new Session(CurrentVersion, DateTimeOffset.Now, windows, monitors);
    }

    /// <summary>Writes a session to disk.</summary>
    /// <param name="root">The tree to capture.</param>
    /// <param name="path">Where to write; the default location when null.</param>
    /// <param name="routine">
    /// True for the periodic save, which is silent unless something changed. The
    /// deliberate saves - shutdown especially - stay audible, because those are the
    /// ones worth confirming happened.
    /// </param>
    /// <param name="focusedMonitor">The monitor being worked on, when known.</param>
    public static bool Save(RootNode root, string? path = null, bool routine = false, MonitorNode? focusedMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        path ??= DefaultPath;

        try
        {
            Session session = Capture(root, focusedMonitor);

            string json = JsonSerializer.Serialize(session, SessionJsonContext.Default.Session);

            // Written only when something changed. The periodic save fired every thirty
            // seconds regardless, so an untouched desktop still rewrote the file nearly
            // three thousand times a day and announced each one - which was half of
            // everything the log had to say.
            //
            // Keyed by path, and only trusted while the file it describes is still
            // there. A single shared fingerprint claimed that any path was up to date
            // once any other path had been written with the same contents - so deleting
            // the session file meant it was never written again, and two saves to
            // different paths in one process silently produced one file.
            string fingerprint = Fingerprint(session);

            if (routine && IsAlreadyOnDisk(path, fingerprint)) return true;

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Written to a temporary file and moved into place, so a crash midway
            // cannot leave a truncated session that fails to parse on next start.
            string temporary = path + ".tmp";

            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);

            RecordWritten(path, fingerprint);

            Log.Debug(LogCategory.Wm, $"session saved: {session.Windows.Count} windows -> {path}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error(LogCategory.Wm, "could not save session", ex);
            return false;
        }
    }

    /// <summary>What the last write to each path contained, so an unchanged one can be skipped.</summary>
    private static readonly Dictionary<string, string> s_lastWritten =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lock s_lastWrittenGate = new();

    /// <summary>Whether this exact session is already the contents of that file.</summary>
    /// <remarks>
    /// Checks the file is still there as well as what it held. A remembered
    /// fingerprint describes a file, and a file that has been deleted no longer
    /// matches anything - without the existence check, removing the session file left
    /// Shubbak convinced it was already written and it never came back.
    /// </remarks>
    private static bool IsAlreadyOnDisk(string path, string fingerprint)
    {
        lock (s_lastWrittenGate)
        {
            if (!s_lastWritten.TryGetValue(path, out string? previous)) return false;
            if (!string.Equals(previous, fingerprint, StringComparison.Ordinal)) return false;
        }

        return File.Exists(path);
    }

    private static void RecordWritten(string path, string fingerprint)
    {
        lock (s_lastWrittenGate) s_lastWritten[path] = fingerprint;
    }

    /// <summary>
    /// A comparable summary of a session, ignoring what cannot change a placement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The capture time is excluded, or every session would differ from the last.
    /// </para>
    /// <para>
    /// So is the title, which is the one that mattered. A browser tab, an unread count
    /// or a terminal's current directory rewrites it constantly, and none of that moves
    /// the window anywhere - yet it defeated the check entirely and the file was still
    /// being written every thirty seconds. The title is only a tiebreaker when
    /// restoring, between several windows of the same application; process and class
    /// are what actually place a window. A clean exit writes unconditionally, so the
    /// titles on disk are current exactly when they are read.
    /// </para>
    /// </remarks>
    private static string Fingerprint(Session session)
    {
        var builder = new System.Text.StringBuilder(session.Windows.Count * 48);

        foreach (RememberedWindow window in session.Windows)
        {
            builder.Append(window.Workspace).Append('\u001f')
                   .Append(window.ProcessName).Append('\u001f')
                   .Append(window.ClassName).Append('\u001f')
                   .Append(window.Sticky).Append('\u001f')
                   .Append(string.Join(',', window.Tags)).Append('\u001f')
                   .Append(window.State).Append('\u001e');
        }

        // Which workspace each monitor is showing is part of what has to be restored,
        // so switching workspace is a real change and does get written.
        foreach (RememberedMonitor monitor in session.Monitors ?? [])
        {
            builder.Append(monitor.DeviceId).Append('\u001f')
                   .Append(monitor.ActiveWorkspace).Append('\u001f')
                   .Append(monitor.Focused).Append('\u001e');
        }

        return builder.ToString();
    }

    /// <summary>Reads a session from disk, or null if there is none to read.</summary>
    public static Session? Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path)) return null;

            Session? session = JsonSerializer.Deserialize(
                File.ReadAllText(path), SessionJsonContext.Default.Session);

            if (session is null) return null;

            if (session.Version != CurrentVersion)
            {
                Log.Warn(LogCategory.Wm,
                    $"ignoring session file version {session.Version}; expected {CurrentVersion}");
                return null;
            }

            return session;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt session is not worth failing startup over; the user simply
            // gets the default placement.
            Log.Warn(LogCategory.Wm, $"could not read session: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The workspace a window should be restored to, or null if it is unrecognised.
    /// </summary>
    /// <remarks>
    /// Scored rather than matched exactly. The process and class must agree - they
    /// are stable - and a matching title hash breaks ties between several windows of
    /// the same application, which is what puts three browser windows back on three
    /// different workspaces rather than all on the first one.
    /// </remarks>
    public static RememberedWindow? Match(Session session, WindowIdentity identity, HashSet<int> claimed)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(claimed);

        int titleHash = HashTitle(identity.Title);

        RememberedWindow? best = null;
        int bestScore = 0;
        int bestIndex = -1;

        for (int i = 0; i < session.Windows.Count; i++)
        {
            if (claimed.Contains(i)) continue;

            RememberedWindow candidate = session.Windows[i];

            if (!string.Equals(candidate.ProcessName, identity.ProcessName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(candidate.ClassName, identity.ClassName, StringComparison.Ordinal))
                continue;

            // Process and class agreeing is enough to restore; a title match on top
            // of that is what disambiguates several windows of the same app.
            int score = candidate.TitleHash == titleHash ? 2 : 1;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
                bestIndex = i;
            }
        }

        // Each remembered entry is consumed once, so N windows of one application
        // are distributed across the N workspaces they came from instead of all
        // matching the first entry.
        if (bestIndex >= 0) claimed.Add(bestIndex);

        return best;
    }

    /// <summary>
    /// Hashes a title for identification.
    /// </summary>
    /// <remarks>
    /// Deliberately not a cryptographic hash and deliberately not reversible: the
    /// point is to compare titles without storing them, because titles contain
    /// document names, URLs and file paths.
    /// </remarks>
    private static int HashTitle(string title) =>
        string.IsNullOrEmpty(title) ? 0 : title.GetHashCode(StringComparison.OrdinalIgnoreCase);
}
