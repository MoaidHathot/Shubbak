using Shubbak.Native;

namespace Shubbak.Native.Tests;

/// <summary>
/// Asking the shell whether now is a bad moment.
/// </summary>
/// <remarks>
/// <para>
/// <c>SHQueryUserNotificationState</c> is what Windows itself consults before showing
/// a toast, and a window manager wants the same answer for the same reason: a game or
/// a presentation is not a moment to be moving windows or claiming keystrokes.
/// </para>
/// <para>
/// Worth being blunt about the limit, because it decides how much can be built on
/// this. Direct3D exclusive fullscreen is reported reliably. Borderless windowed - how
/// most modern games actually run - is an ordinary window covering the screen and
/// reports as ordinary. No shell API distinguishes it, and the obvious geometric test,
/// a caption-less window the size of the monitor, now describes Shubbak's own
/// whole-monitor fullscreen exactly.
/// </para>
/// <para>
/// So these tests pin the shape of the answer rather than its value: what the call
/// does on a desktop with no game running, and that it never reports a state it has
/// not established.
/// </para>
/// </remarks>
public sealed class UserActivityTests
{
    [Fact]
    public void ItAnswersOnThisMachine()
    {
        // Not a property of the code. This asserts the P/Invoke signature is right and
        // the call is accepted by the Windows being built on - a wrong signature would
        // throw, and a rejected call would report Unknown forever while looking like a
        // deliberate answer.
        UserActivity activity = DisplayPreferences.CurrentActivity();

        Assert.NotEqual(UserActivity.Unknown, activity);
    }

    [Fact]
    public void AnOrdinaryDesktopIsReportedAsOrdinary()
    {
        // The test host is a console process on a normal desktop. If this starts
        // failing, either the mapping is wrong or the machine really is running a game
        // - and the assertion message should say which is being claimed.
        UserActivity activity = DisplayPreferences.CurrentActivity();

        Assert.True(
            activity is UserActivity.Ordinary or UserActivity.Presenting,
            $"the shell reports {activity} on what should be an ordinary desktop; " +
            "either the QUNS mapping is wrong or something full-screen is running");
    }

    [Fact]
    public void RepeatedCallsAgree()
    {
        // No caching, no handle, no state of its own: two calls a moment apart must
        // describe the same desktop. A difference here would mean the call is reading
        // something transient and cannot be reported as a fact.
        Assert.Equal(DisplayPreferences.CurrentActivity(), DisplayPreferences.CurrentActivity());
    }

    [Theory]
    [InlineData(UserActivity.FullScreenGame)]
    [InlineData(UserActivity.FullScreenApp)]
    [InlineData(UserActivity.Presenting)]
    public void TheStatesWorthActingOnAreDistinct(UserActivity activity)
    {
        // Each maps to a different QUNS constant and means a different thing to a
        // window manager, so collapsing any two of them into one would lose the
        // distinction that makes the answer useful.
        Assert.NotEqual(UserActivity.Ordinary, activity);
        Assert.NotEqual(UserActivity.Unknown, activity);
    }
}
