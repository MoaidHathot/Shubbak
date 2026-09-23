using Ayn.Core;
using Shubbak.Config;

namespace Ayn.Core.Tests;

/// <summary>
/// The facts the watcher grew, the rules about particular programs, and the renewal
/// of what it holds.
/// </summary>
/// <remarks>
/// <para>
/// Three facts became ten. The new ones - the screen being captured, the speaker
/// muted, the machine on battery, low, lid shut or unattended, the theme dark - are
/// off until the file names them, so nothing changes for a file that never did.
/// </para>
/// <para>
/// A program can be told not to count, and a program can be given a context of its
/// own: <c>camera { ignore "obs64.exe"; by "ms-teams.exe" "in-a-call" }</c>. Both are
/// judged in the provider, where a test can hand it a reading and read the answer.
/// </para>
/// </remarks>
public sealed class ProviderRulesAndRenewalTests
{
    private static readonly AynConfig Defaults = new(Settle: TimeSpan.FromMilliseconds(500));

    private static Reading Camera(params string[] apps) => new(apps, []);

    // ---- ignoring -----------------------------------------------------------

    [Fact]
    public void AnIgnoredProgramDoesNotCountAsUse()
    {
        // The recording tool that keeps the camera open all day is not a meeting.
        var provider = new Provider(Defaults with { CameraIgnores = ["obs64.exe"] });

        provider.Observe(Camera("obs64.exe"), 1_000);

        Assert.Empty(provider.Due(2_000));
        Assert.Null(provider.Pending(2_000));
    }

    [Fact]
    public void AnIgnoredProgramBesideARealOneLeavesTheRealOneCounted()
    {
        var provider = new Provider(Defaults with { CameraIgnores = ["obs64.exe"] });

        provider.Observe(Camera("obs64.exe", "ms-teams.exe"), 1_000);

        ProviderAction hold = Assert.Single(provider.Due(2_000));

        Assert.True(hold.Hold);
        Assert.Equal("camera in use by ms-teams.exe", hold.Because);
    }

    [Fact]
    public void IgnorePatternsHaveWildcardsAndNoCase()
    {
        Assert.True(Provider.AppMatches("*teams*", "MS-Teams.exe"));
        Assert.True(Provider.AppMatches("obs64.exe", "OBS64.EXE"));
        Assert.True(Provider.AppMatches("Microsoft.Windows?amera", "Microsoft.WindowsCamera"));
        Assert.False(Provider.AppMatches("teams", "ms-teams.exe"));
    }

    // ---- by rules -----------------------------------------------------------

    [Fact]
    public void AByRuleHoldsItsOwnContextWhileThatProgramHasTheDevice()
    {
        var provider = new Provider(Defaults with
        {
            AppRules = [new AppRule(DeviceKind.Camera, "ms-teams.exe", "in-a-call")],
        });

        provider.Observe(Camera("ms-teams.exe"), 1_000);

        IReadOnlyList<ProviderAction> due = provider.Due(2_000);

        // The plain fact and the rule both hold: two contexts, two commands.
        Assert.Equal(2, due.Count);

        ProviderAction rule = Assert.Single(due, a => a.App is not null);

        Assert.Equal("in-a-call", rule.Context);
        Assert.Equal("ms-teams.exe", rule.App);
        Assert.Equal("context --set \"in-a-call\" --lease", rule.Command);
        Assert.Contains("rule for ms-teams.exe", rule.Because, StringComparison.Ordinal);
        Assert.True(provider.IsHeld(Fact.CameraInUse, "ms-teams.exe"));
    }

    [Fact]
    public void AByRuleIsNotHeldForSomeOtherProgram()
    {
        var provider = new Provider(Defaults with
        {
            AppRules = [new AppRule(DeviceKind.Camera, "ms-teams.exe", "in-a-call")],
        });

        provider.Observe(Camera("zoom.exe"), 1_000);

        ProviderAction only = Assert.Single(provider.Due(2_000));

        Assert.Null(only.App);
        Assert.Equal("camera-in-use", only.Context);
    }

