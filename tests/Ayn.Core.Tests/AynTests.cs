using Ayn.Core;

namespace Ayn.Core.Tests;

/// <summary>Tests for what the watcher decides to tell the window manager.</summary>
public sealed class ProviderTests
{
    private static readonly AynConfig Defaults = new(Settle: TimeSpan.FromMilliseconds(500));

    private static Reading Camera(params string[] apps) => new(apps, []);

    private static Reading Microphone(params string[] apps) => new([], apps);

    [Fact]
    public void NothingInUseMeansNothingToSay()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Reading.Idle, 0);

        Assert.Empty(provider.Due(10_000));
        Assert.Null(provider.Pending(10_000));
    }

    [Fact]
    public void ADeviceComingIntoUseIsHeldWithALeaseAfterItHasSettled()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 1_000);

        // Not yet: a call opens and closes the camera several times while setting up.
        Assert.Empty(provider.Due(1_100));
        Assert.Equal(TimeSpan.FromMilliseconds(400), provider.Pending(1_100));

        ProviderAction action = Assert.Single(provider.Due(1_500));
        Assert.Equal("camera", action.Context);
        Assert.True(action.Hold);
        Assert.Equal("context --set \"camera\" --lease", action.Command);
        Assert.Equal("camera in use by Teams.exe", action.Because);

        // Said once.
        Assert.Empty(provider.Due(2_000));
        Assert.Null(provider.Pending(2_000));
    }

    [Fact]
    public void ADeviceGoingQuietIsHandedBackWithAutoNotClear()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        _ = provider.Due(500);

        provider.Observe(Reading.Idle, 10_000);

        ProviderAction action = Assert.Single(provider.Due(10_500));
        Assert.False(action.Hold);

        // --auto rather than --clear: no pin is left behind, so the report says
        // "nothing has set it" and somebody else's --set is not held off by us.
        Assert.Equal("context --auto \"camera\"", action.Command);
        Assert.Equal("camera no longer in use", action.Because);
    }

    [Fact]
    public void AFlickerInsideTheSettleTimeIsNeverReported()
    {
        var provider = new Provider(Defaults);

        // Opens, closes, opens again in 300 ms: what a call does while it enumerates
        // devices. One hold, at the end, once it has held.
        provider.Observe(Camera("Teams.exe"), 0);
        provider.Observe(Reading.Idle, 100);
        provider.Observe(Camera("Teams.exe"), 300);

        Assert.Empty(provider.Due(400));
        Assert.Empty(provider.Due(700));
        Assert.Single(provider.Due(800));
    }

    [Fact]
    public void AChangeThatReversesItselfBeforeSettlingSaysNothingAtAll()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        _ = provider.Due(500);

        // Camera drops for 200 ms and comes back. The window manager never hears.
        provider.Observe(Reading.Idle, 5_000);
        provider.Observe(Camera("Teams.exe"), 5_200);

        Assert.Empty(provider.Due(6_000));
        Assert.Null(provider.Pending(6_000));
    }

    [Fact]
    public void TheTwoDevicesAreIndependent()
    {
        var provider = new Provider(Defaults);

        provider.Observe(new Reading(["Teams.exe"], ["Teams.exe", "Discord.exe"]), 0);

        IReadOnlyList<ProviderAction> due = provider.Due(500);

        Assert.Equal(2, due.Count);
        Assert.Contains(due, a => a.Command == "context --set \"camera\" --lease");
        Assert.Contains(due, a => a is { Context: "microphone", Hold: true, Because: "microphone in use by Teams.exe, Discord.exe" });

        // The microphone stays open after the camera closes: one hand-back, for the
        // camera alone.
        provider.Observe(Microphone("Teams.exe"), 5_000);

        ProviderAction action = Assert.Single(provider.Due(5_500));
        Assert.Equal("camera", action.Context);
        Assert.False(action.Hold);
    }

    [Fact]
    public void ADeviceTurnedOffInTheFileIsWatchedForNothing()
    {
        var provider = new Provider(new AynConfig(Camera: null, Microphone: "mic", Settle: TimeSpan.FromMilliseconds(100)));

        provider.Observe(new Reading(["Teams.exe"], ["Teams.exe"]), 0);

        ProviderAction action = Assert.Single(provider.Due(100));
        Assert.Equal("mic", action.Context);
        Assert.Null(provider.Pending(100));
    }

    [Fact]
    public void ALostConnectionHoldsAgainAtOnceWhatIsStillInUse()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        _ = provider.Due(500);

        // The window manager restarted; every lease died with it. The settle time was
        // served the first time round, so the hold is due immediately.
        provider.Forget();

        ProviderAction action = Assert.Single(provider.Due(501));
        Assert.True(action.Hold);
        Assert.Equal("camera", action.Context);
    }

    [Fact]
    public void ALostConnectionHandsNothingBackForADeviceThatWentQuiet()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        _ = provider.Due(500);

        // Quiet, then the connection goes before the hand-back was due. There is no
        // pin on the new window manager to hand back.
        provider.Observe(Reading.Idle, 1_000);
        provider.Forget();

        Assert.Empty(provider.Due(2_000));
    }

    [Fact]
    public void RenamingTheContextUnderARunningWatcherReleasesTheOldNameAndHoldsTheNew()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        _ = provider.Due(500);

        ProviderAction release = Assert.Single(provider.Reconfigure(Defaults with { Camera = "on-camera" }));
        Assert.Equal("context --auto \"camera\"", release.Command);
        Assert.Contains("on-camera", release.Because, StringComparison.Ordinal);

        ProviderAction hold = Assert.Single(provider.Due(501));
        Assert.Equal("context --set \"on-camera\" --lease", hold.Command);
    }

    [Fact]
    public void TurningADeviceOffInTheFileReleasesWhatWasHeldForIt()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        _ = provider.Due(500);

        ProviderAction release = Assert.Single(provider.Reconfigure(Defaults with { Camera = null }));
        Assert.Equal("context --auto \"camera\"", release.Command);

        Assert.Empty(provider.Due(1_000));
        Assert.Null(provider.Pending(1_000));
    }

    [Fact]
    public void AReloadThatChangesNothingSaysNothing()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        _ = provider.Due(500);

        Assert.Empty(provider.Reconfigure(Defaults with { Settle = TimeSpan.FromSeconds(1) }));
        Assert.Empty(provider.Due(1_000));
        Assert.True(provider.IsHeld(DeviceKind.Camera));
    }

    [Fact]
    public void PendingIsNeverNegative()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);

        Assert.Equal(TimeSpan.Zero, provider.Pending(5_000));
    }

    [Fact]
    public void ContextNamesAreQuotedInTheCommand()
    {
        var provider = new Provider(new AynConfig(Camera: "on air", Settle: TimeSpan.FromMilliseconds(1)));

        provider.Observe(Camera("obs64.exe"), 0);

        Assert.Equal("context --set \"on air\" --lease", Assert.Single(provider.Due(1)).Command);
    }
}

