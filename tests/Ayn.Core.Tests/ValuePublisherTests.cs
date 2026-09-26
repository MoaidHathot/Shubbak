using Ayn.Core;
using Shubbak.Config;
using Shubbak.Core.Commands;

namespace Ayn.Core.Tests;

/// <summary>
/// Values: readings the watcher publishes as words for the bar, by way of a signal,
/// where a fact is a yes or a no the window manager holds.
/// </summary>
/// <remarks>
/// <para>
/// The watcher read the battery's percentage for <c>battery-low</c> and had nowhere
/// to put the number, because a context is a boolean by design. A value goes out as
/// <c>signal "battery" "41"</c>, which the window manager carries without reading and
/// a <c>source "battery" kind="signal"</c> on the bar shows.
/// </para>
/// <para>
/// What the bar relies on: a value is said when it changes and not otherwise; every
/// value is said again on <see cref="ValuePublisher.Forget"/>, which is what a bar
/// connecting late asks for; a lost window manager forgets everything; a name the
/// file drops is cleared under its old spelling once. And the command spelled must
/// survive the window manager's own parser with the words intact, including a device
/// name with spaces and brackets in it.
/// </para>
/// </remarks>
public sealed class ValuePublisherTests
{
    private static readonly AynConfig Publishing = new(
        BatteryPercentSignal: "battery",
        SpeakerDeviceSignal: "speaker",
        MicrophoneDeviceSignal: "microphone");

    private static Reading Desk(int? battery = null, string? speaker = null, string? microphone = null) =>
        Reading.Idle with
        {
            Power = battery is { } percent ? new PowerReading(OnBattery: true, BatteryPercent: percent) : null,
            SpeakerDeviceName = speaker,
            MicrophoneDeviceName = microphone,
        };

    // ---- what is due ----------------------------------------------------------

    [Fact]
    public void NothingIsPublishedUnlessTheFileNamesASignal()
    {
        var publisher = new ValuePublisher(new AynConfig());

        publisher.Observe(Desk(battery: 87, speaker: "Speakers", microphone: "Mic"));

        Assert.Empty(publisher.Due());
        Assert.False(publisher.PublishesAnything);
        Assert.False(new AynConfig().PublishesAnyValue);
    }

    [Fact]
    public void EveryNamedValueIsDueOnceAtFirst()
    {
        var publisher = new ValuePublisher(Publishing);

        publisher.Observe(Desk(battery: 87, speaker: "Speakers (Realtek(R) Audio)", microphone: "Headset Microphone"));

        IReadOnlyList<ValueAction> due = publisher.Due();

        Assert.Equal(3, due.Count);
        Assert.Equal("87", Assert.Single(due, a => a.Value == Value.BatteryPercent).Text);
        Assert.Equal("Speakers (Realtek(R) Audio)", Assert.Single(due, a => a.Value == Value.SpeakerDeviceName).Text);
        Assert.Equal("Headset Microphone", Assert.Single(due, a => a.Value == Value.MicrophoneDeviceName).Text);
        Assert.True(publisher.PublishesAnything);
    }

    [Fact]
    public void AValueIsSaidAgainOnlyWhenItChanges()
    {
        var publisher = new ValuePublisher(Publishing with { SpeakerDeviceSignal = null, MicrophoneDeviceSignal = null });

        publisher.Observe(Desk(battery: 87));
        Assert.Single(publisher.Due());

        publisher.Observe(Desk(battery: 87));
        Assert.Empty(publisher.Due());

        publisher.Observe(Desk(battery: 86));
        ValueAction changed = Assert.Single(publisher.Due());

        Assert.Equal("86", changed.Text);
        Assert.Empty(publisher.Due());
    }

    [Fact]
    public void NoBatteryAndNoDeviceAreEmptyWhichHidesTheWidget()
    {
        var publisher = new ValuePublisher(Publishing);

        publisher.Observe(Desk());

        foreach (ValueAction action in publisher.Due()) Assert.Equal(string.Empty, action.Text);
    }

    [Fact]
    public void ForgetSaysEverythingAgainWhetherOrNotItChanged()
    {
        // A bar that connects after the values were last said asks with
        // `signal "announce"`, and the loop answers by forgetting.
        var publisher = new ValuePublisher(Publishing);
        publisher.Observe(Desk(battery: 87, speaker: "Speakers", microphone: "Mic"));
        Assert.Equal(3, publisher.Due().Count);
        Assert.Empty(publisher.Due());

        publisher.Forget();

        Assert.Equal(3, publisher.Due().Count);
    }

