using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Ipc;
using Taj.Core.Sources;

namespace Taj.Core.Tests;

/// <summary>
/// A value put on the bar by a <c>signal</c> another program raises.
/// </summary>
/// <remarks>
/// <para>
/// The bar had two ways to show a value - a clock, or a program the bar itself
/// starts and reads - and no way for a program that already knew the value to hand
/// it over. The watcher read the battery for its <c>battery-low</c> fact and threw
/// the percentage away, because a context is a boolean and there was nothing else to
/// put it in. <c>kind="signal"</c> is the third way: <c>shubbak signal battery 87</c>
/// from anywhere, and <c>{{ battery }}</c> reads <c>87</c>.
/// </para>
/// <para>
/// What the bar relies on: the arguments are the value, one signal reaches every
/// source listening for it whatever their names, a second arrival of the same value
/// wakes nothing, and the hub knows whether it listens at all - since that is what
/// decides whether the bar subscribes to the topic and asks for the values to be
/// said again.
/// </para>
/// </remarks>
public sealed class SignalSourceTests
{
    private static BarModel Model() => new(TajConfigLoader.CreateDefault().Default);

    [Fact]
    public void OneArgumentIsTheValue()
    {
        var source = new SignalSource("battery");

        source.Receive(["87"]);

        Assert.Equal("87", source.Value);
    }

    [Fact]
    public void SeveralArgumentsAreJoinedBySpaces()
    {
        // `shubbak signal weather Sunny 21C` from a shell that split the words is
        // still a readable value; a publisher that wants them apart raises two signals.
        var source = new SignalSource("weather");

        source.Receive(["Sunny", "21C"]);

        Assert.Equal("Sunny 21C", source.Value);
    }

    [Fact]
    public void NoArgumentsIsEmptyWhichHidesTheWidget()
    {
        var source = new SignalSource("battery");
        source.Receive(["87"]);

        source.Receive([]);

        Assert.Equal(string.Empty, source.Value);
    }

    [Fact]
    public void TheSignalListenedForIsTheSourceNameUnlessSaid()
    {
        Assert.Equal("battery", new SignalSource("battery").Signal);
        Assert.Equal("ayn.battery", new SignalSource("battery", "ayn.battery").Signal);
        Assert.Equal("battery", new SignalSource("battery", "  ").Signal);
    }

    [Fact]
    public void TheSameValueTwiceRaisesChangedOnce()
    {
        // Every bar's connection hears every signal and hands it to the one hub, so
        // the second and third copies of one signal must cost nothing on screen.
        var source = new SignalSource("battery");
        int changed = 0;
        source.Changed += _ => changed++;

        source.Receive(["87"]);
        source.Receive(["87"]);
        source.Receive(["87"]);

        Assert.Equal(1, changed);
    }

    [Fact]
    public void TheHubRoutesASignalToEverySourceListeningForItByAnyName()
    {
        using var hub = new SourceHub();
        using BarModel model = Model();
        hub.Attach(model);

        hub.Replace(
        [
            new SignalSource("battery"),
            new SignalSource("bat", "battery"),
            new SignalSource("weather"),
        ]);

        hub.Signal("battery", ["87"]);

        Assert.Equal("87", model.GetValue("battery"));
        Assert.Equal("87", model.GetValue("bat"));
        Assert.Null(model.GetValue("weather"));
    }

    [Fact]
    public void ASignalIsMatchedWithoutRegardForCase()
    {
        // As every signal is: `Battery` typed into a binding and `battery` in the
        // bar's file are the same word to whoever typed them.
        using var hub = new SourceHub();
        using BarModel model = Model();
        hub.Attach(model);
        hub.Replace([new SignalSource("battery")]);

        hub.Signal("BATTERY", ["12"]);

        Assert.Equal("12", model.GetValue("battery"));
    }

    [Fact]
    public void ASignalNobodyListensForDoesNothing()
    {
        using var hub = new SourceHub();
        using BarModel model = Model();
        hub.Attach(model);
        hub.Replace([new SignalSource("battery")]);
        model.Build();

        int dirtied = 0;
        model.Dirtied += () => dirtied++;

        hub.Signal("palette", []);
        hub.Signal("ayn", ["microphone", "toggle-mute"]);

        Assert.Equal(0, dirtied);
        Assert.Null(model.GetValue("palette"));
    }

    [Fact]
    public void TheHubKnowsWhetherItListensForSignals()
    {
        // This is what decides whether a bar subscribes to the topic at all; a bar
        // with no signal source must not, so the window manager keeps its line about a
        // signal raised with nobody listening.
        using var hub = new SourceHub();

        Assert.False(hub.ListensForSignals);

        hub.Replace([new ClockSource("clock", "HH:mm", TimeSpan.FromSeconds(1))]);
        Assert.False(hub.ListensForSignals);

        hub.Replace([new ClockSource("clock", "HH:mm", TimeSpan.FromSeconds(1)), new SignalSource("battery")]);
        Assert.True(hub.ListensForSignals);

        hub.Replace([]);
        Assert.False(hub.ListensForSignals);
    }

    [Fact]
    public void AReplacedSignalSourceIsNoLongerRouted()
    {
        using var hub = new SourceHub();
        using BarModel model = Model();
        hub.Attach(model);

        hub.Replace([new SignalSource("battery")]);
        hub.Replace([new SignalSource("cpu")]);

        hub.Signal("battery", ["87"]);
        hub.Signal("cpu", ["12"]);

        Assert.Null(model.GetValue("battery"));
        Assert.Equal("12", model.GetValue("cpu"));
    }

    [Fact]
    public void DisposingTheHubStopsRouting()
    {
        var hub = new SourceHub();
        using BarModel model = Model();
        hub.Attach(model);
        hub.Replace([new SignalSource("battery")]);

        hub.Dispose();
        hub.Signal("battery", ["87"]);

        Assert.False(hub.ListensForSignals);
        Assert.Null(model.GetValue("battery"));
    }

    [Fact]
    public void TheLoaderReadsASignalSource()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                source "battery" kind="signal"
                source "bat" kind="signal" signal="ayn.battery"
                profile "default" { height 30 }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code is "TAJ0018" or "TAJ0027" or "TAJ0028");

        ISource[] sources = [.. TajConfigLoader.CreateSources(config.Sources)];

        SignalSource battery = Assert.IsType<SignalSource>(Assert.Single(sources, s => s.Name == "battery"));
        SignalSource bat = Assert.IsType<SignalSource>(Assert.Single(sources, s => s.Name == "bat"));

        Assert.Equal("battery", battery.Signal);
        Assert.Equal("ayn.battery", bat.Signal);
    }

    [Fact]
    public void TheAnnounceCommandIsASignalTheWindowManagerParses()
    {
        // The bar sends it on connecting; a publisher hears `signal "announce"`. The
        // spelling is the protocol's, so both ends agree, and it must survive the
        // window manager's own parser or nothing would be announced.
        Assert.True(CommandParser.TryParse(SignalSource.AnnounceCommand, default, out WmCommand? command, out Diagnostic? problem), problem?.Message);

        SignalCommand signal = Assert.IsType<SignalCommand>(command);

        Assert.Equal(IpcProtocol.AnnounceSignal, signal.Signal);
        Assert.Empty(signal.Arguments);
    }
}
