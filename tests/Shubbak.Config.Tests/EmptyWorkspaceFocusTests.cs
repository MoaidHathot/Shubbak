using Shubbak.Core.Wm;

namespace Shubbak.Config.Tests;

/// <summary>
/// Where the keyboard rests while the workspace being looked at is empty.
/// </summary>
/// <remarks>
/// <para>
/// The system's foreground has to be somewhere, and Windows hands it back to whatever
/// had it when the next thing to take it - a launcher - lets go. Left on the desktop,
/// which is never chosen as that fallback, it went instead to whatever was displayed
/// on the other monitor, and the application the launcher started opened there.
/// </para>
/// <para>
/// Held on a window of Shubbak's own it comes back to the monitor the user is looking
/// at. The desktop remains available for anyone who would rather Shubbak owned no
/// visible window at all.
/// </para>
/// </remarks>
public sealed class EmptyWorkspaceFocusTests
{
    [Fact]
    public void HoldingIsTheDefault()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { }");

        Assert.Equal(EmptyWorkspaceFocus.Hold, result.Config.EmptyWorkspaceFocus);
    }

    [Fact]
    public void TheDefaultSurvivesTheSectionBeingAbsent()
    {
        ConfigLoadResult result = ConfigLoader.Load("animation { }");

        Assert.Equal(EmptyWorkspaceFocus.Hold, result.Config.EmptyWorkspaceFocus);
    }

    [Theory]
    [InlineData("hold", EmptyWorkspaceFocus.Hold)]
    [InlineData("desktop", EmptyWorkspaceFocus.Desktop)]
    [InlineData("HOLD", EmptyWorkspaceFocus.Hold)]
    [InlineData("Desktop", EmptyWorkspaceFocus.Desktop)]
    public void BothAnswersCanBeAskedFor(string written, EmptyWorkspaceFocus expected)
    {
        ConfigLoadResult result = ConfigLoader.Load($"general {{ empty-workspace-focus \"{written}\" }}");

        Assert.False(result.HasErrors);
        Assert.Equal(expected, result.Config.EmptyWorkspaceFocus);
    }

    [Fact]
    public void AnUnknownAnswerIsAnErrorRatherThanASilentDefault()
    {
        // The two behaviours are indistinguishable until a window opens on the wrong
        // monitor, which is exactly the situation a typo should not quietly choose.
        ConfigLoadResult result = ConfigLoader.Load("general { empty-workspace-focus \"sink\" }");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0453");
        Assert.Equal(EmptyWorkspaceFocus.Hold, result.Config.EmptyWorkspaceFocus);
    }

    [Fact]
    public void ItIsAKnownSetting()
    {
        // The unknown-setting warning is on by default, so a setting added to the
        // loader without being added to the known list is reported to everyone who
        // adopts it.
        ConfigLoadResult result = ConfigLoader.Load("general { empty-workspace-focus \"desktop\" }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SHB0428");
    }
}