    [Fact]
    public void AnUnreachableWindowManagerForgetsEverything()
    {
        // The one that comes back has heard nothing.
        var publisher = new ValuePublisher(Publishing);
        publisher.Observe(Desk(battery: 87, speaker: "Speakers", microphone: "Mic"));
        IReadOnlyList<ValueAction> first = publisher.Due();

        publisher.Sent(first[0], SendOutcome.Unreachable);

        Assert.Equal(3, publisher.Due().Count);
    }

    [Fact]
    public void ARefusalIsNotRetried()
    {
        // The same words would be refused the same way; a refused signal is a bug in
        // the spelling, not a state of the desk.
        var publisher = new ValuePublisher(Publishing with { SpeakerDeviceSignal = null, MicrophoneDeviceSignal = null });
        publisher.Observe(Desk(battery: 87));
        ValueAction only = Assert.Single(publisher.Due());

        publisher.Sent(only, SendOutcome.Refused);

        Assert.Empty(publisher.Due());
    }

    // ---- the command ----------------------------------------------------------

    [Fact]
    public void TheCommandIsASignalWithOneArgumentThatTheParserReadsBack()
    {
        var action = new ValueAction(Value.SpeakerDeviceName, "speaker", "Speakers (Realtek(R) Audio)");

        Assert.Equal("signal speaker \"Speakers (Realtek(R) Audio)\"", action.Command);

        Assert.True(CommandParser.TryParse(action.Command, default, out WmCommand? command, out Diagnostic? problem), problem?.Message);
        SignalCommand signal = Assert.IsType<SignalCommand>(command);

        Assert.Equal("speaker", signal.Signal);
        Assert.Equal(["Speakers (Realtek(R) Audio)"], signal.Arguments);
    }

    [Fact]
    public void AnEmptyValueIsTheBareSignalWhichTheBarReadsAsEmpty()
    {
        // The command language cannot spell an empty argument - `""` is read back as
        // no token - so the clear is the bare name, and the bar reads no arguments as
        // the empty value.
        var action = new ValueAction(Value.BatteryPercent, "battery", string.Empty);

        Assert.Equal("signal battery", action.Command);

        Assert.True(CommandParser.TryParse(action.Command, default, out WmCommand? command, out _));
        Assert.Empty(Assert.IsType<SignalCommand>(command).Arguments);
    }

    [Fact]
    public void ANameWithBothKindsOfQuoteIsSpelledRatherThanDropped()
    {
        // The command language has no escape, so a value with both quotes cannot be
        // written as it is. The double quotes become the typographic kind; a name shown
        // slightly wrong beats a widget that is blank.
        var action = new ValueAction(Value.SpeakerDeviceName, "speaker", "Bob's \"Speaker\"");

        Assert.True(CommandParser.TryParse(action.Command, default, out WmCommand? command, out Diagnostic? problem), problem?.Message);
        Assert.Equal(["Bob's \u201DSpeaker\u201D"], Assert.IsType<SignalCommand>(command).Arguments);
    }

    [Fact]
    public void ASignalNameWithASpaceIsQuoted()
    {
        var action = new ValueAction(Value.BatteryPercent, "my battery", "5");

        Assert.True(CommandParser.TryParse(action.Command, default, out WmCommand? command, out _));
        Assert.Equal("my battery", Assert.IsType<SignalCommand>(command).Signal);
    }

    // ---- reload ---------------------------------------------------------------

    [Fact]
    public void ARenamedValueIsClearedUnderItsOldNameOnce()
    {
        var publisher = new ValuePublisher(Publishing with { SpeakerDeviceSignal = null, MicrophoneDeviceSignal = null });
        publisher.Observe(Desk(battery: 87));
        Assert.Single(publisher.Due());

        IReadOnlyList<ValueAction> cleared = publisher.Reconfigure(publisher.Config with { BatteryPercentSignal = "power" });

        ValueAction clear = Assert.Single(cleared);
        Assert.Equal("battery", clear.Signal);
        Assert.Equal(string.Empty, clear.Text);

        // And said under the new name at once.
        ValueAction renamed = Assert.Single(publisher.Due());
        Assert.Equal("power", renamed.Signal);
        Assert.Equal("87", renamed.Text);
    }

    [Fact]
    public void ADroppedValueIsClearedAndNotSaidAgain()
    {
        var publisher = new ValuePublisher(Publishing with { SpeakerDeviceSignal = null, MicrophoneDeviceSignal = null });
        publisher.Observe(Desk(battery: 87));
        Assert.Single(publisher.Due());

        IReadOnlyList<ValueAction> cleared = publisher.Reconfigure(new AynConfig());

        Assert.Equal("battery", Assert.Single(cleared).Signal);
        Assert.Empty(publisher.Due());
        Assert.False(publisher.PublishesAnything);
    }

