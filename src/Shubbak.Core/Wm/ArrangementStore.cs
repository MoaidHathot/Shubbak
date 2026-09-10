using Shubbak.Core.Diagnostics;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shubbak.Core.Wm;

/// <summary>
/// One node of a recorded tree: a container with its layout and children, or a
/// window by what it is.
/// </summary>
/// <param name="Ratio">Its share of the parent's main axis, as it was.</param>
/// <param name="Layout">The layout, for a container; null for a window.</param>
/// <param name="Children">The children, for a container; null for a window.</param>
/// <param name="ProcessName">The window's process, without extension.</param>
/// <param name="ClassName">The window's class.</param>
/// <param name="TitleHash">A hash of the window's title, for telling two of one program apart.</param>
/// <param name="ProcessPath">The window's executable, for telling two programs of one name apart.</param>
/// <remarks>
/// A window is recorded by process and class, with the title hashed and the path kept,
/// exactly as the session file records one and for the same reasons: process and
/// class are what a window <em>is</em>, a title is what it is showing and changes
/// every minute, and a title written to disk is a record of what the user was doing.
/// The hash is a tiebreaker between two windows of one program, nothing more.
/// </remarks>
public sealed record ArrangementNode(
    double Ratio,
    string? Layout = null,
    IReadOnlyList<ArrangementNode>? Children = null,
    string? ProcessName = null,
    string? ClassName = null,
    int? TitleHash = null,
    string? ProcessPath = null)
{
    /// <summary>Whether this records a container rather than a window.</summary>
    [JsonIgnore]
    public bool IsContainer => Layout is not null;

    /// <summary>How many windows this node records, itself included.</summary>
    [JsonIgnore]
    public int WindowCount
    {
        get
        {
            if (!IsContainer) return 1;

            int count = 0;
            foreach (ArrangementNode child in Children ?? []) count += child.WindowCount;
            return count;
        }
    }

    /// <summary>
    /// How well a window fits this record: 0 for not at all, more for closer.
    /// </summary>
    /// <remarks>
    /// Process and class must agree - the same rule the session file applies - and
    /// the title hash and the path each add one when they agree too, so that of two
    /// windows of one program the one that was here is preferred.
    /// </remarks>
    public int Score(WindowNode window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (IsContainer) return 0;

        WindowIdentity identity = window.Identity;

        if (!string.Equals(identity.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase)) return 0;
        if (!string.Equals(identity.ClassName, ClassName, StringComparison.Ordinal)) return 0;

        int score = 1;

        if (TitleHash is { } hash && hash == Arrangements.HashTitle(identity.Title)) score++;

        if (ProcessPath is { Length: > 0 } path &&
            string.Equals(identity.ProcessPath, path, StringComparison.OrdinalIgnoreCase))
        {
            score++;
        }

        return score;
    }

    /// <summary>The window, as a person would say it: <c>firefox / MozillaWindowClass</c>.</summary>
    [JsonIgnore]
    public string Description => IsContainer ? $"{Layout} container" : $"{ProcessName} / {ClassName}";
}

/// <summary>
/// A workspace's tree of windows, recorded so it can be put back.
/// </summary>
/// <param name="Name">What it is called.</param>
/// <param name="Workspace">The workspace it was recorded from, for the record.</param>
/// <param name="Layout">The workspace's own layout.</param>
/// <param name="Children">The workspace's children, in order.</param>
/// <param name="SavedAt">When.</param>
public sealed record Arrangement(
    string Name,
    string Workspace,
    string Layout,
    IReadOnlyList<ArrangementNode> Children,
    DateTimeOffset SavedAt)
{
    /// <summary>How many windows it records.</summary>
    [JsonIgnore]
    public int WindowCount
    {
        get
        {
            int count = 0;
            foreach (ArrangementNode child in Children) count += child.WindowCount;
            return count;
        }
    }
}

/// <summary>Everything in the arrangements file.</summary>
public sealed record ArrangementFile(int Version, IReadOnlyList<Arrangement> Arrangements);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(ArrangementFile))]
public sealed partial class ArrangementJsonContext : JsonSerializerContext;

/// <summary>
/// Records a workspace's tree.
/// </summary>
/// <remarks>
/// <para>
/// This is what the session file deliberately does not do. The session file says
/// which workspace each window belongs to, so that a restart puts things back where
/// they were; it is applied while windows arrive in whatever order Windows enumerates
/// them, which is no order to rebuild a tree in. An arrangement is asked for, by
/// name, when every window is already here - a demo whose windows have been dragged
/// about, and want to be back where they were - and so it can record the shape.
/// </para>
/// <para>
/// Only what tiles. A floating or full-screen window is in the tree but takes no
/// part in dividing it, and an arrangement is about the dividing. A container left
/// with one child by that omission is recorded as the child, at the container's
/// share, which is what the tree would have done itself.
/// </para>
/// </remarks>
public static class Arrangements
{
    /// <summary>
    /// Records a workspace, or null when nothing on it tiles.
    /// </summary>
    public static Arrangement? Capture(WorkspaceNode workspace, string name, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        List<ArrangementNode> children = [];

        foreach (Node child in workspace.Children)
            if (CaptureNode(child) is { } recorded) children.Add(recorded);

        if (children.Count == 0) return null;

        return new Arrangement(name, workspace.Name, workspace.Layout.Name, children, now);
    }

