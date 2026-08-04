using Shubbak.Core.Tree;
using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// The four sets that decide which windows Shubbak manages.
/// </summary>
/// <remarks>
/// Nearly every window-lifecycle bug this program has had lived in the interplay
/// between these sets rather than in any one of them, and none of it was tested:
/// a tray application never being managed again after startup, releasing a window
/// by hand lasting a fraction of a second, and deleting a rule appearing to do
/// nothing until the application was restarted.
/// </remarks>
public sealed class WindowRegistryTests
{
    private static WindowNode Node(long handle = 0x1000) =>
        new(handle, new WindowIdentity { Title = "test", ProcessName = "test", ClassName = "Test" });

    // ---- the adoption guard ------------------------------------------------

    [Fact]
    public void AWindowNothingIsKnownAboutHasNoVerdict()
    {
        var registry = new WindowRegistry();

        Assert.False(registry.AlreadyDecided(0x1));
        Assert.False(registry.IsManaged(0x1));
        Assert.False(registry.IsExcluded(0x1));
    }

    [Fact]
    public void EveryKindOfVerdictStopsTheWindowBeingJudgedAgain()
    {
        // The guard exists so the many events a window raises do not re-run the whole
        // decision each time.
        var managed = new WindowRegistry();
        managed.Adopt(0x1, Node());

        var excluded = new WindowRegistry();
        excluded.Exclude(0x2);

        var setAside = new WindowRegistry();
        setAside.SetAside(0x3);

        Assert.True(managed.AlreadyDecided(0x1));
        Assert.True(excluded.AlreadyDecided(0x2));
        Assert.True(setAside.AlreadyDecided(0x3));
    }

    // ---- releasing, and the ordering it depends on -------------------------

    [Fact]
    public void ReleasingForgetsEveryVerdictAboutTheWindow()
    {
        // A window that has closed is gone. Nothing about it should survive, or a new
        // window that happens to reuse the handle inherits its history.
        var registry = new WindowRegistry();

        registry.Adopt(0x1, Node());
        registry.SetAside(0x1);

        Assert.NotNull(registry.Release(0x1));

        Assert.False(registry.IsManaged(0x1));
        Assert.False(registry.IsExcluded(0x1));
        Assert.False(registry.AlreadyDecided(0x1));
    }

    [Fact]
    public void ReleasingByHandKeepsTheWindowReleased()
    {
        // The bug this prevents: releasing forgets every verdict, so a caller that
        // excluded the window first would have that wiped, and the very next event
        // the window raised would take it straight back. Letting go lasted a fraction
        // of a second.
        var registry = new WindowRegistry();
        registry.Adopt(0x1, Node());

        registry.Release(0x1, thenExclude: true);

        Assert.False(registry.IsManaged(0x1));
        Assert.True(registry.IsExcluded(0x1));
        Assert.True(registry.AlreadyDecided(0x1));
    }

    [Fact]
    public void ExcludingBeforeReleasingWouldNotHaveWorked()
    {
        // Stated as a test because it is the mistake the API now makes impossible:
        // doing it in the wrong order really does lose the exclusion.
        var registry = new WindowRegistry();
        registry.Adopt(0x1, Node());

        registry.Exclude(0x1);
        registry.Release(0x1);

        Assert.False(registry.IsExcluded(0x1));
    }

    [Fact]
    public void ReleasingSomethingNeverManagedReportsNothing()
    {
        var registry = new WindowRegistry();

        Assert.Null(registry.Release(0x1));

        // But it still clears, because a caller asking to release is asserting the
        // window is not ours - and that has to hold afterwards.
        registry.Exclude(0x2);
        Assert.Null(registry.Release(0x2));
        Assert.False(registry.IsExcluded(0x2));
    }

    [Fact]
    public void ReleasingReturnsTheNodeSoTheCallerCanTidyUpAfterIt()
    {
        // The daemon needs it to clear the border, stop the animation and tell the
        // window manager - all of which need the node, not just the handle.
        var registry = new WindowRegistry();
        WindowNode node = Node(0x99);

        registry.Adopt(0x99, node);

        Assert.Same(node, registry.Release(0x99));
    }

    // ---- adopting ----------------------------------------------------------

    [Fact]
    public void AdoptingOverridesAnEarlierRefusal()
    {
        // Taking the window on is the newer decision, and toggle-managed depends on
        // it: the whole point is acting on a window Shubbak had refused.
        var registry = new WindowRegistry();

        registry.Exclude(0x1);
        registry.Adopt(0x1, Node());

        Assert.True(registry.IsManaged(0x1));
        Assert.False(registry.IsExcluded(0x1));
    }

