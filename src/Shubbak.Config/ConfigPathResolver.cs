using Shubbak.Core.Diagnostics;

namespace Shubbak.Config;

/// <summary>Where a config file was found, and what else was tried.</summary>
/// <param name="Path">The file, or <see langword="null"/> if none was found.</param>
/// <param name="Origin">Which rule produced it.</param>
/// <param name="Searched">Every location considered, in order.</param>
public readonly record struct ConfigLocation(
    string? Path,
    ConfigOrigin Origin,
    IReadOnlyList<string> Searched)
{
    public bool Found => Path is not null;

    /// <summary>A message listing everywhere that was looked.</summary>
    /// <remarks>
    /// Printed when nothing is found. "No config file" is a useless thing to be told
    /// when the file is sitting right there and the manager simply looked somewhere
    /// else - which is the usual situation with dotfiles on a separate drive.
    /// </remarks>
    public string DescribeSearch()
    {
        var output = new System.Text.StringBuilder();
        output.AppendLine("No config file found. Looked in:");

        foreach (string candidate in Searched)
            output.Append("  ").AppendLine(candidate);

        output.AppendLine();
        output.AppendLine("Set SHUBBAK_CONFIG, set XDG_CONFIG_HOME, or pass --config <path>.");

        return output.ToString();
    }
}

/// <summary>Which rule found the config.</summary>
public enum ConfigOrigin
{
    None,

    /// <summary>An explicit <c>--config</c> argument.</summary>
    CommandLine,

    /// <summary>The <c>SHUBBAK_CONFIG</c> environment variable.</summary>
    Environment,

    /// <summary>Under <c>XDG_CONFIG_HOME</c>.</summary>
    XdgConfigHome,

    /// <summary>Under an entry of <c>XDG_CONFIG_DIRS</c>.</summary>
    XdgConfigDirs,

    /// <summary>The conventional <c>~/.config</c> location.</summary>
    UserConfig,

    /// <summary>The Windows-native <c>%APPDATA%</c> location.</summary>
    AppData,
}

/// <summary>
/// Finds Shubbak's config file.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the window manager, the CLI and the bar. Each having its own copy is how
/// you end up with a CLI that validates one file while the daemon runs another - a
/// confusing failure, because both are behaving exactly as written.
/// </para>
/// <para>
/// <b>XDG is honoured on Windows.</b> The specification is nominally Unix, but people
/// who keep dotfiles in a repository and symlink them per machine set
/// <c>XDG_CONFIG_HOME</c> on Windows too, and every tool that ignores it needs its own
/// bespoke environment variable instead. Supporting it costs nothing and removes that.
/// </para>
/// </remarks>
public static class ConfigPathResolver
{
    /// <summary>The directory name used under each config root.</summary>
    public const string DirectoryName = "shubbak";

    /// <summary>The config file name.</summary>
    public const string FileName = "shubbak.kdl";

    /// <summary>
    /// Locates the config file.
    /// </summary>
    /// <param name="explicitPath">A <c>--config</c> argument, if given.</param>
    /// <remarks>
    /// <para>Search order, first match wins:</para>
    /// <list type="number">
    ///   <item><paramref name="explicitPath"/></item>
    ///   <item><c>SHUBBAK_CONFIG</c> - a full path to the file</item>
    ///   <item><c>XDG_CONFIG_HOME/shubbak/shubbak.kdl</c></item>
    ///   <item>each entry of <c>XDG_CONFIG_DIRS</c></item>
    ///   <item><c>~/.config/shubbak/shubbak.kdl</c></item>
    ///   <item><c>%APPDATA%/shubbak/shubbak.kdl</c></item>
    /// </list>
    /// <para>
    /// An explicit path is returned even when the file does not exist, so the caller
    /// can report "the file you named is missing" rather than silently falling back
    /// to a different config - which would be far more confusing.
    /// </para>
    /// </remarks>
    public static ConfigLocation Resolve(string? explicitPath = null)
    {
        List<string> searched = [];

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string full = Normalise(explicitPath);
            searched.Add(full);

            // Returned whether or not it exists: silently ignoring a path the user
            // typed and running something else is the worst possible behaviour.
            return new ConfigLocation(full, ConfigOrigin.CommandLine, searched);
        }