    [Fact]
    public void AByRuleLetsGoWhenItsProgramDoesEvenIfAnotherStillHasTheDevice()
    {
        var provider = new Provider(Defaults with
        {
            AppRules = [new AppRule(DeviceKind.Camera, "ms-teams.exe", "in-a-call")],
        });

        provider.Observe(Camera("ms-teams.exe", "zoom.exe"), 1_000);
        _ = provider.Due(2_000);

        provider.Observe(Camera("zoom.exe"), 3_000);

        ProviderAction release = Assert.Single(provider.Due(4_000));

        Assert.False(release.Hold);
        Assert.Equal("in-a-call", release.Context);
        Assert.False(provider.IsHeld(Fact.CameraInUse, "ms-teams.exe"));
        Assert.True(provider.IsHeld(Fact.CameraInUse));
    }

    [Fact]
    public void AByRuleAddedByAReloadIsJudgedAgainstTheLastReadingAtOnce()
    {
        // A program already on the camera is noticed without waiting for it to do
        // something, and the settle time counts as served: the desk has said so
        // already.
        var provider = new Provider(Defaults);

        provider.Observe(Camera("ms-teams.exe"), 1_000);
        _ = provider.Due(2_000);

        Assert.Empty(provider.Reconfigure(Defaults with
        {
            AppRules = [new AppRule(DeviceKind.Camera, "ms-teams.exe", "in-a-call")],
        }));

        IReadOnlyList<ProviderAction> due = provider.Due(2_001);

        Assert.Contains(due, a => a.Context == "in-a-call" && a.Hold);
    }

    [Fact]
    public void AByRuleDroppedByAReloadHandsBackWhatItHeld()
    {
        var provider = new Provider(Defaults with
        {
            AppRules = [new AppRule(DeviceKind.Camera, "ms-teams.exe", "in-a-call")],
        });

        provider.Observe(Camera("ms-teams.exe"), 1_000);
        _ = provider.Due(2_000);

        ProviderAction release = Assert.Single(provider.Reconfigure(Defaults));

        Assert.False(release.Hold);
        Assert.Equal("in-a-call", release.Context);
        Assert.False(provider.IsHeld(Fact.CameraInUse, "ms-teams.exe"));
    }

    [Fact]
    public void ARefusedRuleIsBookedAgainstTheRuleNotTheFact()
    {
        var provider = new Provider(Defaults with
        {
            AppRules = [new AppRule(DeviceKind.Camera, "ms-teams.exe", "in-a-call")],
        });

        provider.Observe(Camera("ms-teams.exe"), 1_000);
        IReadOnlyList<ProviderAction> due = provider.Due(2_000);

        provider.Sent(due.Single(a => a.App is not null), SendOutcome.Refused);

        Assert.False(provider.IsHeld(Fact.CameraInUse, "ms-teams.exe"));
        Assert.True(provider.IsHeld(Fact.CameraInUse));
    }

    // ---- the new facts ------------------------------------------------------

    [Fact]
    public void TheNewFactsHoldWhenTheReadingSaysSoAndDoNotSettle()
    {
        var provider = new Provider(Defaults with
        {
            SpeakerMuted = "speaker-muted",
            OnBattery = "on-battery",
            LidClosed = "lid-closed",
            UserAway = "user-away",
            DarkTheme = "dark-theme",
        });

        provider.Observe(new Reading([], [],
            SpeakerMuted: true,
            Power: new PowerReading(OnBattery: true, LidClosed: true, UserAway: true),
            DarkTheme: true), 1_000);

        // Due at once: none of these settle.
        string[] contexts = [.. provider.Due(1_000).Select(a => a.Context).Order()];

        Assert.Equal(["dark-theme", "lid-closed", "on-battery", "speaker-muted", "user-away"], contexts);
    }

    [Fact]
    public void ScreenCaptureSettlesLikeTheOtherDevices()
    {
        var provider = new Provider(Defaults with { ScreenCaptured = "sharing" });

        provider.Observe(new Reading([], [], ScreenApps: ["ms-teams.exe"]), 1_000);

        Assert.Empty(provider.Due(1_100));

        ProviderAction hold = Assert.Single(provider.Due(1_500));

        Assert.Equal("sharing", hold.Context);
        Assert.Equal("screen captured by ms-teams.exe", hold.Because);
    }

