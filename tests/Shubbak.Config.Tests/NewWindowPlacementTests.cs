using Shubbak.Core.Wm;

namespace Shubbak.Config.Tests;

/// <summary>
/// Which workspace a newly-managed window lands on.
/// </summary>
/// <remarks>
/// <para>
/// It used to be the active workspace of whichever monitor the window opened on,
/// reasoning that an application launched onto a secondary display should stay there
/// rather than teleporting to wherever focus happened to be.
/// </para>
/// <para>
/// That is right for an application that chooses its display deliberately and wrong
/// for the common case, because Windows reopens most applications wherever they were
/// last - which has nothing to do with where the user is now. Reported from use:
/// launching something while working on one monitor and finding it had opened on the
/// other.
/// </para>
/// <para>
/// The old behaviour is still available. The reasoning behind it was not wrong, only
/// wrong as a default.
/// </para>
/// </remarks>
public sealed class NewWindowPlacementTests
{
    [Fact]
    public void FollowingFocusIsTheDefault()
    {
        ConfigLoadResult result = ConfigLoader.Load("general { }");

        Assert.Equal(NewWindowPlacement.FollowFocus, result.Config.NewWindowPlacement);
    }

    [Fact]
    public void TheDefaultSurvivesTheSectionBeingAbsent()
    {
        ConfigLoadResult result = ConfigLoader.Load("animation { }");

        Assert.Equal(NewWindowPlacement.FollowFocus, result.Config.NewWindowPlacement);
    }

    [Theory]
    [InlineData("focus", NewWindowPlacement.FollowFocus)]
    [InlineData("window", NewWindowPlacement.FollowWindow)]
    [InlineData("FOCUS", NewWindowPlacement.FollowFocus)]
    [InlineData("Window", NewWindowPlacement.FollowWindow)]
    public void BothAnswersCanBeAskedFor(string written, NewWindowPlacement expected)
    {
        ConfigLoadResult result = ConfigLoader.Load($"general {{ new-window-placement \"{written}\" }}");

        Assert.False(result.HasErrors);
        Assert.Equal(expected, result.Config.NewWindowPlacement);
    }

    [Fact]
    public void AnUnknownAnswerIsAnErrorRatherThanASilentDefault()
    {
        // Silently falling back would leave someone who wrote "monitor" believing they
        // had asked for something, and the setting exists precisely because the two
        // behaviours are hard to tell apart until a window goes missing.
        ConfigLoadResult result = ConfigLoader.Load("general { new-window-placement \"monitor\" }");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0436");
    }

    [Fact]
    public void ItIsAKnownSetting()
    {
        // The unknown-setting warning is on by default, so a setting added to the
        // loader without being added to the known list is reported to everyone who
        // adopts it.
        ConfigLoadResult result = ConfigLoader.Load("general { new-window-placement \"focus\" }");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SHB0428");
    }
}
