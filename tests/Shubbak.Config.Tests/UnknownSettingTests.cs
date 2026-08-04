namespace Shubbak.Config.Tests;

/// <summary>
/// What the loader says about names it does not recognise.
/// </summary>
/// <remarks>
/// <para>
/// Nothing checked that a section or a setting was a name the loader knew. A
/// misspelled one was discarded in perfect silence and <c>check-config</c> reported
/// "ok", so the user was left reading the documentation for a setting they believed
/// they had already written correctly.
/// </para>
/// <para>
/// Warnings rather than errors, so loading stays total: a config with one typo still
/// produces a usable window manager rather than none.
/// </para>
/// </remarks>
public sealed class UnknownSettingTests
{
    [Fact]
    public void AnUnknownSectionIsReported()
    {
        ConfigLoadResult result = ConfigLoader.Load("genral { }");

        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0427");
    }

    [Fact]
    public void AnUnknownSectionIsOnlyAWarning()
    {
        // The rest of the file still has to load.
        ConfigLoadResult result = ConfigLoader.Load("""
            animations { }
            gaps { inner 8 }
            """);

        Assert.False(result.HasErrors);
        Assert.Equal(8, result.Config.InnerGap);
    }

    [Fact]
    public void TheSuggestionNamesWhatWasProbablyMeant()
    {
        ConfigLoadResult result = ConfigLoader.Load("window_effects { }");

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0427");

        Assert.Contains("window-effects", warning.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("general { focus-follows-mouse #true }")]
    [InlineData("animation { enable #true }")]
    [InlineData("gaps { innner 8 }")]
    [InlineData("window-effects { focussed-colour \"#fff\" }")]
    [InlineData("logging { levl \"debug\" }")]
    public void AnUnknownSettingIsReported(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0428");
    }

    [Theory]
    [InlineData("general { focus-follows-cursor #true }")]
    [InlineData("general { allow-shell-exec-over-ipc #true }")]
    [InlineData("animation { animate-new-windows #true }")]
    [InlineData("animation { window-open duration=100 curve=\"ease-out\" }")]
    [InlineData("gaps { inner 8; outer 8 }")]
    [InlineData("window-effects { border #true }")]
    [InlineData("logging { level \"debug\" }")]
    public void EverySettingTheLoaderActuallyReadsIsAccepted(string source)
    {
        // The list of known names has to keep up with the reader, or this becomes a
        // machine for reporting settings that work perfectly well.
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "SHB0427" or "SHB0428");
    }

    [Fact]
    public void TheShippedExampleUsesNoUnknownNames()
    {
        // The example is what everyone starts from, so it must not itself trip the
        // check that exists to catch typos.
        string path = Path.Combine(FindRepositoryRoot(), "docs", "shubbak.example.kdl");

        if (!File.Exists(path)) return;

        ConfigLoadResult result = ConfigLoader.Load(File.ReadAllText(path));

        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "SHB0427" or "SHB0428");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Shubbak.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