    [Fact]
    public void AValueNeverSaidIsNotCleared()
    {
        // The window manager never heard the old name, so no bar shows anything under it.
        var publisher = new ValuePublisher(Publishing);

        Assert.Empty(publisher.Reconfigure(new AynConfig()));
    }

    [Fact]
    public void AValueKeptUnderItsNameIsNotDisturbedByAReload()
    {
        var publisher = new ValuePublisher(Publishing);
        publisher.Observe(Desk(battery: 87, speaker: "S", microphone: "M"));
        publisher.Due();

        Assert.Empty(publisher.Reconfigure(Publishing with { Settle = TimeSpan.FromSeconds(1) }));
        Assert.Empty(publisher.Due());
    }

    // ---- the loader -----------------------------------------------------------

    [Fact]
    public void TheFileNamesTheSignals()
    {
        AynConfig config = AynConfigLoader.Load("""
            ayn {
                power      { battery-percent "bat" }
                speaker    { device-name "out" }
                microphone { device-name "in" }
            }
            """);

        Assert.Equal("bat", config.BatteryPercentSignal);
        Assert.Equal("out", config.SpeakerDeviceSignal);
        Assert.Equal("in", config.MicrophoneDeviceSignal);
        Assert.Equal("bat", config.SignalFor(Value.BatteryPercent));
        Assert.True(config.PublishesAnyValue);
    }

    [Fact]
    public void TrueIsTheSubjectsOwnName()
    {
        AynConfig config = AynConfigLoader.Load("""
            ayn {
                power      { battery-percent #true }
                speaker    { device-name #true }
                microphone { device-name #true }
            }
            """);

        Assert.Equal("battery", config.BatteryPercentSignal);
        Assert.Equal("speaker", config.SpeakerDeviceSignal);
        Assert.Equal("microphone", config.MicrophoneDeviceSignal);
    }

    [Fact]
    public void FalseAndUnsaidAreOff()
    {
        Assert.Null(AynConfigLoader.Load("ayn { power { battery-percent #false } }").BatteryPercentSignal);
        Assert.Null(AynConfigLoader.Load("ayn { power { on-battery \"x\" } }").BatteryPercentSignal);
        Assert.Null(AynConfigLoader.Load("ayn { }").SpeakerDeviceSignal);
    }

    [Fact]
    public void AnEmptyNameIsPointedOutAndOff()
    {
        AynConfigLoad load = AynConfigLoader.Validate("ayn { power { battery-percent \"\" } }");

        Diagnostic warning = Assert.Single(load.Diagnostics, d => d.Code == "AYN0012");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("battery-percent", warning.Message, StringComparison.Ordinal);
        Assert.Contains("kind=\"signal\"", warning.Hint!, StringComparison.Ordinal);
        Assert.Null(load.Config.BatteryPercentSignal);
    }

    [Fact]
    public void ASignalNameIsNotACheckedContextName()
    {
        // The name is whatever the bar's source listens for; the contexts section has
        // nothing to say about it, so no AYN0005.
        AynConfigLoad load = AynConfigLoader.Validate("""
            contexts { context "on-battery" { } }
            ayn { power { on-battery "on-battery"; battery-percent "battery" } }
            """);

        Assert.DoesNotContain(load.Diagnostics, d => d.Code is "AYN0001" or "AYN0005");
    }

    [Fact]
    public void AValueAloneIsSomethingToWatchAndOpensItsSource()
    {
        // A file that names only the battery's percentage still starts the watcher,
        // and still registers for power notifications; one that names only a device
        // name follows that endpoint.
        AynConfig battery = AynConfigLoader.Load("ayn { camera #false; microphone #false; power { battery-percent \"battery\" } }");

        Assert.True(battery.WatchesAnything);
        Assert.True(battery.NeedsPower);
        Assert.False(battery.NeedsAudioEndpoint);
        Assert.False(battery.NeedsSpeakerEndpoint);

        AynConfig speaker = AynConfigLoader.Load("ayn { camera #false; microphone #false; speaker { device-name \"speaker\" } }");

        Assert.True(speaker.NeedsSpeakerEndpoint);
        Assert.False(speaker.NeedsPower);

        AynConfig microphone = AynConfigLoader.Load("ayn { camera #false; microphone { in-use #false; muted #false; device-name \"mic\" } }");

        Assert.True(microphone.NeedsAudioEndpoint);
        Assert.Null(microphone.MicrophoneMuted);
    }

    [Fact]
    public void ADeviceNameOnTheMicrophoneIsNotAnUnknownSetting()
    {
        AynConfigLoad load = AynConfigLoader.Validate("ayn { microphone { device-name \"mic\" } }");

        Assert.DoesNotContain(load.Diagnostics, d => d.Code == "AYN0001");
    }
}