    [Fact]
    public void AnAdoptedWindowIsArrivingExactlyOnce()
    {
        // Cleared as it is read, so a window is exempt from animation only for the
        // single pass that first gives it a rectangle.
        var registry = new WindowRegistry();
        registry.Adopt(0x1, Node());

        Assert.True(registry.TakeArriving(0x1));
        Assert.False(registry.TakeArriving(0x1));
    }

    [Fact]
    public void AWindowNeverAdoptedIsNotArriving()
    {
        Assert.False(new WindowRegistry().TakeArriving(0x1));
    }

    [Fact]
    public void ReleasingAWindowBeforeItIsPlacedForgetsThatItWasArriving()
    {
        // Otherwise a handle reused by a later window would skip its first animation
        // for no reason anyone could trace.
        var registry = new WindowRegistry();

        registry.Adopt(0x1, Node());
        registry.Release(0x1);

        Assert.False(registry.TakeArriving(0x1));
    }

    // ---- set aside is about a moment, not the window -----------------------

    [Fact]
    public void ShowingItselfLetsASetAsideWindowBeJudgedAgain()
    {
        // The bug this covers, exactly: a tray application running when Shubbak
        // started was never managed again, however many times it was opened. Closing
        // and reopening it worked - because that made a new window with a handle the
        // set had never heard of, which is what made it look intermittent.
        var registry = new WindowRegistry();

        registry.SetAside(0x1);
        Assert.True(registry.AlreadyDecided(0x1));

        registry.NoLongerSetAside(0x1);
        Assert.False(registry.AlreadyDecided(0x1));
    }

    [Fact]
    public void BeingSetAsideIsNotBeingExcluded()
    {
        // They are different questions and used to be the same set. One is about the
        // window, the other about the moment Shubbak happened to start.
        var registry = new WindowRegistry();
        registry.SetAside(0x1);

        Assert.False(registry.IsExcluded(0x1));
    }

    // ---- reloading ---------------------------------------------------------

    [Fact]
    public void ReloadingForgetsBothKindsOfVerdictAndCountsThem()
    {
        // Both sets are caches of past verdicts and the verdicts have just changed.
        // Keeping them meant deleting an ignore rule and reloading did nothing at all
        // until the window was closed and reopened.
        var registry = new WindowRegistry();

        registry.Exclude(0x1);
        registry.Exclude(0x2);
        registry.SetAside(0x3);

        Assert.Equal(3, registry.ForgetVerdicts());

        Assert.False(registry.AlreadyDecided(0x1));
        Assert.False(registry.AlreadyDecided(0x2));
        Assert.False(registry.AlreadyDecided(0x3));
    }

    [Fact]
    public void ReloadingDoesNotReleaseAnythingItManages()
    {
        // Forgetting a refusal is not the same as dropping a window, and a reload
        // must not detach the desktop.
        var registry = new WindowRegistry();
        registry.Adopt(0x1, Node());
        registry.Exclude(0x2);

        Assert.Equal(1, registry.ForgetVerdicts());

        Assert.True(registry.IsManaged(0x1));
        Assert.Equal(1, registry.ManagedCount);
    }

    // ---- iteration ---------------------------------------------------------

    [Fact]
    public void ASnapshotSurvivesReleasingWhileWalkingIt()
    {
        // The reload path releases windows as it walks them, which mutates the very
        // dictionary being enumerated. Copying is the protection, and it is its own
        // method so it cannot be forgotten.
        var registry = new WindowRegistry();

        for (nint handle = 1; handle <= 5; handle++) registry.Adopt(handle, Node(handle));

        foreach (nint handle in registry.HandlesSnapshot()) registry.Release(handle);

        Assert.Equal(0, registry.ManagedCount);
    }

    [Fact]
    public void CountsReportWhatIsHeld()
    {
        var registry = new WindowRegistry();

        registry.Adopt(0x1, Node(0x1));
        registry.Adopt(0x2, Node(0x2));
        registry.Exclude(0x3);

        Assert.Equal(2, registry.ManagedCount);
        Assert.Equal(1, registry.ExcludedCount);
        Assert.Equal(2, registry.Handles.Count);
    }

    [Fact]
    public void LookingUpAManagedWindowFindsTheNodeItWasAdoptedWith()
    {
        var registry = new WindowRegistry();
        WindowNode node = Node(0x7);

        registry.Adopt(0x7, node);

        Assert.True(registry.TryGet(0x7, out WindowNode found));
        Assert.Same(node, found);
        Assert.False(registry.TryGet(0x8, out _));
    }
}
