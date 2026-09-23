using Dalil.Core;
using Shubbak.Core.Diagnostics;

namespace Dalil;

/// <summary>
/// The <see cref="Frecency"/> record on disk, beside the logs and the first-open marker.
/// </summary>
/// <remarks>
/// Read once at startup and written whole after every run, which is a few hundred
/// bytes a few times an hour. A file that cannot be read starts the record afresh
/// rather than stopping the palette; one that cannot be written is said once in the
/// log and the palette carries on remembering for the session.
/// </remarks>
internal sealed class FrecencyStore
{
    private static readonly string s_path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shubbak",
        "dalil-frecency.tsv");

    private readonly string _path;
    private bool _warnedOfWrite;

    private FrecencyStore(string path, Frecency record)
    {
        _path = path;
        Record = record;
    }

    /// <summary>What has been run, weighted towards lately.</summary>
    public Frecency Record { get; }

    /// <summary>The store in the state directory, read now.</summary>
    public static FrecencyStore Open() => Open(s_path);

    /// <summary>The store at <paramref name="path"/>, read now.</summary>
    public static FrecencyStore Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            if (File.Exists(path)) return new FrecencyStore(path, Frecency.Parse(File.ReadAllText(path)));
        }
        catch (IOException ex)
        {
            Log.Warn(LogCategory.Wm, $"could not read {path}; starting the command history afresh: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn(LogCategory.Wm, $"could not read {path}; starting the command history afresh: {ex.Message}");
        }

        return new FrecencyStore(path, new Frecency());
    }

    /// <summary>Notes a run and writes the record.</summary>
    public void Ran(string key)
    {
        Record.Record(key, DateTimeOffset.UtcNow);
        Save();
    }

    private void Save()
    {
        try
        {
            if (Path.GetDirectoryName(_path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            // Whole and then renamed, so a crash mid-write leaves the last good record
            // rather than half of this one.
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, Record.Serialise());
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_warnedOfWrite) return;

            _warnedOfWrite = true;
            Log.Warn(LogCategory.Wm, $"could not write {_path}; the command history will not survive this session: {ex.Message}");
        }
    }
}
