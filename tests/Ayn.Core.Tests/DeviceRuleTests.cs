using Ayn.Core;
using Shubbak.Config;

namespace Ayn.Core.Tests;

/// <summary>
/// The <c>device</c> rule: a context held while the default speaker or microphone is
/// a particular device, matched by the name Windows shows.
/// </summary>
/// <remarks>
/// A headset that is plugged in becomes the default speaker and microphone, and a
/// file that wants the bar's volume widget quiet, or a profile switched, while sound
/// goes to the headset had no way to know. The consent store says who uses a device,
/// the endpoint said whether it is muted; which device it is was the missing fact.
/// </remarks>
public sealed class DeviceRuleTests
{
    private static readonly AynConfig Defaults = new(Settle: TimeSpan.FromMilliseconds(500));

    private static readonly AynConfig HeadsetRules = Defaults with
    {
        DeviceRules =
        [
            new DeviceRule(Fact.SpeakerDevice, "*barracuda*", "on-headset"),
            new DeviceRule(Fact.MicrophoneDevice, "*webcam*", "webcam-mic"),
        ],
    };

    private static Reading Desk(string? speaker, string? microphone = null) =>
        Reading.Idle with { SpeakerDeviceName = speaker, MicrophoneDeviceName = microphone };

    // ---- the provider ---------------------------------------------------------

    [Fact]
    public void ARuleHoldsWhileTheDefaultDeviceMatchesItsPattern()
    {
        var provider = new Provider(HeadsetRules);

        provider.Observe(Desk("Speakers (Razer Barracuda Pro 2.4)"), 1_000);

        ProviderAction only = Assert.Single(provider.Due(1_000));

        Assert.True(only.Hold);
        Assert.Equal("on-headset", only.Context);
        Assert.Equal(Fact.SpeakerDevice, only.Fact);
        Assert.Equal("*barracuda*", only.App);
        Assert.Equal("speaker is \"Speakers (Razer Barracuda Pro 2.4)\" (rule for *barracuda*)", only.Because);
        Assert.True(provider.IsHeld(Fact.SpeakerDevice, "*barracuda*"));
    }

    [Fact]
    public void WhichDeviceIsTheDefaultDoesNotSettle()
    {
        // Use settles because a call opens and closes the camera while it sets up.
        // Plugging a headset in is one event, and the person who plugged it in is
        // looking at the bar. Due at the very tick it was observed.
        var provider = new Provider(HeadsetRules);

        provider.Observe(Desk("Speakers (Razer Barracuda Pro 2.4)"), 1_000);

        Assert.Single(provider.Due(1_000));
        Assert.False(Fact.SpeakerDevice.Settles());
        Assert.False(Fact.MicrophoneDevice.Settles());
    }

    [Fact]
    public void ARuleLetsGoWhenTheDefaultMovesAndSaysWhereTo()
    {
        var provider = new Provider(HeadsetRules);

        provider.Observe(Desk("Speakers (Razer Barracuda Pro 2.4)"), 1_000);
        _ = provider.Due(1_000);

        // Unplugged: Windows moves the default to the monitor.
        provider.Observe(Desk("LG ULTRAGEAR (NVIDIA High Definition Audio)"), 2_000);

        ProviderAction release = Assert.Single(provider.Due(2_000));

        Assert.False(release.Hold);
        Assert.Equal("on-headset", release.Context);
        Assert.Equal("speaker is now \"LG ULTRAGEAR (NVIDIA High Definition Audio)\"", release.Because);
        Assert.False(provider.IsHeld(Fact.SpeakerDevice, "*barracuda*"));
    }

    [Fact]
    public void NoDeviceAtAllIsNoMatch()
    {
        var provider = new Provider(HeadsetRules);

        provider.Observe(Desk("Speakers (Razer Barracuda Pro 2.4)"), 1_000);
        _ = provider.Due(1_000);

        provider.Observe(Desk(speaker: null), 2_000);

        ProviderAction release = Assert.Single(provider.Due(2_000));

        Assert.False(release.Hold);
        Assert.Equal("no speaker", release.Because);
    }

    [Fact]
    public void TheMicrophoneAndTheSpeakerAreJudgedApart()
    {
        // The same headset is usually both, but the rules are about one flow each: a
        // webcam's microphone with the monitor's speakers holds the microphone rule
        // alone.
        var provider = new Provider(HeadsetRules);

        provider.Observe(Desk("LG ULTRAGEAR (NVIDIA High Definition Audio)", "Microphone (Logi Webcam C920)"), 1_000);

        ProviderAction only = Assert.Single(provider.Due(1_000));

        Assert.Equal("webcam-mic", only.Context);
        Assert.Equal(Fact.MicrophoneDevice, only.Fact);
        Assert.Equal("microphone is \"Microphone (Logi Webcam C920)\" (rule for *webcam*)", only.Because);
    }

    [Fact]
    public void PatternsHaveWildcardsAndNoCase()
    {
        var provider = new Provider(Defaults with
        {
            DeviceRules = [new DeviceRule(Fact.SpeakerDevice, "speakers (RAZER*", "on-headset")],
        });

        provider.Observe(Desk("Speakers (Razer Barracuda Pro 2.4)"), 1_000);

        Assert.Single(provider.Due(1_000));
    }