/// <summary>Tests for how the consent store is read.</summary>
public sealed class ConsentStoreTests
{
    [Theory]
    [InlineData(@"C:#Program Files#WindowsApps#MicrosoftTeams_24295.605.3225.8804_x64__8wekyb3d8bbwe#ms-teams.exe", "ms-teams.exe")]
    [InlineData(@"C:#Program Files#Mozilla Firefox#firefox.exe", "firefox.exe")]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "Microsoft.WindowsCamera")]
    [InlineData("Microsoft.SkypeApp_kzf8qxf38zg5c", "Microsoft.SkypeApp")]
    [InlineData("plain", "plain")]
    public void TheStoresKeyNameBecomesSomethingAPersonWouldSay(string key, string expected)
    {
        Assert.Equal(expected, ConsentEntry.AppName(key));
    }

    [Fact]
    public void AStartWithNoStopMeansTheDeviceIsOpen()
    {
        Assert.True(new ConsentEntry("Teams.exe", Started: 133_000_000_000_000_000, Stopped: 0).InUse);
        Assert.False(new ConsentEntry("Teams.exe", Started: 133_000_000_000_000_000, Stopped: 133_000_000_000_000_001).InUse);

        // Granted access, never used: neither value, not an open device.
        Assert.False(new ConsentEntry("Teams.exe", Started: 0, Stopped: 0).InUse);
    }

    [Fact]
    public void AReadingNamesEachProgramWithADeviceOpenOnce()
    {
        var store = new FakeStore
        {
            [DeviceKind.Camera] =
            [
                new ConsentEntry("Teams.exe", 1, 0),
                new ConsentEntry("teams.exe", 1, 0),
                new ConsentEntry("firefox.exe", 1, 2),
            ],
            [DeviceKind.Microphone] = [new ConsentEntry("Discord.exe", 5, 0)],
        };

        Reading reading = Reading.From(store);

        Assert.Equal(["Teams.exe"], reading.CameraApps);
        Assert.Equal(["Discord.exe"], reading.MicrophoneApps);
    }

    private sealed class FakeStore : IConsentStore
    {
        private readonly Dictionary<DeviceKind, IReadOnlyList<ConsentEntry>> _entries = [];

        public IReadOnlyList<ConsentEntry> this[DeviceKind device]
        {
            set => _entries[device] = value;
        }

        public IReadOnlyList<ConsentEntry> Read(DeviceKind device) =>
            _entries.TryGetValue(device, out IReadOnlyList<ConsentEntry>? entries) ? entries : [];
    }
}