    [Fact]
    public void LowBatteryIsTheReadingAgainstTheFilesThreshold()
    {
        var provider = new Provider(Defaults with { BatteryLow = "battery-low", BatteryLowPercent = 15 });

        provider.Observe(new Reading([], [], Power: new PowerReading(BatteryPercent: 20)), 1_000);
        Assert.Empty(provider.Due(1_000));

        provider.Observe(new Reading([], [], Power: new PowerReading(BatteryPercent: 15)), 2_000);
        ProviderAction hold = Assert.Single(provider.Due(2_000));

        Assert.Equal("battery-low", hold.Context);
        Assert.Equal("battery low", hold.Because);

        // No battery at all is not a low one.
        provider.Observe(new Reading([], [], Power: PowerReading.Mains), 3_000);
        Assert.False(provider.Due(3_000).Single().Hold);
    }

    [Fact]
    public void AFactTheFileDoesNotNameIsNeverSpokenOf()
    {
        var provider = new Provider(Defaults);

        provider.Observe(new Reading([], [], SpeakerMuted: true, Power: new PowerReading(OnBattery: true), DarkTheme: true), 1_000);

        Assert.Empty(provider.Due(5_000));
        Assert.Null(provider.Pending(5_000));
    }

    // ---- renewal ------------------------------------------------------------

    [Fact]
    public void WithRenewalEveryHoldCarriesATimeToLiveOfTwiceTheInterval()
    {
        var provider = new Provider(Defaults with { Renew = TimeSpan.FromSeconds(60) });

        provider.Observe(Camera("ms-teams.exe"), 1_000);

        ProviderAction hold = Assert.Single(provider.Due(2_000));

        Assert.Equal("context --set \"camera-in-use\" --lease --ttl 120s", hold.Command);
    }

    [Fact]
    public void AHeldContextIsAssertedAgainWhenTheIntervalHasPassed()
    {
        var provider = new Provider(Defaults with { Renew = TimeSpan.FromSeconds(60) });

        provider.Observe(Camera("ms-teams.exe"), 1_000);
        _ = provider.Due(2_000);

        Assert.Empty(provider.Due(30_000));
        Assert.Equal(TimeSpan.FromSeconds(32), provider.Pending(30_000));

        ProviderAction renewal = Assert.Single(provider.Due(62_000));

        Assert.True(renewal.Hold);
        Assert.Equal("camera-in-use", renewal.Context);
        Assert.StartsWith("renewing", renewal.Because, StringComparison.Ordinal);

        // And again an interval later, not at once.
        Assert.Empty(provider.Due(63_000));
        Assert.Single(provider.Due(122_000));
    }

    [Fact]
    public void AReleasedContextIsNotRenewed()
    {
        var provider = new Provider(Defaults with { Renew = TimeSpan.FromSeconds(60) });

        provider.Observe(Camera("ms-teams.exe"), 1_000);
        _ = provider.Due(2_000);
        provider.Observe(Camera(), 3_000);
        _ = provider.Due(4_000);

        Assert.Empty(provider.Due(200_000));
        Assert.Null(provider.Pending(200_000));
    }

    [Fact]
    public void WithoutRenewalNothingIsEverDueForAHeldContext()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("ms-teams.exe"), 1_000);
        ProviderAction hold = Assert.Single(provider.Due(2_000));

        Assert.Equal("context --set \"camera-in-use\" --lease", hold.Command);
        Assert.Null(provider.Pending(2_000));
        Assert.Empty(provider.Due(1_000_000));
    }

    // ---- signals ------------------------------------------------------------

    [Fact]
    public void TheSpeakerTakesTheSameSignalsAsTheMicrophone()
    {
        SignalRequest? request = SignalRequest.Parse(["speaker", "toggle"], out string? refusal);

        Assert.Null(refusal);
        Assert.Equal(new SignalRequest("speaker", "toggle-mute"), request);

        Assert.Equal("speaker", SignalRequest.Parse(["output", "mute"], out _)?.Subject);
        Assert.Contains("speaker", SignalRequest.Parse(["speaker"], out string? missingVerb) is null ? missingVerb! : string.Empty, StringComparison.Ordinal);
    }
}

