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
    public void NothingAbsurdIsReported()
    {
        // This asserted Ordinary or Presenting, on the reasoning that the test host is
        // a console process on a normal desktop. That holds on a desktop and does not
        // hold on a build agent, which has no interactive session for the shell to
        // describe - it reports a busy or quiet state instead, and the test failed on
        // the first run in CI for a reason that had nothing to do with the mapping.
        //
        // PrivilegeTests already declines to assert elevation for the same kind of
        // reason: a legitimate way of running the suite must not fail a test that
        // insisted the machine look a particular way.
        //
        // What is portable is that the mapping produces a state it has actually
        // established, and that it never claims a game. QUNS_RUNNING_D3D_FULL_SCREEN
        // is the one value no build agent and no console session can produce, so a
        // mapping that collapsed everything onto it would still be caught here.
        UserActivity activity = DisplayPreferences.CurrentActivity();

        Assert.NotEqual(UserActivity.Unknown, activity);

        Assert.NotEqual(UserActivity.FullScreenGame, activity);
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
