using Ayn.Core;
using Microsoft.Win32;

namespace Ayn.Tests;

/// <summary>
/// The watchers that read the machine rather than the registry's consent store: the
/// power notifications and the theme key. Both are opened for real, since the point
/// of each is that Windows answers; the assertions are about the shape of the answer
/// rather than its value, which depends on the desk the tests run on.
/// </summary>
public sealed class MachineWatchTests
{
    [Fact]
    public void ThePowerWatchOpensAndAnswersWithAReading()
    {
        using var power = new PowerWatch();

        // A desktop has no lid and no battery and says so; the mains and the
        // presence register anywhere. Either way something registers on a machine
        // that has a power manager at all.
        Assert.True(power.Open());
        Assert.True(power.Open(), "opening twice is nothing");

        PowerReading reading = power.Read();

        Assert.True(reading.BatteryPercent is null or (>= 0 and <= 100));
    }

    [Fact]
    public void TheThemeWatchOpensAndReadsTheKey()
    {
        using var theme = new ThemeWatch();

        Assert.True(theme.Open());

        // Dark or light; null only when the value was never written, which Settings
        // does on first use, so a machine with a user on it has one.
        bool? dark = theme.IsDark();

        Assert.True(dark is not null, "AppsUseLightTheme is not set on this machine");

        // Arming twice is what every wake does.
        theme.Arm();
        theme.Arm();
    }

