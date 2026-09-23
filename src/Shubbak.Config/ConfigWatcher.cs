namespace Shubbak.Config;

/// <summary>
/// What a file looked like at a moment: enough to tell a save the daemon has already
/// loaded from one it has not.
/// </summary>
/// <remarks>
/// Taken before the file is read, never after. Taken after, a save landing between
/// the read and the stamp would be stamped as loaded while the running configuration
/// was the older one, and the watcher would then skip the very change it exists to
/// catch. Taken before, the same race produces one reload too many, which costs a
/// log line.
/// </remarks>
public readonly record struct ConfigStamp(long Length, long LastWriteUtcTicks)
{
    /// <summary>The file as it stands, or the empty stamp when it cannot be read.</summary>
    public static ConfigStamp Of(string path)
    {
        try
        {
            var info = new FileInfo(path);

            return info.Exists ? new ConfigStamp(info.Length, info.LastWriteTimeUtc.Ticks) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}

/// <summary>
/// Says when the configuration file has been saved, once per save rather than once
/// per write.
/// </summary>
/// <remarks>
/// <para>
/// A directory notification, not a poll: Windows tells the process when something in
/// the folder changes, and a daemon nobody is editing pays nothing between saves. The
/// folder rather than the file, because most editors do not write the file - they
/// write a temporary one beside it and rename it into place, and a watch held on the
/// old file would be holding a handle to something that no longer has the name.
/// </para>
/// <para>
/// One save is several notifications - a create, some writes, a rename, an attribute
/// change - spread over a few milliseconds, and an editor writing in place may be read
/// half-written between two of them. So the callback waits for the notifications to
/// stop: every notification restarts the wait, and only the last one to arrive fires.
/// The reload then reads a file that has settled.
/// </para>
/// <para>
/// The callback arrives on a thread-pool thread. The daemon marshals it onto its own
/// loop, which is where the running configuration lives.
/// </para>
/// </remarks>
public sealed class ConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly string _fileName;
    private readonly TimeSpan _settle;
    private readonly Action _saved;

    private int _version;
    private bool _disposed;

    /// <summary>Starts watching a file.</summary>
    /// <param name="path">The file to watch; its folder must exist.</param>
    /// <param name="settle">How long the folder has to be quiet before a save is announced.</param>
    /// <param name="saved">What to do about it, on a thread-pool thread.</param>
    /// <exception cref="ArgumentException">The path has no folder.</exception>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    public ConfigWatcher(string path, TimeSpan settle, Action saved)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(saved);

        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full) ?? throw new ArgumentException("The path has no folder.", nameof(path));

        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"No such folder: {directory}");

        _fileName = Path.GetFileName(full);
        _settle = settle;
        _saved = saved;

        _watcher = new FileSystemWatcher(directory)
        {
            // The file's own name. A rename onto it from a temporary name is reported
            // under the new name, so the write-rename editors are caught by this filter
            // too - and everything else in the folder is never heard about.
            Filter = _fileName,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
        };

        _watcher.Changed += OnSomething;
        _watcher.Created += OnSomething;
        _watcher.Deleted += OnSomething;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;

        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>The file being watched, as the watcher spells it.</summary>
    public string FileName => _fileName;

    private void OnSomething(object sender, FileSystemEventArgs e) => Touch();

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // A rename onto the name is a save; a rename away from it is the file going.
        // Either way the file the daemon loaded is not the file with the name now.
        if (Matches(e.Name) || Matches(e.OldName)) Touch();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // The buffer overflowed and notifications were lost. Something happened in the
        // folder, and the honest response is the same as to any notification: look.
        Touch();
    }

    private bool Matches(string? name) =>
        name is not null && string.Equals(Path.GetFileName(name), _fileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Restarts the wait; the last notification to arrive is the one that fires.</summary>
    private void Touch()
    {
        if (_disposed) return;

        int version = Interlocked.Increment(ref _version);

        _ = Task.Delay(_settle).ContinueWith(
            _ =>
            {
                if (_disposed || Volatile.Read(ref _version) != version) return;

                _saved();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