/// <summary>The loader's reading of the new blocks and the things it now points out.</summary>
public sealed class AynConfigLoaderExtensionTests
{
    private static AynConfigLoad Load(string source) => AynConfigLoader.Validate(source);

    private const string Declared = """
        contexts {
            context "camera-in-use" {}
            context "microphone-in-use" {}
            context "microphone-muted" {}
            context "sharing" {}
            context "quiet" {}
            context "on-battery" {}
            context "battery-low" {}
            context "lid-closed" {}
            context "user-away" {}
            context "dark-theme" {}
            context "in-a-call" {}
            context "busy" {}
        }
        """;

    [Fact]
    public void EveryNewBlockIsRead()
    {
        AynConfigLoad load = Load(Declared + """
            ayn {
                camera { in-use "camera-in-use"; ignore "obs64.exe" "*camera*"; by "ms-teams.exe" "in-a-call" }
                screen { captured "sharing"; ignore "snip*" }
                speaker { muted "quiet" }
                power { on-battery "on-battery"; battery-low "battery-low"; battery-low-at 15; lid-closed "lid-closed"; user-away "user-away" }
                theme { dark "dark-theme" }
                renew 30
            }
            """);

        Assert.Empty(load.Diagnostics);

        AynConfig config = load.Config;

        Assert.Equal("sharing", config.ScreenCaptured);
        Assert.Equal("quiet", config.SpeakerMuted);
        Assert.Equal("on-battery", config.OnBattery);
        Assert.Equal("battery-low", config.BatteryLow);
        Assert.Equal(15, config.BatteryLowPercent);
        Assert.Equal("lid-closed", config.LidClosed);
        Assert.Equal("user-away", config.UserAway);
        Assert.Equal("dark-theme", config.DarkTheme);
        Assert.Equal(TimeSpan.FromSeconds(30), config.Renew);
        Assert.Equal(TimeSpan.FromSeconds(60), config.LeaseTtl);
        Assert.Equal(["obs64.exe", "*camera*"], config.CameraIgnores);
        Assert.Equal(["snip*"], config.ScreenIgnores);
        Assert.Equal([new AppRule(DeviceKind.Camera, "ms-teams.exe", "in-a-call")], config.AppRules);

        Assert.True(config.NeedsPower);
        Assert.True(config.NeedsTheme);
        Assert.True(config.NeedsSpeakerEndpoint);
        Assert.True(config.Watches(DeviceKind.Screen));
    }

    [Fact]
    public void TheNewFactsAreOffUntilNamed()
    {
        AynConfig config = Load("ayn { settle 500 }").Config;

        Assert.Null(config.ScreenCaptured);
        Assert.Null(config.SpeakerMuted);
        Assert.Null(config.OnBattery);
        Assert.Null(config.DarkTheme);
        Assert.Null(config.Renew);
        Assert.False(config.NeedsPower);
        Assert.False(config.Watches(DeviceKind.Screen));
        Assert.True(config.NeedsConsentStore);
    }

    [Fact]
    public void ABlockTurnedOffReportsNothing()
    {
        AynConfig config = Load("ayn { power #false\n theme #false }").Config;

        Assert.False(config.NeedsPower);
        Assert.False(config.NeedsTheme);
    }

