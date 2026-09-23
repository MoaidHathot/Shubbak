using Ayn.Core;

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
    public void TheConsentStoreWatchesOnlyTheDevicesAsked()
    {
        using var camera = new RegistryConsentStore([DeviceKind.Camera]);

        Assert.DoesNotContain(DeviceKind.Screen, camera.Devices);
        Assert.DoesNotContain(DeviceKind.Microphone, camera.Devices);

        using var everything = new RegistryConsentStore();

        // The screen's key exists from Windows 10 1903 on, alongside the other two.
        Assert.Contains(DeviceKind.Screen, everything.Devices);
    }

    [Fact]
    public void ADeviceCanBeWatchedAfterTheStoreWasBuilt()
    {
        // A reload that names a device the file did not name at startup used to be
        // told to restart the watcher. The store now opens the device's keys on the
        // spot, and the events it exposes grow with it, so the loop can wait on them.
        using var store = new RegistryConsentStore([DeviceKind.Camera]);

        int before = store.Changed.Count;

        Assert.True(store.Watch(DeviceKind.Screen));
        Assert.Contains(DeviceKind.Screen, store.Devices);
        Assert.True(store.Changed.Count > before, "a watched device adds at least one event");

        // Again is nothing: no second set of keys, no duplicate events.
        int after = store.Changed.Count;
        Assert.True(store.Watch(DeviceKind.Screen));
        Assert.Equal(after, store.Changed.Count);

        // The devices watched at construction keep their places, so an event index the
        // loop already holds still names the same key.
        Assert.Equal(DeviceKind.Camera, store.Devices[0]);
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
