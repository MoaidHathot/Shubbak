namespace Ayn.Core;

/// <summary>A device Windows keeps a consent record for.</summary>
public enum DeviceKind
{
    Camera,
    Microphone,

    /// <summary>
    /// The screen, as recorded under <c>graphicsCaptureProgrammatic</c>: a program that
    /// is capturing a display or a window through the graphics-capture pipeline, which
    /// is what screen sharing and screen recording use.
    /// </summary>
    Screen,
}

/// <summary>The facts a device's use gives rise to, and its name in the file.</summary>
public static class DeviceKinds
{
    /// <summary>The block in the file: <c>camera</c>, <c>microphone</c>, <c>screen</c>.</summary>
    public static string Word(this DeviceKind device) => device switch
    {
        DeviceKind.Camera => "camera",
        DeviceKind.Microphone => "microphone",
        DeviceKind.Screen => "screen",
        _ => device.ToString().ToLowerInvariant(),
    };

    /// <summary>The fact that is true while any program has the device.</summary>
    public static Fact InUseFact(this DeviceKind device) => device switch
    {
        DeviceKind.Camera => Fact.CameraInUse,
        DeviceKind.Microphone => Fact.MicrophoneInUse,
        DeviceKind.Screen => Fact.ScreenCaptured,
        _ => throw new ArgumentOutOfRangeException(nameof(device), device, "Not a device with a consent record."),
    };

    /// <summary>Every device, in a stable order.</summary>
    public static IReadOnlyList<DeviceKind> All { get; } = [DeviceKind.Camera, DeviceKind.Microphone, DeviceKind.Screen];
}

/// <summary>
/// One program's record in the consent store for one device.
/// </summary>
/// <param name="App">Who, as a person would say it: <c>Teams.exe</c>, <c>Microsoft.WindowsCamera</c>.</param>
/// <param name="Started">When it last opened the device, as a FILETIME; zero if never.</param>
/// <param name="Stopped">When it last closed the device, as a FILETIME; zero while it is open.</param>
/// <remarks>
/// Windows writes these two values for every program that opens a camera or a
/// microphone through the capture pipeline, packaged or not, in
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{webcam,microphone}</c>,
/// so that the settings page can say "currently in use" and "last accessed". A
/// program that has the device open has a start and no stop. That pair is the whole
/// signal, and it is the same one the shell's own privacy indicator reads.
/// </remarks>
public sealed record ConsentEntry(string App, long Started, long Stopped)
{
    /// <summary>
    /// Whether this program has the device open right now.
    /// </summary>
    /// <remarks>
    /// A start with no stop is the observed shape: Windows zeroes the stop when the
    /// device is opened. A start after the stop is read the same way, in case a build
    /// of Windows leaves the old stop in place and writes only the new start - a
    /// device opened after it was last closed is open, whichever way the store says it.
    /// </remarks>
    public bool InUse => Started != 0 && (Stopped == 0 || Started > Stopped);

    /// <summary>
    /// The program's name as a person would say it, from the store's key name.
    /// </summary>
    /// <remarks>
    /// Packaged programs are keyed by package family name, <c>Microsoft.WindowsCamera_8wekyb3d8bbwe</c>;
    /// the publisher hash after the underscore says nothing to anyone. Everything
    /// else sits under <c>NonPackaged</c> keyed by its path with every backslash
    /// turned into a hash sign, <c>C:#Program Files#Teams#Teams.exe</c>, and the last
    /// segment is the executable. Names in the log and in the window manager's
    /// report read better as the executable than as the escaped path.
    /// </remarks>
    public static string AppName(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        int hash = key.LastIndexOf('#');
        if (hash >= 0) return key[(hash + 1)..];

        int underscore = key.LastIndexOf('_');
        return underscore > 0 ? key[..underscore] : key;
    }
}

/// <summary>
/// Reads the consent store for a device. The host reads the registry; a test hands
/// back whatever it likes.
/// </summary>
public interface IConsentStore
{
    IReadOnlyList<ConsentEntry> Read(DeviceKind device);
}