        if (Environment.GetEnvironmentVariable("SHUBBAK_CONFIG") is { Length: > 0 } fromEnvironment)
        {
            string full = Normalise(fromEnvironment);

            // SHUBBAK_CONFIG may name either the file or the directory holding it.
            // Both readings are natural, so both are accepted.
            if (Directory.Exists(full)) full = Path.Combine(full, FileName);

            searched.Add(full);

            if (File.Exists(full))
                return new ConfigLocation(full, ConfigOrigin.Environment, searched);
        }

        if (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdgHome)
        {
            string candidate = Normalise(Path.Combine(xdgHome, DirectoryName, FileName));
            searched.Add(candidate);

            if (File.Exists(candidate))
                return new ConfigLocation(candidate, ConfigOrigin.XdgConfigHome, searched);
        }

        if (Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS") is { Length: > 0 } xdgDirs)
        {
            foreach (string directory in SplitSearchPath(xdgDirs))
            {
                string candidate = Normalise(Path.Combine(directory, DirectoryName, FileName));
                searched.Add(candidate);

                if (File.Exists(candidate))
                    return new ConfigLocation(candidate, ConfigOrigin.XdgConfigDirs, searched);
            }
        }

        string userConfig = Normalise(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", DirectoryName, FileName));

        searched.Add(userConfig);

        if (File.Exists(userConfig))
            return new ConfigLocation(userConfig, ConfigOrigin.UserConfig, searched);

        string appData = Normalise(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            DirectoryName, FileName));

        searched.Add(appData);

        if (File.Exists(appData))
            return new ConfigLocation(appData, ConfigOrigin.AppData, searched);

        return new ConfigLocation(null, ConfigOrigin.None, searched);
    }

    /// <summary>
    /// Resolves and logs where the config came from.
    /// </summary>
    /// <remarks>
    /// Recording the origin is worth the line it costs: "which config is actually
    /// loaded?" is a question that comes up constantly on a machine with dotfiles in
    /// one place and a stale copy in another.
    /// </remarks>
    public static ConfigLocation ResolveAndLog(string? explicitPath = null)
    {
        ConfigLocation location = Resolve(explicitPath);

        if (location.Found)
            Log.Info(LogCategory.Config, $"config: {location.Path} (via {location.Origin})");
        else
            Log.Warn(LogCategory.Config, "no config file found; using defaults");

        return location;
    }

    /// <summary>
    /// Where a new config should be written.
    /// </summary>
    /// <remarks>
    /// Prefers <c>XDG_CONFIG_HOME</c> when it is set, so a generated file lands
    /// alongside the user's other configs rather than in a second location they then
    /// have to find and move.
    /// </remarks>
    public static string DefaultWriteLocation()
    {
        if (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdgHome)
            return Normalise(Path.Combine(xdgHome, DirectoryName, FileName));

        return Normalise(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", DirectoryName, FileName));
    }

    /// <summary>
    /// Splits a search-path variable into directories.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The XDG specification separates entries with <c>:</c>, which cannot work on
    /// Windows: <c>C:\Users\me</c> would split at the drive letter into <c>C</c> and
    /// <c>\Users\me</c>, and the search would silently look in the wrong place on the
    /// current drive.
    /// </para>
    /// <para>
    /// So <c>;</c> is the separator - matching <c>PATH</c>, and what anyone setting
    /// this on Windows will reach for. A colon is only treated as a separator when
    /// the value contains no <c>;</c> and no drive letter, which is the case for a
    /// value copied verbatim from a Unix machine.
    /// </para>
    /// </remarks>
    private static string[] SplitSearchPath(string value)
    {
        const StringSplitOptions Options =
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;

        if (value.Contains(';', StringComparison.Ordinal) || LooksLikeWindowsPath(value))
            return value.Split(';', Options);

        return value.Split(':', Options);
    }

    /// <summary>True when the value contains a drive-letter prefix.</summary>
    private static bool LooksLikeWindowsPath(string value)
    {
        for (int i = 1; i < value.Length; i++)
        {
            if (value[i] != ':') continue;
            if (char.IsAsciiLetter(value[i - 1]) && (i == 1 || !char.IsAsciiLetterOrDigit(value[i - 2])))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Cleans up a path.
    /// </summary>
    /// <remarks>
    /// XDG variables are frequently set with forward slashes and relative segments -
    /// the author's own resolves through <c>configurations/../config/</c> - so paths
    /// are normalised before use rather than being compared or printed raw.
    /// </remarks>
    private static string Normalise(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A malformed variable should not stop the search; the caller will simply
            // find no file there.
            return path;
        }
    }
}