    private static ArrangementNode? CaptureNode(Node node)
    {
        switch (node)
        {
            case WindowNode window:
                if (!window.ParticipatesInTiling) return null;

                return new ArrangementNode(
                    window.SizeRatio,
                    ProcessName: window.Identity.ProcessName,
                    ClassName: window.Identity.ClassName,
                    TitleHash: HashTitle(window.Identity.Title),
                    ProcessPath: window.Identity.ProcessPath);

            case ContainerNode container:
                List<ArrangementNode> children = [];

                foreach (Node child in container.Children)
                    if (CaptureNode(child) is { } recorded) children.Add(recorded);

                return children.Count switch
                {
                    0 => null,
                    1 => children[0] with { Ratio = container.SizeRatio },
                    _ => new ArrangementNode(container.SizeRatio, container.Layout.Name, children),
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// A stable hash of a title, the same one the session file uses.
    /// </summary>
    /// <remarks>
    /// Stable across processes, so an arrangement saved today tells two windows of one
    /// program apart tomorrow; see <see cref="SessionStore"/> for why that took a hash
    /// of its own. A hash rather than the title, because titles are document names and
    /// URLs.
    /// </remarks>
    public static int HashTitle(string? title) => SessionStore.HashTitle(title ?? string.Empty);
}

/// <summary>
/// The arrangements file: every recorded tree, by name.
/// </summary>
/// <remarks>
/// Kept beside the session file and written the same way - a temporary file moved
/// into place, so a crash midway cannot leave a truncated file that fails to parse.
/// Loaded once when the window manager starts and rewritten on every save or delete;
/// the file is small and the operations are rare.
/// </remarks>
public sealed class ArrangementStore
{
    public const int CurrentVersion = 1;

    private readonly string _path;
    private readonly List<Arrangement> _arrangements = [];

    public ArrangementStore(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>Where the file lives unless told otherwise, beside the session file.</summary>
    public static string DefaultPath =>
        Path.Combine(Path.GetDirectoryName(SessionStore.DefaultPath) ?? string.Empty, "arrangements.json");

    /// <summary>The file's location.</summary>
    public string FilePath => _path;

    /// <summary>Every recorded arrangement, in the order they were first saved.</summary>
    public IReadOnlyList<Arrangement> All => _arrangements;

    /// <summary>The arrangement of that name, or null. Names are compared without case.</summary>
    public Arrangement? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (Arrangement arrangement in _arrangements)
            if (string.Equals(arrangement.Name, name, StringComparison.OrdinalIgnoreCase)) return arrangement;

        return null;
    }

    /// <summary>Records an arrangement, replacing one of the same name in place.</summary>
    public void Put(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);

        for (int i = 0; i < _arrangements.Count; i++)
        {
            if (string.Equals(_arrangements[i].Name, arrangement.Name, StringComparison.OrdinalIgnoreCase))
            {
                _arrangements[i] = arrangement;
                return;
            }
        }

        _arrangements.Add(arrangement);
    }

    /// <summary>Forgets an arrangement. False if there was none of that name.</summary>
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _arrangements.RemoveAll(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>
    /// Reads the file. A missing file is empty; a corrupt or foreign one is reported
    /// and treated as empty rather than failing the window manager.
    /// </summary>
    public void Load()
    {
        _arrangements.Clear();

        try
        {
            if (!File.Exists(_path)) return;

            ArrangementFile? file = JsonSerializer.Deserialize(
                File.ReadAllText(_path), ArrangementJsonContext.Default.ArrangementFile);

            if (file is null) return;

            if (file.Version != CurrentVersion)
            {
                Log.Warn(LogCategory.Wm, $"ignoring arrangements file version {file.Version}; expected {CurrentVersion}");
                return;
            }

            _arrangements.AddRange(file.Arrangements);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn(LogCategory.Wm, $"could not read arrangements: {ex.Message}");
        }
    }

    /// <summary>Writes the file. False, and a log line, if it could not be.</summary>
    public bool Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(
                new ArrangementFile(CurrentVersion, _arrangements), ArrangementJsonContext.Default.ArrangementFile);

            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error(LogCategory.Wm, "could not save arrangements", ex);
            return false;
        }
    }
}
