using Ayn.Core;

namespace Ayn.Core.Tests;

/// <summary>Tests for what the watcher decides to tell the window manager.</summary>
public sealed class ProviderTests
{
    private static readonly AynConfig Defaults = new(Settle: TimeSpan.FromMilliseconds(500));

    private static Reading Camera(params string[] apps) => new(apps, []);

    private static Reading Microphone(params string[] apps) => new([], apps);

    private static Reading Muted(bool? muted) => new([], [], muted);

    [Fact]
    public void NothingTrueMeansNothingToSay()
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
        Assert.Equal("camera-in-use", action.Context);
        Assert.True(action.Hold);
        Assert.Equal("context --set \"camera-in-use\" --lease", action.Command);
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
        Assert.Equal("context --auto \"camera-in-use\"", action.Command);
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
    public void TheMuteIsBelievedAtOnceBecauseAPersonPressedTheKey()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Muted(true), 1_000);

        // No settle: the icon is wanted now.
        Assert.Equal(TimeSpan.Zero, provider.Pending(1_000));
        ProviderAction on = Assert.Single(provider.Due(1_000));
        Assert.Equal("context --set \"microphone-muted\" --lease", on.Command);
        Assert.Equal("microphone muted", on.Because);

        provider.Observe(Muted(false), 1_050);

        ProviderAction off = Assert.Single(provider.Due(1_050));
        Assert.Equal("context --auto \"microphone-muted\"", off.Command);
        Assert.Equal("microphone unmuted", off.Because);
    }

    [Fact]
    public void NoMicrophoneMeansNotMutedSoAHeldMuteIsLetGo()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Muted(true), 0);
        _ = provider.Due(0);

        // The device was unplugged: nothing to be muted.
        provider.Observe(Muted(null), 100);

        ProviderAction action = Assert.Single(provider.Due(100));
        Assert.False(action.Hold);
        Assert.Equal("microphone-muted", action.Context);
    }

    [Fact]
    public void TheFactsAreIndependent()
    {
        var provider = new Provider(Defaults);

        provider.Observe(new Reading(["Teams.exe"], ["Teams.exe", "Discord.exe"], MicrophoneMuted: true), 0);

        // The mute is due at once; the two uses wait out the settle.
        Assert.Equal("microphone-muted", Assert.Single(provider.Due(0)).Context);

        IReadOnlyList<ProviderAction> due = provider.Due(500);

        Assert.Equal(2, due.Count);
        Assert.Contains(due, a => a.Command == "context --set \"camera-in-use\" --lease");
        Assert.Contains(due, a => a is { Context: "microphone-in-use", Hold: true, Because: "microphone in use by Teams.exe, Discord.exe" });

        // The microphone stays open and muted after the camera closes: one hand-back,
        // for the camera alone.
        provider.Observe(new Reading([], ["Teams.exe"], MicrophoneMuted: true), 5_000);

        ProviderAction action = Assert.Single(provider.Due(5_500));
        Assert.Equal("camera-in-use", action.Context);
        Assert.False(action.Hold);
    }

    [Fact]
    public void AFactTurnedOffInTheFileIsWatchedForNothing()
    {
        var provider = new Provider(new AynConfig(CameraInUse: null, MicrophoneInUse: "mic", MicrophoneMuted: null, Settle: TimeSpan.FromMilliseconds(100)));

        provider.Observe(new Reading(["Teams.exe"], ["Teams.exe"], MicrophoneMuted: true), 0);

        ProviderAction action = Assert.Single(provider.Due(100));
        Assert.Equal("mic", action.Context);
        Assert.Null(provider.Pending(100));
    }

    [Fact]
    public void ALostConnectionHoldsAgainAtOnceWhatIsStillTrue()
    {
        var provider = new Provider(Defaults);

        provider.Observe(new Reading(["Teams.exe"], [], MicrophoneMuted: true), 0);
        _ = provider.Due(500);

        // The window manager restarted; every lease died with it. The settle time was
        // served the first time round, so both holds are due immediately.
        provider.Forget();

        IReadOnlyList<ProviderAction> again = provider.Due(501);
        Assert.Equal(2, again.Count);
        Assert.All(again, a => Assert.True(a.Hold));
    }

    [Fact]
    public void ALostConnectionHandsNothingBackForAFactThatWentFalse()
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
    public void RenamingAContextUnderARunningWatcherReleasesTheOldNameAndHoldsTheNew()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        _ = provider.Due(500);

        ProviderAction release = Assert.Single(provider.Reconfigure(Defaults with { CameraInUse = "on-camera" }));
        Assert.Equal("context --auto \"camera-in-use\"", release.Command);
        Assert.Contains("on-camera", release.Because, StringComparison.Ordinal);

        ProviderAction hold = Assert.Single(provider.Due(501));
        Assert.Equal("context --set \"on-camera\" --lease", hold.Command);
    }

    [Fact]
    public void TurningAFactOffInTheFileReleasesWhatWasHeldForIt()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Muted(true), 0);
        _ = provider.Due(0);

        ProviderAction release = Assert.Single(provider.Reconfigure(Defaults with { MicrophoneMuted = null }));
        Assert.Equal("context --auto \"microphone-muted\"", release.Command);

        Assert.Empty(provider.Due(1_000));
        Assert.Null(provider.Pending(1_000));
    }

    [Fact]
    public void AReloadThatChangesNothingReleasesNothingAndHoldsAgainWhatItHeld()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        _ = provider.Due(500);

        // Nothing renamed, so nothing is handed back under an old name. The hold is
        // asserted again all the same - see AReloadHoldsAgainWhatIsStillTrueUnderAnUnchangedName
        // - and the picture afterwards is the one from before.
        Assert.Empty(provider.Reconfigure(Defaults with { Settle = TimeSpan.FromSeconds(1) }));

        Assert.True(Assert.Single(provider.Due(1_000)).Hold);
        Assert.True(provider.IsHeld(Fact.CameraInUse));
        Assert.Empty(provider.Due(2_000));
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
        var provider = new Provider(new AynConfig(CameraInUse: "on air", Settle: TimeSpan.FromMilliseconds(1)));

        provider.Observe(Camera("obs64.exe"), 0);

        Assert.Equal("context --set \"on air\" --lease", Assert.Single(provider.Due(1)).Command);
    }

    [Fact]
    public void TheFactsAreNamedSubjectThenState()
    {
        Assert.Equal("camera-in-use", Fact.CameraInUse.Wire());
        Assert.Equal("microphone-in-use", Fact.MicrophoneInUse.Wire());
        Assert.Equal("microphone-muted", Fact.MicrophoneMuted.Wire());

        // The defaults are the names, so a file that says nothing gets them.
        var config = new AynConfig();
        Assert.All(FactNames.All, fact => Assert.Equal(fact.Wire(), config.ContextFor(fact)));
    }

    [Fact]
    public void AnActionSaysWhichFactItIsAbout()
    {
        var provider = new Provider(Defaults);

        provider.Observe(new Reading(["Teams.exe"], [], MicrophoneMuted: true), 0);

        IReadOnlyList<ProviderAction> due = provider.Due(500);

        Assert.Contains(due, a => a is { Fact: Fact.CameraInUse, Context: "camera-in-use" });
        Assert.Contains(due, a => a is { Fact: Fact.MicrophoneMuted, Context: "microphone-muted" });
    }

    // ---- what the host does with the outcome of a send -------------------------

    [Fact]
    public void AnUnreachableWindowManagerIsForgottenAndHeldAgainOnceReachable()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        ProviderAction hold = Assert.Single(provider.Due(500));

        // Sent into the void: the connection is gone and every lease with it.
        provider.Sent(hold, SendOutcome.Unreachable);

        Assert.False(provider.IsHeld(Fact.CameraInUse));
        Assert.Equal("context --set \"camera-in-use\" --lease", Assert.Single(provider.Due(501)).Command);
    }

    [Fact]
    public void ARefusedHoldIsNotAskedAgainOnEveryChange()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        ProviderAction hold = Assert.Single(provider.Due(500));

        // "No context called camera-in-use": the file does not declare it. Asking on
        // every wake would say the same thing a hundred times.
        provider.Sent(hold, SendOutcome.Refused);

        Assert.False(provider.IsHeld(Fact.CameraInUse));
        Assert.Empty(provider.Due(1_000));

        // And nothing is pending for it either - a host that waited for a pending time
        // of zero would wake at once, for ever.
        Assert.Null(provider.Pending(1_000));

        // The camera closing hands nothing back: the window manager holds nothing.
        provider.Observe(Reading.Idle, 2_000);
        Assert.Empty(provider.Due(2_500));
        Assert.Null(provider.Pending(2_500));
    }

    [Fact]
    public void ARefusedHoldIsAskedAgainAfterAReloadUnderTheSameName()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        provider.Sent(Assert.Single(provider.Due(500)), SendOutcome.Refused);

        // The user adds `context "camera-in-use" { }` and saves. The name in the ayn
        // section did not change; the answer will.
        Assert.Empty(provider.Reconfigure(Defaults));

        ProviderAction again = Assert.Single(provider.Due(501));
        Assert.True(again.Hold);
        Assert.Equal("camera-in-use", again.Context);
    }

    [Fact]
    public void ARefusedHoldIsAskedAgainAfterTheConnectionIsRemade()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        provider.Sent(Assert.Single(provider.Due(500)), SendOutcome.Refused);

        // The window manager restarted, perhaps with a different file.
        provider.Forget();

        Assert.True(Assert.Single(provider.Due(501)).Hold);
    }

    [Fact]
    public void ARefusedHandBackNeedsNoBookkeeping()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        provider.Sent(Assert.Single(provider.Due(500)), SendOutcome.Accepted);

        provider.Observe(Reading.Idle, 1_000);
        ProviderAction handBack = Assert.Single(provider.Due(1_500));
        Assert.False(handBack.Hold);

        // Refused on the way down - the context was removed from the file between the
        // two. Already booked as gone; the camera coming back is a fresh hold.
        provider.Sent(handBack, SendOutcome.Refused);

        provider.Observe(Camera("Teams.exe"), 2_000);
        Assert.True(Assert.Single(provider.Due(2_500)).Hold);
    }

    [Fact]
    public void AReloadHoldsAgainWhatIsStillTrueUnderAnUnchangedName()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        provider.Sent(Assert.Single(provider.Due(500)), SendOutcome.Accepted);

        // The window manager drops every pin on a context the reloaded file no longer
        // declares, and says nothing. Asserting a pin it still holds replaces it with
        // itself, so being sure costs one command per held context per reload.
        Assert.Empty(provider.Reconfigure(Defaults));

        ProviderAction again = Assert.Single(provider.Due(501));
        Assert.True(again.Hold);
        Assert.Equal("camera-in-use", again.Context);

        // Once.
        Assert.Empty(provider.Due(1_000));
    }

    [Fact]
    public void AReloadThatLengthensTheSettleWaitsItOutBeforeHoldingAgain()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        provider.Sent(Assert.Single(provider.Due(500)), SendOutcome.Accepted);

        // The new file wants a second of quiet before believing the camera. The
        // re-hold is judged by the new rule, which is what the rule is for; it is
        // bounded by the longest settle the loader allows.
        Assert.Empty(provider.Reconfigure(Defaults with { Settle = TimeSpan.FromSeconds(1) }));

        Assert.Empty(provider.Due(600));
        Assert.Equal(TimeSpan.FromMilliseconds(400), provider.Pending(600));
        Assert.True(Assert.Single(provider.Due(1_000)).Hold);
    }

    [Fact]
    public void AReloadLeavesAPendingHandBackToGoOutOnTime()
    {
        var provider = new Provider(Defaults);

        provider.Observe(Camera("Teams.exe"), 0);
        provider.Sent(Assert.Single(provider.Due(500)), SendOutcome.Accepted);

        // The camera closed 100 ms ago; the hand-back is waiting out its settle when
        // the file is saved. It must still be sent, or the pin would be held until the
        // camera was used again.
        provider.Observe(Reading.Idle, 1_000);
        Assert.Empty(provider.Reconfigure(Defaults));

        Assert.Empty(provider.Due(1_100));
        ProviderAction handBack = Assert.Single(provider.Due(1_500));
        Assert.False(handBack.Hold);
    }

    // ---- how long the host sleeps -----------------------------------------------

    [Fact]
    public void NothingPendingAndNothingToRetryWaitsForTheDeskAlone()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, Provider.NextWait(retrying: false, pending: null, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ASettleThatIsPendingIsWaitedForExactly()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(400), Provider.NextWait(retrying: false, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.Zero, Provider.NextWait(retrying: false, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AnUnreachableWindowManagerWithSomethingDueNowIsRetriedNotSpunOn()
    {
        // The case that spun a core: after a failed send the provider forgets what it
        // held, everything still true is due at once - a pending time of zero - and a
        // wait of zero is no wait at all.
        Assert.Equal(TimeSpan.FromSeconds(1), Provider.NextWait(retrying: true, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(1), Provider.NextWait(retrying: true, pending: null, TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(1), Provider.NextWait(retrying: true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ASettleExpiringBeforeTheRetryIsStillHonouredWhileRetrying()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300), Provider.NextWait(retrying: true, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void TheLoopAsWrittenNeverWaitsZeroWhileTheWindowManagerIsDown()
    {
        // What the host does: observe, hand out, fail to send, forget, ask what is
        // pending, decide how long to sleep. With the microphone muted - a fact that is
        // true for hours - every step of that is repeated on every wake.
        var provider = new Provider(Defaults);
        provider.Observe(Muted(true), 0);

        for (int round = 0; round < 3; round++)
        {
            bool retrying = false;

            foreach (ProviderAction action in provider.Due(round))
            {
                provider.Sent(action, SendOutcome.Unreachable);
                retrying = true;
            }

            TimeSpan wait = Provider.NextWait(retrying, provider.Pending(round), TimeSpan.FromSeconds(1));

            Assert.True(retrying, "the hold should be due again after being forgotten");
            Assert.Equal(TimeSpan.FromSeconds(1), wait);
        }
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

        // Opened after it was last closed is open too, should a build of Windows leave
        // the old stop in place rather than zeroing it.
        Assert.True(new ConsentEntry("Teams.exe", Started: 133_000_000_000_000_002, Stopped: 133_000_000_000_000_001).InUse);
    }

    [Fact]
    public void AReadingNamesEachProgramWithADeviceOpenOnceAndCarriesTheMute()
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

        Reading reading = Reading.From(store, microphoneMuted: true);

        Assert.Equal(["Teams.exe"], reading.CameraApps);
        Assert.Equal(["Discord.exe"], reading.MicrophoneApps);
        Assert.True(reading.Holds(Fact.CameraInUse));
        Assert.True(reading.Holds(Fact.MicrophoneInUse));
        Assert.True(reading.Holds(Fact.MicrophoneMuted));

        Assert.False(Reading.Idle.Holds(Fact.MicrophoneMuted));
        Assert.False(new Reading([], [], MicrophoneMuted: null).Holds(Fact.MicrophoneMuted));
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
    private const string Declared = """
        contexts {
            context "camera-in-use" { }
            context "microphone-in-use" { }
            context "microphone-muted" { }
            context "on-camera" { }
        }
        """;

    [Fact]
    public void NoSectionMeansEveryFactUnderItsOwnName()
    {
        AynConfig config = AynConfigLoader.Load("general { }");

        Assert.Equal("camera-in-use", config.CameraInUse);
        Assert.Equal("microphone-in-use", config.MicrophoneInUse);
        Assert.Equal("microphone-muted", config.MicrophoneMuted);
        Assert.Equal(TimeSpan.FromMilliseconds(500), config.EffectiveSettle);
        Assert.True(config.WatchesAnything);
        Assert.True(config.NeedsConsentStore);
        Assert.True(config.NeedsAudioEndpoint);
    }

    [Fact]
    public void TheSectionIsNestedBySubject()
    {
        AynConfigLoad load = AynConfigLoader.Validate(Declared + """
            ayn {
                camera     { in-use "on-camera" }
                microphone { in-use "microphone-in-use"; muted #false }
                settle 1200
            }
            """);

        Assert.Empty(load.Diagnostics);
        Assert.Equal("on-camera", load.Config.CameraInUse);
        Assert.Equal("microphone-in-use", load.Config.MicrophoneInUse);
        Assert.Null(load.Config.MicrophoneMuted);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), load.Config.EffectiveSettle);
        Assert.False(load.Config.NeedsAudioEndpoint);
    }

    [Fact]
    public void FalseTurnsAWholeDeviceOff()
    {
        AynConfig config = AynConfigLoader.Load("ayn { camera #false }");

        Assert.Null(config.CameraInUse);
        Assert.Equal("microphone-in-use", config.MicrophoneInUse);
        Assert.Equal("microphone-muted", config.MicrophoneMuted);

        AynConfig nothing = AynConfigLoader.Load("ayn { camera #false; microphone #false }");
        Assert.False(nothing.WatchesAnything);
        Assert.False(nothing.NeedsConsentStore);
        Assert.False(nothing.NeedsAudioEndpoint);

        // Only the mute: no need for the consent store at all.
        AynConfig muteOnly = AynConfigLoader.Load("ayn { camera #false; microphone { in-use #false } }");
        Assert.False(muteOnly.NeedsConsentStore);
        Assert.True(muteOnly.NeedsAudioEndpoint);
    }

    [Fact]
    public void AnEmptyNameIsASlipNotASwitch()
    {
        AynConfigLoad load = AynConfigLoader.Validate("ayn { camera { in-use \"\" } }");

        Shubbak.Config.Diagnostic warning = Assert.Single(load.Diagnostics);
        Assert.Equal("AYN0002", warning.Code);
        Assert.Contains("#false", warning.Hint!, StringComparison.Ordinal);
        Assert.Equal("camera-in-use", load.Config.CameraInUse);
    }

    [Fact]
    public void AMistypedSettingIsReportedWithAGuessAtEitherLevel()
    {
        AynConfigLoad load = AynConfigLoader.Validate("ayn { camara { in-use \"c\" }; microphone { mutd \"m\" } }");

        Shubbak.Config.Diagnostic[] warnings = [.. load.Diagnostics.Where(d => d.Code == "AYN0001")];
        Assert.Equal(2, warnings.Length);
        Assert.Contains("camera", warnings[0].Hint!, StringComparison.Ordinal);
        Assert.Contains("muted", warnings[1].Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ACameraHasNoMute()
    {
        AynConfigLoad load = AynConfigLoader.Validate("ayn { camera { muted \"x\" } }");

        Assert.Contains(load.Diagnostics, d => d.Code == "AYN0001" && d.Message.Contains("'camera'", StringComparison.Ordinal));
    }

    [Fact]
    public void ANameTheFileDoesNotDeclareIsPointedOutButOnlyWhenWritten()
    {
        // Written and undeclared: said, with a guess.
        AynConfigLoad written = AynConfigLoader.Validate(Declared + "ayn { camera { in-use \"on-camra\" } }");
        Shubbak.Config.Diagnostic warning = Assert.Single(written.Diagnostics);
        Assert.Equal("AYN0005", warning.Code);
        Assert.Contains("on-camera", warning.Hint!, StringComparison.Ordinal);

        // A default in a file with no contexts at all: nothing said, or a file that
        // never mentioned the watcher would be told about three contexts.
        Assert.Empty(AynConfigLoader.Validate("ayn { settle 300 }").Diagnostics);
        Assert.Empty(AynConfigLoader.Validate("general { }").Diagnostics);

        // Written and declared: nothing said.
        Assert.Empty(AynConfigLoader.Validate(Declared + "ayn { camera { in-use \"on-camera\" } }").Diagnostics);
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
        AynConfig config = AynConfigLoader.Load(Declared + "ayn settle=250 { camera in-use=\"on-camera\"; microphone muted=#false }");

        Assert.Equal("on-camera", config.CameraInUse);
        Assert.Null(config.MicrophoneMuted);
        Assert.Equal(TimeSpan.FromMilliseconds(250), config.EffectiveSettle);
    }

    [Fact]
    public void AFileThatDoesNotParseYieldsTheDefaultsWithoutRepeatingTheParsersComplaint()
    {
        // The window manager's loader reports the syntax error; this one stays quiet
        // rather than saying it twice - but says that it did, so a host starting up on
        // the broken file can say which file it is ignoring.
        AynConfigLoad load = AynConfigLoader.Validate("ayn { camera { in-use \"c\" ");

        Assert.Empty(load.Diagnostics);
        Assert.True(load.SyntaxErrors);
        Assert.Equal("camera-in-use", load.Config.CameraInUse);

        Assert.False(AynConfigLoader.Validate("ayn { }").SyntaxErrors);
    }
}

/// <summary>Tests for what a signal to the watcher asks.</summary>
public sealed class SignalRequestTests
{
    [Theory]
    [InlineData(new[] { "microphone", "mute" }, "mute")]
    [InlineData(new[] { "microphone", "unmute" }, "unmute")]
    [InlineData(new[] { "microphone", "toggle-mute" }, "toggle-mute")]
    [InlineData(new[] { "Microphone", "Toggle" }, "toggle-mute")]
    [InlineData(new[] { "mic", "mute" }, "mute")]
    public void SubjectThenVerbReadsAsASentence(string[] arguments, string verb)
    {
        SignalRequest? request = SignalRequest.Parse(arguments, out string? refusal);

        Assert.Null(refusal);
        Assert.Equal("microphone", request!.Subject);
        Assert.Equal(verb, request.Verb);
    }

    [Theory]
    [InlineData(new string[0], "nothing to do")]
    [InlineData(new[] { "camera", "mute" }, "not something ayn acts on")]
    [InlineData(new[] { "microphone" }, "does not say what to do")]
    [InlineData(new[] { "microphone", "louder" }, "does not say what to do")]
    public void WhatItCannotDoIsRefusedWithTheList(string[] arguments, string reason)
    {
        Assert.Null(SignalRequest.Parse(arguments, out string? refusal));
        Assert.Contains(reason, refusal!, StringComparison.Ordinal);
        Assert.Contains("mute", refusal, StringComparison.Ordinal);
    }
}
