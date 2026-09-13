namespace Shubbak.Config.Tests;

/// <summary>
/// Whether tools may edit the configuration file over the pipe, and whether saving it
/// reloads it.
/// </summary>
/// <remarks>
/// Both on by default, and the asymmetry with <c>allow-shell-exec-over-ipc</c> is the
/// point of the first test: the pipe is scoped to the account, and every process
/// running as the user can already open the file and write to it, so refusing to do it
/// on their behalf would protect nothing. What is gated is narrow - rules, and nothing
/// else - and gated on the one thing the pipe adds, which is doing it for you.
/// </remarks>
public sealed class ConfigEditsOverIpcTests
{
    [Fact]
    public void EditsArePermittedByDefault()
    {
        Assert.True(ConfigLoader.Load("general { }").Config.AllowConfigEditsOverIpc);
        Assert.True(ShubbakConfig.Default.AllowConfigEditsOverIpc);
    }

    [Theory]
    [InlineData("general { allow-config-edits-over-ipc #false }")]
    [InlineData("general allow-config-edits-over-ipc=#false { }")]
    public void EditsCanBeRefused(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(result.HasErrors);
        Assert.False(result.Config.AllowConfigEditsOverIpc);
    }

    [Fact]
    public void ReloadingOnSaveIsOnByDefault()
    {
        Assert.True(ConfigLoader.Load("general { }").Config.ReloadOnSave);
    }

    [Theory]
    [InlineData("general { reload-on-save #false }")]
    [InlineData("general reload-on-save=#false { }")]
    public void ReloadingOnSaveCanBeTurnedOff(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(result.HasErrors);
        Assert.False(result.Config.ReloadOnSave);
    }

    [Fact]
    public void BothAreKnownSettings()
    {
        // Otherwise the loader would warn that they will be ignored, which for a
        // setting it then honours is the more confusing of the two mistakes.
        ConfigLoadResult result = ConfigLoader.Load(
            "general { allow-config-edits-over-ipc #false; reload-on-save #false }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code is "SHB0428");
    }

    [Fact]
    public void ANonBooleanIsReported()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { reload-on-save \"soon\" }");

        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0419");
        Assert.True(result.Config.ReloadOnSave, "the default stands when the value is nonsense");
    }
}