    [Fact]
    public void TwoFactsHoldingOneContextArePointedOut()
    {
        // Each hands its context back with --auto when it goes false, which drops the
        // pin the other set: closing the camera would end "busy" while the microphone
        // is still open.
        AynConfigLoad load = Load(Declared + """
            ayn {
                camera { in-use "busy" }
                microphone { in-use "busy" }
            }
            """);

        Diagnostic shared = Assert.Single(load.Diagnostics, d => d.Code == "AYN0006");

        Assert.Contains("busy", shared.Message, StringComparison.Ordinal);
        Assert.Contains("camera-in-use", shared.Message, StringComparison.Ordinal);
        Assert.Contains("microphone-in-use", shared.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleSharingAFactsContextIsPointedOutToo()
    {
        AynConfigLoad load = Load(Declared + """
            ayn {
                camera { in-use "camera-in-use"; by "ms-teams.exe" "camera-in-use" }
            }
            """);

        Assert.Contains(load.Diagnostics, d => d.Code == "AYN0006");
    }

    [Fact]
    public void TheShorthandIsReadAsTheNameAndSaidSo()
    {
        AynConfigLoad load = Load(Declared + """
            ayn { camera "in-a-call" }
            """);

        Diagnostic shorthand = Assert.Single(load.Diagnostics, d => d.Code == "AYN0007");

        Assert.Contains("camera { in-use \"in-a-call\" }", shorthand.Message, StringComparison.Ordinal);
        Assert.Equal("in-a-call", load.Config.CameraInUse);
    }

    [Fact]
    public void AByRuleMissingItsHalfIsIgnoredAndSaidSo()
    {
        AynConfigLoad load = Load(Declared + """
            ayn { camera { by "ms-teams.exe" } }
            """);

        Assert.Single(load.Diagnostics, d => d.Code == "AYN0008");
        Assert.Empty(load.Config.AppRules);
    }

    [Fact]
    public void AByRuleNamingAnUndeclaredContextIsPointedOut()
    {
        AynConfigLoad load = Load(Declared + """
            ayn { camera { by "ms-teams.exe" "teams-call" } }
            """);

        Diagnostic undeclared = Assert.Single(load.Diagnostics, d => d.Code == "AYN0005");

        Assert.Contains("teams-call", undeclared.Message, StringComparison.Ordinal);
        Assert.Contains("camera by", undeclared.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("renew \"soon\"", null)]
    [InlineData("renew 0", null)]
    [InlineData("renew #false", null)]
    [InlineData("renew 2", 5)]
    [InlineData("renew #true", 60)]
    [InlineData("renew 45", 45)]
    public void RenewIsSecondsWithAFloorAndAnOff(string setting, int? expectedSeconds)
    {
        AynConfigLoad load = Load($"ayn {{ {setting} }}");

        Assert.Equal(expectedSeconds is { } s ? TimeSpan.FromSeconds(s) : null, load.Config.Renew);

        if (setting is "renew \"soon\"" or "renew 2")
            Assert.Single(load.Diagnostics, d => d.Code == "AYN0009");
        else
            Assert.DoesNotContain(load.Diagnostics, d => d.Code == "AYN0009");
    }

    [Theory]
    [InlineData("battery-low-at 0")]
    [InlineData("battery-low-at 101")]
    [InlineData("battery-low-at \"low\"")]
    public void ABadBatteryThresholdIsPointedOutAndTheDefaultUsed(string setting)
    {
        AynConfigLoad load = Load($"ayn {{ power {{ {setting} }} }}");

        Assert.Single(load.Diagnostics, d => d.Code == "AYN0010");
        Assert.Equal(20, load.Config.BatteryLowPercent);
    }

    [Fact]
    public void AnUnknownSettingInANewBlockIsPointedOutWithAGuess()
    {
        AynConfigLoad load = Load("ayn { power { on-batery \"x\" } }");

        Diagnostic unknown = Assert.Single(load.Diagnostics, d => d.Code == "AYN0001");

        Assert.Contains("on-battery", unknown.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadingFromAStoreIncludesTheScreen()
    {
        var store = new FakeStore();
        store[DeviceKind.Screen] = [new ConsentEntry("ms-teams.exe", 10, 0)];

        Reading reading = Reading.From(store);

        Assert.Equal(["ms-teams.exe"], reading.ScreenApps);
        Assert.True(reading.Holds(Fact.ScreenCaptured));
    }

    private sealed class FakeStore : IConsentStore
    {
        private readonly Dictionary<DeviceKind, IReadOnlyList<ConsentEntry>> _entries = [];

        public IReadOnlyList<ConsentEntry> this[DeviceKind device]
        {
            set => _entries[device] = value;
        }

        public IReadOnlyList<ConsentEntry> Read(DeviceKind device) => _entries.GetValueOrDefault(device, []);
    }
}
