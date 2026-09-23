using Taj.Core.Sources;

namespace Taj.Core.Tests;

/// <summary>
/// One set of sources feeding every bar.
/// </summary>
/// <remarks>
/// <para>
/// Each bar used to build its own sources from the same declarations, so a desk with
/// two displays ran two clocks, two keyboard pollers and - the one that mattered -
/// two copies of every <c>kind="command"</c> script, each doing the same work to
/// print the same value. The hub owns the sources once and fans each value out to
/// whichever models are attached at the time.
/// </para>
/// <para>
/// The contract the bars rely on: a model attached late gets every value the sources
/// have so far, a replaced set is disposed rather than dropped, and a stand-down in
/// force when the set is replaced applies to the replacements too.
/// </para>
/// </remarks>
public sealed class SourceHubTests
{
    /// <summary>A source whose value the test sets, that records what was done to it.</summary>
    private sealed class Probe(string name, string? value = null) : ISource
    {
        private string? _value = value;

        public string Name { get; } = name;

        public string? Value => _value;

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public int StoodDown { get; private set; }

        public int StoodUp { get; private set; }

        public event Action<ISource>? Changed;

        public void Start() => Started = true;

        public void StandDown() => StoodDown++;

        public void StandUp() => StoodUp++;

        public void Publish(string value)
        {
            _value = value;
            Changed?.Invoke(this);
        }

        public void Dispose() => Disposed = true;
    }

    private static BarModel Model() => new(TajConfigLoader.CreateDefault().Default);

    [Fact]
    public void AValueReachesEveryAttachedModel()
    {
        using var hub = new SourceHub();
        using BarModel first = Model();
        using BarModel second = Model();

        var clock = new Probe("clock");
        hub.Replace([clock]);
        hub.Attach(first);
        hub.Attach(second);

        clock.Publish("12:00");

        Assert.Equal("12:00", first.GetValue("clock"));
        Assert.Equal("12:00", second.GetValue("clock"));
    }

    [Fact]
    public void AModelAttachedLateIsGivenWhatTheSourcesAlreadyHave()
    {
        // A monitor plugged in an hour after logon gets a bar that shows the time at
        // once, not one that waits for the next tick of a clock it did not see start.
        using var hub = new SourceHub();

        var clock = new Probe("clock", "09:41");
        hub.Replace([clock]);

        using BarModel late = Model();
        hub.Attach(late);

        Assert.Equal("09:41", late.GetValue("clock"));
    }

    [Fact]
    public void ADetachedModelHearsNothingMore()
    {
        using var hub = new SourceHub();
        using BarModel model = Model();

        var clock = new Probe("clock");
        hub.Replace([clock]);
        hub.Attach(model);

        clock.Publish("1");
        hub.Detach(model);
        clock.Publish("2");

        Assert.Equal("1", model.GetValue("clock"));
    }

    [Fact]
    public void ReplacingStartsTheNewSourcesAndDisposesTheOld()
    {
        using var hub = new SourceHub();

        var old = new Probe("clock", "old");
        hub.Replace([old]);

        var replacement = new Probe("clock", "new");
        hub.Replace([replacement]);

        Assert.True(old.Disposed);
        Assert.True(replacement.Started);
        Assert.Equal(["clock"], hub.Names);
    }

    [Fact]
    public void AReplacementsValueReachesTheModelsAtOnce()
    {
        // Replace publishes each new source's current value, so a reload does not
        // blank the pills until the replacements tick.
        using var hub = new SourceHub();
        using BarModel model = Model();
        hub.Attach(model);

        hub.Replace([new Probe("clock", "old")]);
        hub.Replace([new Probe("clock", "new")]);

        Assert.Equal("new", model.GetValue("clock"));
    }

    [Fact]
    public void AnOldSourceThatPublishesAfterReplacementIsIgnored()
    {
        // A disposed process source may still deliver a line that was in flight.
        using var hub = new SourceHub();
        using BarModel model = Model();
        hub.Attach(model);

        var old = new Probe("clock", "old");
        hub.Replace([old]);
        hub.Replace([new Probe("clock", "new")]);

        old.Publish("stale");

        Assert.Equal("new", model.GetValue("clock"));
    }

    [Fact]
    public void TheFirstOfTwoSourcesWithOneNameWins()
    {
        // The same rule the model applied, kept: the declaration order in the file
        // decides, and the loser is disposed rather than left ticking unheard.
        using var hub = new SourceHub();

        var first = new Probe("clock", "first");
        var second = new Probe("clock", "second");
        hub.Replace([first, second]);

        using BarModel model = Model();
        hub.Attach(model);

        Assert.Equal("first", model.GetValue("clock"));
        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.True(second.Disposed);
    }

    [Fact]
    public void StandingDownReachesEverySourceOnce()
    {
        using var hub = new SourceHub();

        var a = new Probe("a");
        var b = new Probe("b");
        hub.Replace([a, b]);

        hub.StandDown();
        hub.StandUp();

        Assert.Equal(1, a.StoodDown);
        Assert.Equal(1, b.StoodDown);
        Assert.Equal(1, a.StoodUp);
        Assert.Equal(1, b.StoodUp);
    }

    [Fact]
    public void SourcesCreatedDuringAStandDownAreStoodDownToo()
    {
        // A reload while a full-screen game is up must not start a clock ticking
        // behind it; the replacements inherit the state the bar is in.
        using var hub = new SourceHub();
        hub.StandDown();

        var clock = new Probe("clock");
        hub.Replace([clock]);

        Assert.Equal(1, clock.StoodDown);

        hub.StandUp();

        Assert.Equal(1, clock.StoodUp);
    }

    [Fact]
    public void DisposingDisposesTheSourcesAndForgetsTheModels()
    {
        var hub = new SourceHub();
        using BarModel model = Model();
        hub.Attach(model);

        var clock = new Probe("clock", "1");
        hub.Replace([clock]);

        hub.Dispose();

        Assert.True(clock.Disposed);
        Assert.Empty(hub.Names);

        // Idempotent, and nothing after it reaches a model.
        hub.Dispose();
        clock.Publish("2");

        Assert.Equal("1", model.GetValue("clock"));
    }

    [Fact]
    public void AttachingTwiceIsOnce()
    {
        using var hub = new SourceHub();
        using BarModel model = Model();

        var clock = new Probe("clock");
        hub.Replace([clock]);
        hub.Attach(model);
        hub.Attach(model);

        // A fresh model is dirty until it has been built once; settle it so the
        // publish below is the only thing that wakes it.
        model.Build();

        int dirtied = 0;
        model.Dirtied += () => dirtied++;

        clock.Publish("x");

        // The default profile shows the clock, so one publish is one wake - not two.
        Assert.Equal(1, dirtied);
        Assert.Equal("x", model.GetValue("clock"));
    }
}
