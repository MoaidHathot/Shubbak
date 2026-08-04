using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// What becomes of a window that is still concealed when adoption reaches it.
/// </summary>
/// <remarks>
/// <para>
/// A window concealed at startup was concealed by whoever ran last - us, before a
/// crash or a kill. Reviving it is right only when the saved session names it.
/// Without that evidence it belongs to the application that hid it: a tray host, a
/// message-only helper, a media-key listener. A desktop carries dozens.
/// </para>
/// <para>
/// This has been wrong twice, in both directions, and both were visible to the user
/// within a second of starting: once revealing eighty-four background windows at
/// once, and once never managing a tray application again.
/// </para>
/// </remarks>
public sealed class ConcealedWindowVerdictTests
{
    [Fact]
    public void OutsideARestoreNothingIsConcealedAsFarAsThisIsConcerned()
    {
        // Restoration applies only to the initial adoption pass. A window opened an
        // hour into a session must land where the user is, not where an old session
        // says - so this question is not even asked of it.
        Assert.Equal(
            WmDaemon.ConcealedVerdict.Adopt,
            WmDaemon.JudgeConcealed(
                restoring: false, concealed: true, claimedBySession: true, revived: 0, revivalBudget: 10));
    }

    [Fact]
    public void AVisibleWindowIsSimplyAdopted()
    {
        Assert.Equal(
            WmDaemon.ConcealedVerdict.Adopt,
            WmDaemon.JudgeConcealed(
                restoring: true, concealed: false, claimedBySession: false, revived: 0, revivalBudget: 0));
    }

    [Fact]
    public void AConcealedWindowNoSessionClaimsIsLeftAlone()
    {
        // Left alone rather than refused. It is concealed right now and nothing claims
        // it, which is a statement about this moment and not about the window - the
        // instant it shows itself the evidence arrives and it is judged again.
        Assert.Equal(
            WmDaemon.ConcealedVerdict.LeaveAlone,
            WmDaemon.JudgeConcealed(
                restoring: true, concealed: true, claimedBySession: false, revived: 0, revivalBudget: 10));
    }

    [Fact]
    public void AClaimedWindowWithinBudgetIsRevived()
    {
        Assert.Equal(
            WmDaemon.ConcealedVerdict.Revive,
            WmDaemon.JudgeConcealed(
                restoring: true, concealed: true, claimedBySession: true, revived: 0, revivalBudget: 1));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(0, 0)]
    public void RevivingMoreThanTheSessionRemembersIsRefused(int revived, int budget)
    {
        // The budget exists not because the claim check is expected to fail, but
        // because it already did once. The session cannot justify reviving more
        // windows than it remembers, so exceeding that is proof of a logic error -
        // and refusing costs one window that stays concealed, where not refusing
        // carpets the desktop.
        Assert.Equal(
            WmDaemon.ConcealedVerdict.TooManyRevived,
            WmDaemon.JudgeConcealed(
                restoring: true, concealed: true, claimedBySession: true, revived: revived, revivalBudget: budget));
    }

    [Fact]
    public void TheBudgetIsExactlyTheSizeOfTheSession()
    {
        // The last window a session of three remembers is still revived; the fourth
        // is not. Off by one here is either a window stranded or the guard useless.
        for (int revived = 0; revived < 3; revived++)
        {
            Assert.Equal(
                WmDaemon.ConcealedVerdict.Revive,
                WmDaemon.JudgeConcealed(
                    restoring: true, concealed: true, claimedBySession: true, revived: revived, revivalBudget: 3));
        }

        Assert.Equal(
            WmDaemon.ConcealedVerdict.TooManyRevived,
            WmDaemon.JudgeConcealed(
                restoring: true, concealed: true, claimedBySession: true, revived: 3, revivalBudget: 3));
    }

    [Fact]
    public void AnEmptySessionRevivesNothingAtAll()
    {
        // Starting with no session file at all. Every concealed window is unclaimed,
        // so none of them is touched - which is the case that used to reveal
        // eighty-four background windows.
        Assert.Equal(
            WmDaemon.ConcealedVerdict.LeaveAlone,
            WmDaemon.JudgeConcealed(
                restoring: true, concealed: true, claimedBySession: false, revived: 0, revivalBudget: 0));
    }

    [Fact]
    public void BeingClaimedIsCheckedBeforeTheBudget()
    {
        // Order matters: an unclaimed window is set aside rather than refused, even
        // when the budget is already spent. The two verdicts differ in whether the
        // window can ever be reconsidered.
        Assert.Equal(
            WmDaemon.ConcealedVerdict.LeaveAlone,
            WmDaemon.JudgeConcealed(
                restoring: true, concealed: true, claimedBySession: false, revived: 99, revivalBudget: 1));
    }
}
