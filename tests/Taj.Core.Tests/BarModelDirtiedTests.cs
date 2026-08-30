using System.Globalization;
using Taj.Core;
using Taj.Core.Sources;

namespace Taj.Core.Tests;

/// <summary>
/// The signal that tells a waiting bar something changed.
/// </summary>
/// <remarks>
/// <para>
/// The bar's loop used to run every 16 ms and ask whether anything had happened.
/// Almost nothing ever had, and that poll was the single largest consumer across the
/// three processes - the bar spent more CPU than the window manager it reports on.
/// It now waits, which means the model has to say when it changes.
/// </para>
/// <para>
/// The failure this guards against is a change that publishes without raising: the
/// bar would go on showing the old value until something unrelated woke it, and a
/// clock frozen at the wrong minute is a far worse bug than the polling it replaced.
/// </para>
/// </remarks>
public sealed class BarModelDirtiedTests
{
    private static BarModel Model() => new(TajConfigLoader.CreateDefault().Default);

    /// <summary>Clears the dirty flag the way the loop does, by building the tree.</summary>
    private static void Settle(BarModel model) => model.Build();

    [Fact]
    public void SettingAValueWakesTheLoop()
    {
        BarModel model = Model();
        Settle(model);

        int woken = 0;
        model.Dirtied += () => woken++;

        model.SetValue("clock", "12:34");

        Assert.Equal(1, woken);
        Assert.True(model.IsDirty);
    }

    [Fact]
    public void SettingTheSameValueDoesNotWakeIt()
    {
        // The suppression that makes the whole design worth having. A clock polled
        // twice a second whose minute has not changed must not wake anything, or the
        // wait would be no better than the poll it replaced.
        BarModel model = Model();
        model.SetValue("clock", "12:34");
        Settle(model);

        int woken = 0;
        model.Dirtied += () => woken++;

        model.SetValue("clock", "12:34");

        Assert.Equal(0, woken);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void AModelAlreadyDirtyDoesNotWakeAgain()
    {
        // Edge-triggered. Whoever was going to be woken has been, and has not looked
        // yet, so a second signal is a wake-up for work already queued.
        BarModel model = Model();
        Settle(model);

        int woken = 0;
        model.Dirtied += () => woken++;

        model.SetValue("a", "1");
        model.SetValue("b", "2");
        model.SetValue("c", "3");

        Assert.Equal(1, woken);
    }

    [Fact]
    public void ItWakesAgainOnceTheTreeHasBeenBuilt()
    {
        // The other half of edge-triggering: after the loop has looked, the next
        // change must wake it again. Getting this wrong gives a bar that updates once
        // and then never again, which is the worst outcome available here.
        BarModel model = Model();
        Settle(model);

        int woken = 0;
        model.Dirtied += () => woken++;

        model.SetValue("clock", "12:34");
        Settle(model);
        model.SetValue("clock", "12:35");

        Assert.Equal(2, woken);
    }

    [Fact]
    public void ASourcePublishingWakesTheLoop()
    {
        // The path that matters most in practice: values arrive from thread-pool
        // timers and from the pipe, on threads that are not the loop's.
        BarModel model = Model();
        var source = new PushSource("weather");
        model.AddSource(source);
        Settle(model);

        int woken = 0;
        model.Dirtied += () => woken++;

        source.Set("raining");

        Assert.Equal(1, woken);
        Assert.Equal("raining", model.GetValue("weather"));
    }

    [Fact]
    public void AProfileChangeWakesTheLoop()
    {
        // A profile carries the bar's height, so missing this leaves a bar the wrong
        // size until something else happens to change.
        BarModel model = Model();
        Settle(model);

        int woken = 0;
        model.Dirtied += () => woken++;

        model.Profile = TajConfigLoader.CreateDefault().Default with { Height = 40 };

        Assert.Equal(1, woken);
    }

    [Fact]
    public void AnIntervalSourceWakesTheLoopWhenItsValueChanges()
    {
        BarModel model = Model();

        int produced = 0;
        var source = new IntervalSource(
            "counter",
            TimeSpan.FromMilliseconds(50),
            () => Interlocked.Increment(ref produced).ToString(CultureInfo.InvariantCulture));

        model.AddSource(source);
        Settle(model);

        var woken = new ManualResetEventSlim(false);
        model.Dirtied += () => woken.Set();

        Assert.True(
            woken.Wait(TimeSpan.FromSeconds(5)),
            "a polling source published a new value and the loop was never told");
    }

    [Fact]
    public void AHandlerMayCallBackIntoTheModel()
    {
        // The notification is raised after the lock is released. Every real subscriber
        // is a wake handle and would not care, but a callback invoked while holding
        // the lock that every publish needs is the shape of a deadlock, and this is
        // the cheapest way to keep saying so.
        BarModel model = Model();
        Settle(model);

        string? seen = null;
        model.Dirtied += () => seen = model.GetValue("clock");

        model.SetValue("clock", "12:34");

        Assert.Equal("12:34", seen);
    }
}