/// <summary>Tests for the ayn section of the configuration.</summary>
public sealed class AynConfigLoaderTests
{
    [Fact]
    public void NoSectionMeansBothDevicesUnderTheirOwnNames()
    {
        AynConfig config = AynConfigLoader.Load("general { }");

        Assert.Equal("camera", config.Camera);
        Assert.Equal("microphone", config.Microphone);
        Assert.Equal(TimeSpan.FromMilliseconds(500), config.EffectiveSettle);
        Assert.True(config.WatchesAnything);
    }

    [Fact]
    public void TheNamesAndTheSettleTimeAreRead()
    {
        AynConfigLoad load = AynConfigLoader.Validate("""
            ayn {
                camera "on-camera"
                microphone "on-mic"
                settle 1200
            }
            """);

        Assert.Empty(load.Diagnostics);
        Assert.Equal("on-camera", load.Config.Camera);
        Assert.Equal("on-mic", load.Config.Microphone);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), load.Config.EffectiveSettle);
    }

    [Fact]
    public void FalseTurnsADeviceOff()
    {
        AynConfig config = AynConfigLoader.Load("ayn { camera #false }");

        Assert.Null(config.Camera);
        Assert.Equal("microphone", config.Microphone);
        Assert.True(config.WatchesAnything);

        Assert.False(AynConfigLoader.Load("ayn { camera #false; microphone #false }").WatchesAnything);
    }

    [Fact]
    public void AnEmptyNameIsASlipNotASwitch()
    {
        AynConfigLoad load = AynConfigLoader.Validate("ayn { camera \"\" }");

        Shubbak.Config.Diagnostic warning = Assert.Single(load.Diagnostics);
        Assert.Equal("AYN0002", warning.Code);
        Assert.Contains("#false", warning.Hint!, StringComparison.Ordinal);
        Assert.Equal("camera", load.Config.Camera);
    }

    [Fact]
    public void AMistypedSettingIsReportedWithAGuess()
    {
        AynConfigLoad load = AynConfigLoader.Validate("ayn { camara \"c\" }");

        Shubbak.Config.Diagnostic warning = Assert.Single(load.Diagnostics);
        Assert.Equal("AYN0001", warning.Code);
        Assert.Contains("camera", warning.Hint!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("settle \"soon\"", "AYN0003", 500)]
    [InlineData("settle 60000", "AYN0004", 10000)]
    [InlineData("settle -5", "AYN0004", 0)]
    public void ASettleTimeThatIsNotANumberOrIsOutOfRangeIsReportedAndRepaired(string setting, string code, int expected)
    {
        AynConfigLoad load = AynConfigLoader.Validate($"ayn {{ {setting} }}");

        Assert.Equal(code, Assert.Single(load.Diagnostics).Code);
        Assert.Equal(TimeSpan.FromMilliseconds(expected), load.Config.EffectiveSettle);
    }

    [Fact]
    public void PropertiesAndChildrenAreBothAccepted()
    {
        AynConfig config = AynConfigLoader.Load("ayn camera=\"c\" microphone=\"m\" settle=250");

        Assert.Equal("c", config.Camera);
        Assert.Equal("m", config.Microphone);
        Assert.Equal(TimeSpan.FromMilliseconds(250), config.EffectiveSettle);
    }

    [Fact]
    public void AFileThatDoesNotParseYieldsTheDefaultsWithoutRepeatingTheParsersComplaint()
    {
        // The window manager's loader reports the syntax error; this one stays quiet
        // rather than saying it twice.
        AynConfigLoad load = AynConfigLoader.Validate("ayn { camera \"c\" ");

        Assert.Empty(load.Diagnostics);
        Assert.Equal("camera", load.Config.Camera);
    }
}