    [Fact]
    public void ARuleAddedByAReloadIsJudgedAgainstTheLastReadingAtOnce()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Desk("Speakers (Razer Barracuda Pro 2.4)"), 1_000);
        Assert.Empty(provider.Due(1_000));

        Assert.Empty(provider.Reconfigure(HeadsetRules));

        ProviderAction only = Assert.Single(provider.Due(1_000));

        Assert.Equal("on-headset", only.Context);
    }

    [Fact]
    public void TheBareFactIsNeverSpokenOf()
    {
        // The fact without a pattern would be "there is a speaker"; the file cannot
        // name a context for it, and the slot that exists for uniformity is never due.
        var provider = new Provider(HeadsetRules);

        provider.Observe(Desk("Speakers (Razer Barracuda Pro 2.4)"), 1_000);

        Assert.All(provider.Due(1_000), action => Assert.NotNull(action.App));
        Assert.Null(HeadsetRules.ContextFor(Fact.SpeakerDevice));
        Assert.False(Reading.Idle.Holds(Fact.SpeakerDevice));
    }

    [Fact]
    public void TheKeyReadsAsMatchingNotBy()
    {
        // "speaker-device by *barracuda*" would say a program; the pattern is a name.
        Assert.Equal("speaker-device matching *barracuda*", new FactKey(Fact.SpeakerDevice, "*barracuda*").ToString());
        Assert.Equal("camera-in-use by ms-teams.exe", new FactKey(Fact.CameraInUse, "ms-teams.exe").ToString());
    }

    // ---- the loader -----------------------------------------------------------

    private const string Declared = """
        contexts {
            context "camera-in-use" {}
            context "microphone-in-use" {}
            context "microphone-muted" {}
            context "on-headset" {}
            context "webcam-mic" {}
            context "quiet" {}
        }
        """;

    [Fact]
    public void DeviceRulesAreReadFromBothBlocks()
    {
        AynConfigLoad load = AynConfigLoader.Validate(Declared + """
            ayn {
                microphone { device "*webcam*" "webcam-mic" }
                speaker { muted "quiet"; device "*barracuda*" "on-headset" }
            }
            """);

        Assert.Empty(load.Diagnostics);

        Assert.Equal(
            [
                new DeviceRule(Fact.MicrophoneDevice, "*webcam*", "webcam-mic"),
                new DeviceRule(Fact.SpeakerDevice, "*barracuda*", "on-headset"),
            ],
            load.Config.DeviceRules);

        Assert.True(load.Config.WatchesAnything);
    }

    [Fact]
    public void ASpeakerRuleAloneMakesTheSpeakerFollowed()
    {
        // The speaker's endpoint was opened only for its mute; a rule about which
        // speaker it is needs the same endpoint, so the name can be read.
        AynConfig config = AynConfigLoader.Load(Declared + """
            ayn {
                microphone #false
                speaker { device "*barracuda*" "on-headset" }
            }
            """);

        Assert.Null(config.SpeakerMuted);
        Assert.True(config.NeedsSpeakerEndpoint);
        Assert.False(config.NeedsAudioEndpoint);
    }

    [Fact]
    public void AMicrophoneRuleAloneOpensTheAudioEndpoint()
    {
        AynConfig config = AynConfigLoader.Load(Declared + """
            ayn {
                microphone { muted #false; device "*webcam*" "webcam-mic" }
            }
            """);

        Assert.True(config.NeedsAudioEndpoint);
        Assert.False(config.NeedsSpeakerEndpoint);
    }

    [Fact]
    public void AHalfWrittenRuleIsPointedOutAndIgnored()
    {
        AynConfigLoad load = AynConfigLoader.Validate(Declared + """
            ayn {
                speaker { device "*barracuda*" }
            }
            """);

        Diagnostic warning = Assert.Single(load.Diagnostics);

        Assert.Equal("AYN0011", warning.Code);
        Assert.Contains("speaker device", warning.Message, StringComparison.Ordinal);
        Assert.Empty(load.Config.DeviceRules);
    }

    [Fact]
    public void ARuleNamingAnUndeclaredContextIsPointedOut()
    {
        AynConfigLoad load = AynConfigLoader.Validate(Declared + """
            ayn {
                speaker { device "*barracuda*" "on-headphones" }
            }
            """);

        Diagnostic warning = Assert.Single(load.Diagnostics);

        Assert.Equal("AYN0005", warning.Code);
        Assert.Contains("speaker device \"*barracuda*\"", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleSharingAFactsContextIsPointedOut()
    {
        // The rule hands the context back when the headset goes, taking the mute's pin
        // with it; the same trap as two facts on one context, and the same warning.
        AynConfigLoad load = AynConfigLoader.Validate(Declared + """
            ayn {
                speaker { muted "quiet"; device "*barracuda*" "quiet" }
            }
            """);

        Diagnostic warning = Assert.Single(load.Diagnostics);

        Assert.Equal("AYN0006", warning.Code);
        Assert.Contains("speaker device \"*barracuda*\"", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceIsAKnownKeyOfBothBlocks()
    {
        Assert.Contains("device", AynConfigLoader.KnownSpeakerKeys);
        Assert.Contains("device", AynConfigLoader.KnownMicrophoneKeys);
        Assert.DoesNotContain("device", AynConfigLoader.KnownCameraKeys);
        Assert.DoesNotContain("device", AynConfigLoader.KnownScreenKeys);
    }
}
