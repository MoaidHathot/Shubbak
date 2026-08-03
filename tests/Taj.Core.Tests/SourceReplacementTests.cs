using Taj.Core;
using Taj.Core.Sources;

namespace Taj.Core.Tests;

/// <summary>
/// Replacing the whole set of sources, which is what a configuration reload does.
/// </summary>
/// <remarks>
/// The bar reads the same configuration file as the window manager but never re-read
/// it, so reloading with alt+shift+r left the bar showing whatever it had been
/// launched with - and nothing said so.
/// </remarks>
public sealed class SourceReplacementTests
{
    /// <summary>A source that reports whether it was started and disposed.</summary>
    private sealed class Probe(string name, string value) : ISource
    {
        public string Name { get; } = name;

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public event Action<ISource>? Changed;

        public string? Value => value;

        public void Start()
        {
            Started = true;
            Changed?.Invoke(this);
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TheReplacementsValuesAreTheOnesTheBarSees()
    {
        using var model = new BarModel(TajConfigLoader.CreateDefault().Default);

        model.AddSource(new Probe("clock", "old"));
        model.ReplaceSources([new Probe("clock", "new")]);

        Assert.Equal("new", model.GetValue("clock"));
    }

    [Fact]
    public void TheOldSourcesAreDisposed()
    {
        // Each source owns a timer. Dropping them instead of disposing them would add
        // a clock on every reload, and the bar would slowly fill with ticking that
        // nothing reads.
        using var model = new BarModel(TajConfigLoader.CreateDefault().Default);

        var original = new Probe("clock", "old");
        model.AddSource(original);

        model.ReplaceSources([new Probe("clock", "new")]);

        Assert.True(original.Disposed);
    }

    [Fact]
    public void TheReplacementsAreStarted()
    {
        using var model = new BarModel(TajConfigLoader.CreateDefault().Default);

        var replacement = new Probe("clock", "new");
        model.ReplaceSources([replacement]);

        Assert.True(replacement.Started);
    }

    [Fact]
    public void ASourceThatIsNoLongerDeclaredStopsBeingUpdated()
    {
        // A reload that removes a source must not leave the old one publishing into
        // the model behind the new configuration's back.
        using var model = new BarModel(TajConfigLoader.CreateDefault().Default);

        var removed = new Probe("seattle", "old");
        model.AddSource(removed);

        model.ReplaceSources([new Probe("clock", "new")]);

        Assert.True(removed.Disposed);
        Assert.True(model.GetValue("clock") is not null);
    }

    [Fact]
    public void ReplacingWithNothingDisposesEverything()
    {
        using var model = new BarModel(TajConfigLoader.CreateDefault().Default);

        var a = new Probe("a", "1");
        var b = new Probe("b", "2");

        model.AddSource(a);
        model.AddSource(b);

        model.ReplaceSources([]);

        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
    }
}
