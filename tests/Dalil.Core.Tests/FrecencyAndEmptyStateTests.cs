using Dalil.Core;

namespace Dalil.Core.Tests;

/// <summary>
/// The command list learning what gets used, and the words an empty list shows.
/// </summary>
/// <remarks>
/// <para>
/// Alphabetical is the order that helps nobody. <see cref="Frecency"/> is the smallest
/// learning that does: a count per command that halves every fortnight, applied as
/// rank so it decides the order before a letter is typed and settles ties after.
/// </para>
/// <para>
/// <see cref="EmptyStateText"/> is here too because both are about what the list says
/// when the search has not decided for it.
/// </para>
/// </remarks>
public sealed class FrecencyAndEmptyStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static PaletteEntry Verb(string verb, long rank = 0) =>
        new(verb, string.Empty, [], verb, Rank: rank);

    // ---- weight -------------------------------------------------------------

    [Fact]
    public void ARunIsWorthOneAndFadesByHalfEveryHalfLife()
    {
        var store = new Frecency();
        store.Record("focus", Now);

        Assert.Equal(1.0, store.WeightOf("focus", Now), precision: 6);
        Assert.Equal(0.5, store.WeightOf("focus", Now + Frecency.HalfLife), precision: 6);
        Assert.Equal(0.25, store.WeightOf("focus", Now + (2 * Frecency.HalfLife)), precision: 6);
        Assert.Equal(0.0, store.WeightOf("never", Now));
    }

    [Fact]
    public void RunsAddAfterTheOldOnesHaveFaded()
    {
        var store = new Frecency();
        store.Record("focus", Now);
        store.Record("focus", Now + Frecency.HalfLife);

        // Half of the first plus the whole of the second.
        Assert.Equal(1.5, store.WeightOf("focus", Now + Frecency.HalfLife), precision: 6);
    }

    [Fact]
    public void WhatWasRunALotLongAgoGivesWayToWhatWasRunALittleLately()
    {
        var store = new Frecency();

        for (int i = 0; i < 10; i++) store.Record("old", Now - TimeSpan.FromDays(90));
        store.Record("new", Now);

        Assert.True(store.WeightOf("new", Now) > store.WeightOf("old", Now));
    }

    // ---- applied to the list --------------------------------------------------

    [Fact]
    public void TheUsedRiseAboveTheUnusedMostUsedFirst()
    {
        var store = new Frecency();
        store.Record("resize", Now);
        store.Record("focus", Now);
        store.Record("focus", Now);

        IReadOnlyList<PaletteEntry> applied = store.Applied([Verb("close"), Verb("focus"), Verb("resize")], Now);

        Assert.True(applied[1].Rank > applied[2].Rank, "focus, run twice, outranks resize");
        Assert.True(applied[2].Rank >= Frecency.UsedRankFloor, "anything run is above the floor");
        Assert.Equal(0, applied[0].Rank);
    }

    [Fact]
    public void TheUnusedKeepTheRankTheyHad()
    {
        // A macro's ten or a built-in's twelve is its layering among the unused, and
        // frecency has nothing to say about it.
        var store = new Frecency();
        store.Record("something else", Now);

        PaletteEntry macro = Verb("Dev layout", rank: 10);

        Assert.Equal(10, store.Applied([macro], Now)[0].Rank);
    }

    [Fact]
    public void AUsedCommandThatIsNotAvailableNowStaysWhereItIs()
    {
        // "not now" rows sit below everything for a reason; having been popular does
        // not make wm-resume useful when nothing is suspended.
        var store = new Frecency();
        store.Record("wm-resume", Now);

        var entry = new PaletteEntry("wm-resume", "already running", [], string.Empty, Rank: -1, Unavailable: true);

        Assert.Equal(-1, store.Applied([entry], Now)[0].Rank);
    }

    [Fact]
    public void AnEmptyRecordHandsTheListBackUntouched()
    {
        var store = new Frecency();
        PaletteEntry[] entries = [Verb("a"), Verb("b")];

        Assert.Same(entries, store.Applied(entries, Now));
    }

    [Fact]
    public void TheOrderTheModelProducesFollowsTheRecordBeforeTyping()
    {
        var store = new Frecency();
        store.Record("resize", Now);

        var model = new PaletteModel();
        model.SetEntries(store.Applied([Verb("close"), Verb("focus"), Verb("resize")], Now));
        model.SetQuery(">");

        Assert.Equal("resize", model.Rows[0].Entry.Primary);
        Assert.Equal("close", model.Rows[1].Entry.Primary);
        Assert.Equal("focus", model.Rows[2].Entry.Primary);
    }

    [Fact]
    public void OnceALetterIsTypedTheMatchDecides()
    {
        var store = new Frecency();
        store.Record("resize", Now);

        var model = new PaletteModel();
        model.SetEntries(store.Applied([Verb("focus"), Verb("resize")], Now));
        model.SetQuery(">foc");

        Assert.Equal("focus", model.Rows[0].Entry.Primary);
    }

    // ---- the file -----------------------------------------------------------

    [Fact]
    public void TheRecordSurvivesARoundTrip()
    {
        var store = new Frecency();
        store.Record("focus", Now);
        store.Record("focus", Now);
        store.Record("Dev layout", Now - TimeSpan.FromDays(1));

        Frecency back = Frecency.Parse(store.Serialise());

        Assert.Equal(store.WeightOf("focus", Now), back.WeightOf("focus", Now), precision: 9);
        Assert.Equal(store.WeightOf("Dev layout", Now), back.WeightOf("Dev layout", Now), precision: 9);
    }

    [Fact]
    public void ABrokenLineLosesItselfAndNotTheRest()
    {
        Frecency back = Frecency.Parse("2\t638000000000000000\tfocus\nnot a line\n\t\t\nNaN\t1\tbad\n1\t638000000000000000\tresize\n");

        Assert.Equal(["focus", "resize"], back.Keys.Order());
    }

    [Fact]
    public void TheRecordDoesNotGrowForEver()
    {
        var store = new Frecency();

        for (int i = 0; i < Frecency.Capacity + 50; i++)
            store.Record($"typo-{i}", Now + TimeSpan.FromSeconds(i));

        Assert.Equal(Frecency.Capacity, store.Keys.Count);

        // The newest survive; the oldest typos are what goes.
        Assert.Contains($"typo-{Frecency.Capacity + 49}", store.Keys);
        Assert.DoesNotContain("typo-0", store.Keys);
    }

    // ---- the empty list -----------------------------------------------------

    [Fact]
    public void AnUnreachableWindowManagerIsTheWholeStoryWhateverTheMode()
    {
        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
        {
            (string headline, _) = EmptyStateText.For(mode, searched: true, connected: false);
            Assert.Equal("Can't reach the window manager", headline);
        }
    }

    [Fact]
    public void ASearchThatFoundNothingIsAboutTheSearch()
    {
        (string headline, string hint) = EmptyStateText.For(PaletteMode.Scratchpad, searched: true, connected: true);

        Assert.Equal("No matches", headline);
        Assert.Contains("Backspace", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyScratchpadIsNotASlowWindowManager()
    {
        (string headline, string hint) = EmptyStateText.For(PaletteMode.Scratchpad, searched: false, connected: true);

        Assert.Equal("Nothing stashed", headline);
        Assert.Contains("scratchpad", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyInspectListIsGoodNews()
    {
        (string headline, _) = EmptyStateText.For(PaletteMode.Inspect, searched: false, connected: true);

        Assert.Equal("Every window is managed", headline);
    }

    [Fact]
    public void ListsTheWindowManagerFillsSayTheyAreWaiting()
    {
        foreach (PaletteMode mode in new[] { PaletteMode.Layouts, PaletteMode.Monitors, PaletteMode.Commands })
        {
            (_, string hint) = EmptyStateText.For(mode, searched: false, connected: true);
            Assert.Contains("not answered", hint, StringComparison.Ordinal);
        }
    }
}
