namespace Shubbak.Wm.Tests;

/// <summary>
/// The watcher that turns a save of the configuration file into a reload.
/// </summary>
/// <remarks>
/// <para>
/// Real folders and real notifications, because the two things worth knowing about a
/// file watcher cannot be known any other way: that an editor's write-rename is heard
/// under the filter for the file's own name, and that a burst of notifications from
/// one save arrives as one callback rather than five.
/// </para>
/// <para>
/// Each test gets its own folder under the temp directory and deletes it afterwards.
/// </para>
/// </remarks>
public sealed class ConfigWatcherTests : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"shubbak-watch-{Guid.NewGuid():N}");
    private readonly string _path;

    public ConfigWatcherTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "shubbak.kdl");
        File.WriteAllText(_path, "general { }\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task AWriteInPlaceIsHeard()
    {
        int saves = 0;
        using var gate = new SemaphoreSlim(0);
        using var watcher = new ConfigWatcher(_path, Settle, () => { Interlocked.Increment(ref saves); gate.Release(); });

        await File.WriteAllTextAsync(_path, "general { follow-window-on-move #true }\n");

        Assert.True(await gate.WaitAsync(Patience), "the save was never announced");
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task AWriteRenameIsHeardUnderTheFilesOwnName()
    {
        // What most editors do: write a temporary file beside the target and rename it
        // into place. The watcher filters on the file's name, and a rename onto that
        // name has to count.
        int saves = 0;
        using var gate = new SemaphoreSlim(0);
        using var watcher = new ConfigWatcher(_path, Settle, () => { Interlocked.Increment(ref saves); gate.Release(); });

        string temporary = Path.Combine(_folder, "shubbak.kdl.tmp");
        await File.WriteAllTextAsync(temporary, "general { keep-in-taskbar #false }\n");
        File.Move(temporary, _path, overwrite: true);

        Assert.True(await gate.WaitAsync(Patience), "the rename onto the file was never announced");
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task ABurstOfWritesIsOneSave()
    {
        // One save is several notifications spread over a few milliseconds. Announced
        // per notification, the daemon would reload the same file five times and read
        // it half-written on the way.
        int saves = 0;
        using var gate = new SemaphoreSlim(0);
        using var watcher = new ConfigWatcher(_path, Settle, () => { Interlocked.Increment(ref saves); gate.Release(); });

        for (int i = 0; i < 5; i++)
        {
            await File.AppendAllTextAsync(_path, $"// {i}\n");
            await Task.Delay(10);
        }

        Assert.True(await gate.WaitAsync(Patience), "the burst was never announced");

        // Nothing more arrives once the folder has been quiet.
        Assert.False(await gate.WaitAsync(Settle * 3), "the burst was announced more than once");
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task OtherFilesInTheFolderAreNotHeard()
    {
        // The folder may be a dotfiles repository with a great deal going on. Only the
        // configuration file is anybody's business here.
        int saves = 0;
        using var gate = new SemaphoreSlim(0);
        using var watcher = new ConfigWatcher(_path, Settle, () => { Interlocked.Increment(ref saves); gate.Release(); });

        await File.WriteAllTextAsync(Path.Combine(_folder, "notes.md"), "# hello\n");
        await File.WriteAllTextAsync(Path.Combine(_folder, "other.kdl"), "general { }\n");

        Assert.False(await gate.WaitAsync(Settle * 3), "a neighbouring file was announced as a save");
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task NothingIsHeardAfterDisposal()
    {
        int saves = 0;
        using var gate = new SemaphoreSlim(0);
        var watcher = new ConfigWatcher(_path, Settle, () => { Interlocked.Increment(ref saves); gate.Release(); });

        watcher.Dispose();

        await File.WriteAllTextAsync(_path, "general { follow-window-on-move #true }\n");

        Assert.False(await gate.WaitAsync(Settle * 3));
        Assert.Equal(0, saves);
    }

    [Fact]
    public void AMissingFolderIsRefusedUpFront()
    {
        // Rather than a watcher that silently watches nothing.
        Assert.Throws<DirectoryNotFoundException>(() =>
            new ConfigWatcher(Path.Combine(_folder, "nowhere", "shubbak.kdl"), Settle, () => { }));
    }

    [Fact]
    public void TheStampChangesWhenTheFileDoes()
    {
        ConfigStamp before = ConfigStamp.Of(_path);

        File.WriteAllText(_path, "general { }\n// more\n");

        Assert.NotEqual(before, ConfigStamp.Of(_path));
        Assert.Equal(ConfigStamp.Of(_path), ConfigStamp.Of(_path));
        Assert.Equal(default, ConfigStamp.Of(Path.Combine(_folder, "missing.kdl")));
    }
}