/// <summary>
/// What the machine's power says at one moment.
/// </summary>
/// <param name="OnBattery">Running from the battery rather than the mains.</param>
/// <param name="BatteryPercent">How much is left, or null when there is no battery.</param>
/// <param name="LidClosed">The lid of a laptop is shut.</param>
/// <param name="UserAway">Windows judges nobody to be at the keyboard.</param>
public sealed record PowerReading(
    bool OnBattery = false,
    int? BatteryPercent = null,
    bool LidClosed = false,
    bool UserAway = false)
{
    /// <summary>On the mains, lid open, somebody there.</summary>
    public static PowerReading Mains { get; } = new();
}

/// <summary>What the desk said at one moment.</summary>
/// <param name="CameraApps">Programs with the camera open.</param>
/// <param name="MicrophoneApps">Programs with the microphone open.</param>
/// <param name="MicrophoneMuted">
/// Whether the default microphone is muted at the system level, or null when there is
/// no microphone to ask - which counts as not muted, so a context held for it is let
/// go rather than left hanging on a device that was unplugged.
/// </param>
/// <param name="ScreenApps">Programs capturing the screen.</param>
/// <param name="SpeakerMuted">Whether the default speaker is muted, or null when there is none.</param>
/// <param name="Power">The machine's power, or null when it was not asked.</param>
/// <param name="DarkTheme">Whether apps are set to the dark theme, or null when it was not asked.</param>
public sealed record Reading(
    IReadOnlyList<string> CameraApps,
    IReadOnlyList<string> MicrophoneApps,
    bool? MicrophoneMuted = null,
    IReadOnlyList<string>? ScreenApps = null,
    bool? SpeakerMuted = null,
    PowerReading? Power = null,
    bool? DarkTheme = null)
{
    /// <summary>Nothing open anywhere, nothing muted.</summary>
    public static Reading Idle { get; } = new([], []);

    /// <summary>Programs capturing the screen; empty when not asked.</summary>
    public IReadOnlyList<string> ScreenApps { get; init; } = ScreenApps ?? [];

    /// <summary>Takes a reading from a store. The mute state comes from elsewhere; see the host.</summary>
    public static Reading From(IConsentStore store, bool? microphoneMuted = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        return new Reading(
            InUse(store.Read(DeviceKind.Camera)),
            InUse(store.Read(DeviceKind.Microphone)),
            microphoneMuted,
            InUse(store.Read(DeviceKind.Screen)));
    }

    /// <summary>Whether a fact holds in this reading.</summary>
    public bool Holds(Fact fact) => fact switch
    {
        Fact.CameraInUse => CameraApps.Count > 0,
        Fact.MicrophoneInUse => MicrophoneApps.Count > 0,
        Fact.MicrophoneMuted => MicrophoneMuted == true,
        Fact.ScreenCaptured => ScreenApps.Count > 0,
        Fact.SpeakerMuted => SpeakerMuted == true,
        Fact.OnBattery => Power?.OnBattery == true,
        Fact.LidClosed => Power?.LidClosed == true,
        Fact.UserAway => Power?.UserAway == true,
        Fact.DarkTheme => DarkTheme == true,

        // Low needs a threshold, which is the config's; see Provider.
        Fact.BatteryLow => false,
        _ => false,
    };

    /// <summary>The programs using a device, or empty when it is not watched.</summary>
    public IReadOnlyList<string> AppsFor(DeviceKind device) => device switch
    {
        DeviceKind.Camera => CameraApps,
        DeviceKind.Microphone => MicrophoneApps,
        DeviceKind.Screen => ScreenApps,
        _ => [],
    };

    private static List<string> InUse(IReadOnlyList<ConsentEntry> entries)
    {
        List<string> apps = [];

        foreach (ConsentEntry entry in entries)
            if (entry.InUse && !apps.Contains(entry.App, StringComparer.OrdinalIgnoreCase))
                apps.Add(entry.App);

        return apps;
    }
}