    [Fact]
    public void TheAudioEndpointNamesItsDevices()
    {
        // The name a `device` rule matches against, read from the device's property
        // store through the hand-rolled IMMDevice and IPropertyStore vtables. A wrong
        // slot here would not fail neatly: it would call some other method with our
        // arguments. So the read is made on the real device, and its answer has to
        // look like a name Windows shows - "Speakers (Realtek(R) Audio)" - not empty,
        // not a GUID.
        using var endpoint = new AudioEndpoint { WatchSpeaker = true };

        Assert.True(endpoint.Open(), "Core Audio is not available on this machine");

        if (endpoint.HasDevice)
        {
            Assert.False(string.IsNullOrWhiteSpace(endpoint.MicrophoneName), "a microphone with no name");
            Assert.DoesNotContain("{", endpoint.MicrophoneName, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(endpoint.MicrophoneName);
        }

        if (endpoint.HasSpeaker)
        {
            Assert.False(string.IsNullOrWhiteSpace(endpoint.SpeakerName), "a speaker with no name");
            Assert.DoesNotContain("{", endpoint.SpeakerName, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(endpoint.SpeakerName);
        }

        // Resolving again, as a default-device change does, reads the same names and
        // leaks nothing that a second read would trip over.
        string? microphone = endpoint.MicrophoneName;
        string? speaker = endpoint.SpeakerName;

        endpoint.Resolve();

        Assert.Equal(microphone, endpoint.MicrophoneName);
        Assert.Equal(speaker, endpoint.SpeakerName);
    }

    // ---- the consent store ----------------------------------------------------
    //
    // Against a store of the test's own under HKCU\Software\Shubbak\Tests, with the
    // leaves the test creates, rather than against Windows's: Windows's has whichever
    // leaves this machine has been asked for, and a build agent that has never shared
    // its screen has no graphicsCaptureProgrammatic key. The first version of these
    // tests asserted that it did, and was red on CI for four runs.

    [Fact]
    public void TheConsentStoreWatchesOnlyTheDevicesAsked()
    {
        using var scratch = new ScratchStore(DeviceKind.Camera, DeviceKind.Microphone, DeviceKind.Screen);

        using var camera = new RegistryConsentStore([DeviceKind.Camera], scratch.Path);

        Assert.Equal([DeviceKind.Camera], camera.Devices);
        Assert.Equal(1, camera.Count);

        using var everything = new RegistryConsentStore(DeviceKinds.All, scratch.Path);

        Assert.Equal(DeviceKinds.All, everything.Devices);

        // One key per leaf: the scratch store is under the user's hive only, and the
        // machine's hive not having it is the ordinary case, not a failure.
        Assert.Equal(3, everything.Count);
    }

    [Fact]
    public void ADeviceCanBeWatchedAfterTheStoreWasBuilt()
    {
        // A reload that names a device the file did not name at startup used to be
        // told to restart the watcher. The store now opens the device's keys on the
        // spot, and the events it exposes grow with it, so the loop can wait on them.
        using var scratch = new ScratchStore(DeviceKind.Camera, DeviceKind.Screen);
        using var store = new RegistryConsentStore([DeviceKind.Camera], scratch.Path);

        Assert.Single(store.Changed);

        Assert.True(store.Watch(DeviceKind.Screen));
        Assert.Equal([DeviceKind.Camera, DeviceKind.Screen], store.Devices);
        Assert.Equal(2, store.Changed.Count);

        // Again is nothing: no second set of keys, no duplicate events.
        Assert.True(store.Watch(DeviceKind.Screen));
        Assert.Equal(2, store.Changed.Count);

        // The devices watched at construction keep their places, so an event index the
        // loop already holds still names the same key.
        Assert.Equal(DeviceKind.Camera, store.Devices[0]);

        // And the new watch is live: a program starting to share the screen wakes the
        // new event - the second, not the camera's - and the read says who it was.
        WaitHandle screenChanged = store.Changed[1];
        WaitHandle cameraChanged = store.Changed[0];

        scratch.StartUsing(DeviceKind.Screen, @"C:#Program Files#Teams#ms-teams.exe");

        Assert.True(screenChanged.WaitOne(TimeSpan.FromSeconds(5)), "the new watch did not fire");
        Assert.False(cameraChanged.WaitOne(0), "the camera's watch fired for a change under the screen");

        ConsentEntry entry = Assert.Single(store.Read(DeviceKind.Screen));

        Assert.Equal("ms-teams.exe", entry.App);
        Assert.True(entry.InUse);
    }

    [Fact]
    public void ADeviceWhoseKeyIsNotThereIsNotWatchedAndSaysSo()
    {
        // The build agent's case, made deliberate: asked to watch a device the store
        // has no leaf for, the store declines rather than pretending. The loop then
        // keeps the handles it had, and the file's screen { } does nothing until the
        // key appears and the watcher is restarted - which is logged, once, as a
        // warning against the user's hive.
        using var scratch = new ScratchStore(DeviceKind.Camera);
        using var store = new RegistryConsentStore([DeviceKind.Camera, DeviceKind.Screen], scratch.Path);

        Assert.Equal([DeviceKind.Camera], store.Devices);
        Assert.Single(store.Changed);

        Assert.False(store.Watch(DeviceKind.Screen));
        Assert.Equal([DeviceKind.Camera], store.Devices);
        Assert.Single(store.Changed);
        Assert.Empty(store.Read(DeviceKind.Screen));
    }

    [Fact]
    public void WindowsOwnStoreIsWatchedForWhateverLeavesThisMachineHas()
    {
        // The one look at the real store, and machine-honest: the store's answer for
        // each device is checked against the registry's own - is the leaf there, under
        // either hive - rather than against what a developer's machine happened to
        // have. Where the leaf is missing the store must decline, as above.
        using var store = new RegistryConsentStore();

        foreach (DeviceKind device in DeviceKinds.All)
        {
            string path = RegistryConsentStore.WindowsStorePath + RegistryConsentStore.LeafFor(device);

            bool exists = Exists(Registry.CurrentUser, path) || Exists(Registry.LocalMachine, path);

            Assert.Equal(exists, store.Devices.Contains(device));
        }

        static bool Exists(RegistryKey hive, string path)
        {
            using RegistryKey? key = hive.OpenSubKey(path);
            return key is not null;
        }
    }

    /// <summary>
    /// A consent store of the test's own, under the user's hive, with the leaves asked
    /// for and nothing under them; deleted whole when the test is done.
    /// </summary>
    private sealed class ScratchStore : IDisposable
    {
        private readonly string _root;

        public ScratchStore(params DeviceKind[] devices)
        {
            _root = @"SOFTWARE\Shubbak\Tests\ConsentStore-" + Guid.NewGuid().ToString("N");
            Path = _root + @"\";

            foreach (DeviceKind device in devices)
            {
                using RegistryKey leaf = Registry.CurrentUser.CreateSubKey(Path + RegistryConsentStore.LeafFor(device));
            }
        }

        /// <summary>The store's path relative to the hive, with the trailing backslash the store expects.</summary>
        public string Path { get; }

        /// <summary>
        /// Records a program opening a device the way Windows does: a key named after
        /// the program under <c>NonPackaged</c>, a start time, and no stop.
        /// </summary>
        public void StartUsing(DeviceKind device, string programKey)
        {
            using RegistryKey record = Registry.CurrentUser.CreateSubKey(
                Path + RegistryConsentStore.LeafFor(device) + @"\NonPackaged\" + programKey);

            record.SetValue("LastUsedTimeStart", DateTime.UtcNow.ToFileTimeUtc(), RegistryValueKind.QWord);
            record.SetValue("LastUsedTimeStop", 0L, RegistryValueKind.QWord);
        }

        public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
    }

    [Fact]
    public void TheDescriptionNamesTheRules()
    {
        string described = Program.Describe(new AynConfig(
            CameraInUse: null, MicrophoneInUse: null, MicrophoneMuted: null,
            DarkTheme: "dark-theme",
            AppRules: [new AppRule(DeviceKind.Camera, "ms-teams.exe", "in-a-call")]));

        Assert.Equal("dark-theme, camera by ms-teams.exe as \"in-a-call\"", described);
    }
}
