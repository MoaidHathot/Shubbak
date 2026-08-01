namespace Shubbak.Config.Tests;

/// <summary>
/// Tests for config file location.
/// </summary>
/// <remarks>
/// Environment variables are process-wide, so these are serialised into one xUnit
/// collection and every variable touched is restored afterwards. Leaking one would
/// make an unrelated test fail in a way that looks like a real bug.
/// </remarks>
[Collection("ConfigPath")]
public sealed class ConfigPathResolverTests : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, string?> _saved = [];

    public ConfigPathResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"shubbak-xdg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        foreach (string name in (string[])["SHUBBAK_CONFIG", "XDG_CONFIG_HOME", "XDG_CONFIG_DIRS"])
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in _saved)
            Environment.SetEnvironmentVariable(name, value);

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Creates a config file under a root and returns its path.</summary>
    private static string WriteConfig(string root, string content = "gaps { inner 4 }")
    {
        string directory = Path.Combine(root, ConfigPathResolver.DirectoryName);
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, ConfigPathResolver.FileName);
        File.WriteAllText(path, content);

        return path;
    }

    [Fact]
    public void AnExplicitPathWinsOverEverything()
    {
        string other = WriteConfig(_root);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);

        string chosen = Path.Combine(_root, "elsewhere.kdl");
        File.WriteAllText(chosen, "gaps { inner 8 }");

        ConfigLocation location = ConfigPathResolver.Resolve(chosen);

        Assert.Equal(ConfigOrigin.CommandLine, location.Origin);
        Assert.Equal(chosen, location.Path);
        Assert.NotEqual(other, location.Path);
    }

    [Fact]
    public void AnExplicitPathIsReturnedEvenIfMissing()
    {
        // Silently ignoring a path the user typed and running a different config is
        // the worst possible behaviour: everything appears to work, but not as asked.
        string missing = Path.Combine(_root, "does-not-exist.kdl");

        ConfigLocation location = ConfigPathResolver.Resolve(missing);

        Assert.Equal(missing, location.Path);
        Assert.Equal(ConfigOrigin.CommandLine, location.Origin);
    }

    [Fact]
    public void XdgConfigHomeIsHonoured()
    {
        string expected = WriteConfig(_root);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);

        ConfigLocation location = ConfigPathResolver.Resolve();

        Assert.True(location.Found);
        Assert.Equal(expected, location.Path);
        Assert.Equal(ConfigOrigin.XdgConfigHome, location.Origin);
    }

    [Fact]
    public void XdgPathsWithRelativeSegmentsAndForwardSlashesAreNormalised()
    {
        // Exactly the author's own value:
        //   XDG_CONFIG_HOME = P:\Github\Neovim-Moaid\configurations/../config/
        string expected = WriteConfig(_root);

        string awkward = Path.Combine(_root, "sub") + "/../";
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", awkward);

        ConfigLocation location = ConfigPathResolver.Resolve();

        Assert.True(location.Found);
        Assert.Equal(expected, location.Path);
    }

    [Fact]
    public void ShubbakConfigWinsOverXdg()
    {
        // The dedicated variable is the more specific statement of intent.
        string xdg = WriteConfig(_root);

        string dedicated = Path.Combine(_root, "dedicated.kdl");
        File.WriteAllText(dedicated, "gaps { inner 8 }");

        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);
        Environment.SetEnvironmentVariable("SHUBBAK_CONFIG", dedicated);

        ConfigLocation location = ConfigPathResolver.Resolve();

        Assert.Equal(dedicated, location.Path);
        Assert.Equal(ConfigOrigin.Environment, location.Origin);
        Assert.NotEqual(xdg, location.Path);
    }

    [Fact]
    public void ShubbakConfigMayNameTheDirectoryRatherThanTheFile()
    {
        // Both readings are natural, and guessing wrong just means the tool claims
        // there is no config while the user is looking straight at it.
        string expected = WriteConfig(_root);

        Environment.SetEnvironmentVariable(
            "SHUBBAK_CONFIG", Path.Combine(_root, ConfigPathResolver.DirectoryName));

        ConfigLocation location = ConfigPathResolver.Resolve();

        Assert.True(location.Found);
        Assert.Equal(expected, location.Path);
    }

    [Fact]
    public void XdgConfigDirsIsSearchedAfterXdgConfigHome()
    {
        string second = Path.Combine(_root, "second");
        Directory.CreateDirectory(second);

        string expected = WriteConfig(second);

        // XDG_CONFIG_HOME points somewhere with no config in it.
        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", empty);
        Environment.SetEnvironmentVariable("XDG_CONFIG_DIRS", second);

        ConfigLocation location = ConfigPathResolver.Resolve();

        Assert.True(location.Found);
        Assert.Equal(expected, location.Path);
        Assert.Equal(ConfigOrigin.XdgConfigDirs, location.Origin);
    }

    [Fact]
    public void XdgConfigDirsAcceptsSemicolonSeparatedWindowsPaths()
    {
        // The specification says ':', but a Windows path contains a colon after the
        // drive letter, so ';' is what anyone setting this on Windows will use.
        string second = Path.Combine(_root, "second");
        Directory.CreateDirectory(second);

        string expected = WriteConfig(second);

        Environment.SetEnvironmentVariable(
            "XDG_CONFIG_DIRS", $@"C:\nowhere-{Guid.NewGuid():N};{second}");

        ConfigLocation location = ConfigPathResolver.Resolve();

        Assert.True(location.Found);
        Assert.Equal(expected, location.Path);
    }

    [Fact]
    public void NothingFoundListsEverywhereThatWasSearched()
    {
        // "No config file" is useless when the file is sitting right there and the
        // search simply looked elsewhere - the usual situation with dotfiles on a
        // separate drive.
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "nowhere"));

        ConfigLocation location = ConfigPathResolver.Resolve();

        Assert.False(location.Found);
        Assert.NotEmpty(location.Searched);

        string description = location.DescribeSearch();

        Assert.Contains("Looked in:", description, StringComparison.Ordinal);
        Assert.Contains("XDG_CONFIG_HOME", description, StringComparison.Ordinal);
        Assert.Contains("nowhere", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSearchListRecordsCandidatesInOrder()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "a"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_DIRS", Path.Combine(_root, "b"));

        ConfigLocation location = ConfigPathResolver.Resolve();

        Assert.Contains(location.Searched, p => p.Contains(@"a\shubbak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(location.Searched, p => p.Contains(@"b\shubbak", StringComparison.OrdinalIgnoreCase));

        int xdgHome = location.Searched.ToList()
            .FindIndex(p => p.Contains(@"a\shubbak", StringComparison.OrdinalIgnoreCase));
        int xdgDirs = location.Searched.ToList()
            .FindIndex(p => p.Contains(@"b\shubbak", StringComparison.OrdinalIgnoreCase));

        Assert.True(xdgHome < xdgDirs, "XDG_CONFIG_HOME must be searched before XDG_CONFIG_DIRS");
    }

    [Fact]
    public void EnvironmentVariablesInsidePathsAreExpanded()
    {
        string expected = WriteConfig(_root);

        Environment.SetEnvironmentVariable("SHUBBAK_TEST_ROOT", _root);

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "%SHUBBAK_TEST_ROOT%");

            ConfigLocation location = ConfigPathResolver.Resolve();

            Assert.True(location.Found);
            Assert.Equal(expected, location.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHUBBAK_TEST_ROOT", null);
        }
    }

    [Fact]
    public void MalformedVariablesDoNotStopTheSearch()
    {
        string expected = WriteConfig(_root);

        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "\0not|a<valid>path");
        Environment.SetEnvironmentVariable("XDG_CONFIG_DIRS", _root);

        ConfigLocation location = ConfigPathResolver.Resolve();

        Assert.True(location.Found);
        Assert.Equal(expected, location.Path);
    }

    [Fact]
    public void DefaultWriteLocationPrefersXdg()
    {
        // A generated config should land alongside the user's other configs, not in
        // a second place they then have to find and move.
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);

        string path = ConfigPathResolver.DefaultWriteLocation();

        Assert.StartsWith(_root, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(ConfigPathResolver.FileName, path, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultWriteLocationFallsBackToUserConfig()
    {
        string path = ConfigPathResolver.DefaultWriteLocation();

        Assert.Contains(".config", path, StringComparison.Ordinal);
        Assert.EndsWith(ConfigPathResolver.FileName, path, StringComparison.Ordinal);
    }

    [Fact]
    public void AResolvedConfigActuallyLoads()
    {
        // End to end: the resolver and the loader agree about what a config file is.
        WriteConfig(_root, "gaps { inner 7 }");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _root);

        ConfigLocation location = ConfigPathResolver.Resolve();
        ConfigLoadResult result = ConfigLoader.LoadFile(location.Path!);

        Assert.False(result.HasErrors);
        Assert.Equal(7, result.Config.InnerGap);
    }
}

/// <summary>
/// Serialises the config-path tests, which manipulate process-wide environment
/// variables.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit collection definition naming convention.")]
[CollectionDefinition("ConfigPath", DisableParallelization = true)]
public sealed class ConfigPathTestCollection;
