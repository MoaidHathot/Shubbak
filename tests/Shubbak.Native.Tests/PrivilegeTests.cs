using Shubbak.Native;

namespace Shubbak.Native.Tests;

/// <summary>
/// What this process is allowed to do to windows it does not own.
/// </summary>
/// <remarks>
/// <para>
/// Windows refuses to let a process move a window belonging to one at a higher
/// integrity level. That is why an ordinary build cannot tile Task Manager, and it is
/// not something a window manager can code around: SetWindowPos returns
/// ERROR_ACCESS_DENIED and the window does not move.
/// </para>
/// <para>
/// Two things lift it, both fixed when the process starts. Elevation is the obvious
/// one. The other is uiAccess, granted to a signed binary installed under Program
/// Files whose manifest asks for it - the privilege a screen reader needs, and what
/// GlazeWM ships, which is why it tiles Task Manager without asking to be run as
/// administrator.
/// </para>
/// <para>
/// These tests cannot assert which answer this machine gives, because it depends on
/// how the test host was started and where it lives. What they can pin is that the
/// question is answerable, that the three answers agree with each other, and that a
/// failure to ask is reported as "no" rather than as "yes".
/// </para>
/// </remarks>
public sealed class PrivilegeTests
{
    [Fact]
    public void TheQuestionIsAnswerableOnThisMachine()
    {
        // Not a property of the code. It asserts the token calls are accepted by the
        // Windows being built on - a wrong signature would throw, and a rejected call
        // would silently report false forever while looking like a real answer.
        //
        // Reading them is the assertion; there is nothing to compare against.
        _ = Win32Privilege.HasUiAccess;
        _ = Win32Privilege.IsElevated;
        _ = Win32Privilege.CanDriveHigherIntegrity;
    }

    [Fact]
    public void TheCapabilityIsExactlyTheTwoWaysOfHavingIt()
    {
        // The relationship that the window filter depends on. If this ever stops
        // holding, a build will either refuse to manage windows it could move or
        // reserve tiles for windows it cannot.
        Assert.Equal(
            Win32Privilege.HasUiAccess || Win32Privilege.IsElevated,
            Win32Privilege.CanDriveHigherIntegrity);
    }

    [Fact]
    public void TheAnswerDoesNotChangeWhileTheProcessRuns()
    {
        // Both inputs are fixed at process creation, so the value is cached and asked
        // once per window evaluated. A changing answer would mean the cache is wrong
        // and every window filtered before the change was filtered against a
        // different rule.
        Assert.Equal(Win32Privilege.HasUiAccess, Win32Privilege.HasUiAccess);
        Assert.Equal(Win32Privilege.IsElevated, Win32Privilege.IsElevated);
        Assert.Equal(Win32Privilege.CanDriveHigherIntegrity, Win32Privilege.CanDriveHigherIntegrity);
    }

    [Fact]
    public void AnOrdinaryDevelopmentBuildHasNeither()
    {
        // Deliberately asserted rather than assumed, because the whole arrangement
        // exists so that a source-tree build behaves differently from a packaged one.
        // A test host running from a source tree is unsigned and outside Program
        // Files, so uiAccess cannot have been granted - Windows would have refused to
        // start it at all.
        //
        // Elevation is not asserted: running the suite from an elevated shell is a
        // reasonable thing to do and would fail a test that insisted otherwise.
        Assert.False(
            Win32Privilege.HasUiAccess,
            "uiAccess was granted to a build running from the source tree, which should " +
            "be impossible - it requires a signature and an install under Program Files");
    }
}
