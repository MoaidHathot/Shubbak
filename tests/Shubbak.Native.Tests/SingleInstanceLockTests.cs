using Shubbak.Native;

namespace Shubbak.Native.Tests;

/// <summary>
/// Claiming the right to be the only process of a kind.
/// </summary>
/// <remarks>
/// The window manager has had this since two of them were found fighting over one
/// desktop. The bar and the palette had nothing, and reach the same state by an
/// ordinary route: both survive the window manager restarting, and the restarted
/// window manager then runs the startup commands that launch them.
/// </remarks>
public class SingleInstanceLockTests
{
    /// <summary>A name nothing else will claim, so the tests cannot collide.</summary>
    private static string Name() =>
        $@"Local\shubbak-test-{Guid.NewGuid():N}";

    /// <summary>
    /// Runs a claim on a thread of its own, and reports what it found.
    /// </summary>
    /// <remarks>
    /// A Windows mutex is owned by a <em>thread</em>, and the owning thread may take it
    /// again as often as it likes - so claiming twice on this one would succeed and
    /// prove nothing. Another thread is refused exactly as another process would be,
    /// which makes it a faithful stand-in for the case that matters and costs no child
    /// process to arrange.
    /// </remarks>
    private static (bool Held, bool Certain) ClaimOnAnotherThread(string name)
    {
        (bool Held, bool Certain) outcome = default;

        var thread = new Thread(() =>
        {
            using SingleInstanceLock claim = SingleInstanceLock.Claim(name);
            outcome = (claim.Held, claim.Certain);
        });

        thread.Start();
        thread.Join();

        return outcome;
    }

    [Fact]
    public void TheFirstClaimSucceeds()
    {
        using SingleInstanceLock first = SingleInstanceLock.Claim(Name());

        Assert.True(first.Held);
        Assert.True(first.Certain);
    }

    [Fact]
    public void TheSecondClaimOnTheSameNameIsRefused()
    {
        string name = Name();

        using SingleInstanceLock first = SingleInstanceLock.Claim(name);

        Assert.True(first.Held);

        (bool held, bool certain) = ClaimOnAnotherThread(name);

        Assert.False(held);

        // Refused, not unanswerable. The two demand opposite responses - the window
        // manager stops either way, the bar carries on when it cannot tell - and a
        // caller that cannot distinguish them has to guess.
        Assert.True(certain);
    }

    [Fact]
    public void TheOwningThreadIsNotBlockedByItself()
    {
        // Worth stating because it is surprising, and because it is why the guard is
        // written the way it is: it counts processes, not calls. Every program here
        // claims once, at startup, on its main thread.
        string name = Name();

        using SingleInstanceLock first = SingleInstanceLock.Claim(name);
        using SingleInstanceLock again = SingleInstanceLock.Claim(name);

        Assert.True(first.Held);
        Assert.True(again.Held);
    }

    [Fact]
    public void ADifferentNameIsADifferentClaim()
    {
        // What keeps the bar, the palette and the window manager from blocking one
        // another while still each blocking themselves.
        using SingleInstanceLock bar = SingleInstanceLock.Claim(Name());
        using SingleInstanceLock palette = SingleInstanceLock.Claim(Name());

        Assert.True(bar.Held);
        Assert.True(palette.Held);
    }

    [Fact]
    public void ReleasingLetsTheNextClaimThrough()
    {
        // The ordinary case: a bar is stopped, and starting one works again. Without
        // this a single-instance guard would be a one-shot per boot.
        string name = Name();

        SingleInstanceLock first = SingleInstanceLock.Claim(name);
        Assert.True(first.Held);

        Assert.False(ClaimOnAnotherThread(name).Held);

        first.Dispose();

        Assert.True(ClaimOnAnotherThread(name).Held);
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        // It is held in a using in every caller and released again on the way out of
        // some of them, and a double release throws rather than being ignored.
        SingleInstanceLock claim = SingleInstanceLock.Claim(Name());

        claim.Dispose();
        claim.Dispose();

        Assert.False(claim.Held);
    }

    [Fact]
    public void DisposingAClaimThatWasRefusedIsHarmless()
    {
        string name = Name();

        using SingleInstanceLock first = SingleInstanceLock.Claim(name);
        SingleInstanceLock second = SingleInstanceLock.Claim(name);

        second.Dispose();

        Assert.False(second.Held);
    }

    [Fact]
    public void AnUnheldNameReadsAsFree()
    {
        Assert.False(SingleInstanceLock.IsHeldByAnyone(Name()));
    }

    [Fact]
    public void AHeldNameReadsAsTaken()
    {
        // What --replace waits on. Reading a held name as free would start the second
        // window manager the wait exists to prevent.
        //
        // Asked from another thread, because the owner may take its own mutex again -
        // so asking on the owning thread would report every name as free.
        string name = Name();

        using SingleInstanceLock claim = SingleInstanceLock.Claim(name);

        bool? held = null;
        var thread = new Thread(() => held = SingleInstanceLock.IsHeldByAnyone(name));

        thread.Start();
        thread.Join();

        Assert.True(held);
    }

    [Fact]
    public void AskingWhetherANameIsHeldDoesNotTakeIt()
    {
        // It probes with a mutex of its own, and forgetting to release that would mean
        // asking the question made the answer true.
        string name = Name();

        Assert.False(SingleInstanceLock.IsHeldByAnyone(name));

        using SingleInstanceLock claim = SingleInstanceLock.Claim(name);

        Assert.True(claim.Held);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyNameIsRefusedOutright(string name)
    {
        // A blank name would silently become a process-wide unnamed mutex, which every
        // caller would acquire and nobody would be guarded by.
        Assert.Throws<ArgumentException>(() => SingleInstanceLock.Claim(name));
        Assert.Throws<ArgumentException>(() => SingleInstanceLock.IsHeldByAnyone(name));
    }

    [Fact]
    public void ANullNameIsRefusedOutright()
    {
        Assert.Throws<ArgumentNullException>(() => SingleInstanceLock.Claim(null!));
        Assert.Throws<ArgumentNullException>(() => SingleInstanceLock.IsHeldByAnyone(null!));
    }
}
