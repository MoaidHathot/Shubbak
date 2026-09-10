namespace Ayn.Core;

/// <summary>A device Windows keeps a consent record for.</summary>
public enum DeviceKind
{
    Camera,
    Microphone,
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
    /// <summary>Whether this program has the device open right now.</summary>
    public bool InUse => Started != 0 && Stopped == 0;

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

/// <summary>What the desk said about both devices at one moment.</summary>
/// <param name="CameraApps">Programs with the camera open.</param>
/// <param name="MicrophoneApps">Programs with the microphone open.</param>
/// <param name="MicrophoneMuted">
/// Whether the default microphone is muted at the system level, or null when there is
/// no microphone to ask - which counts as not muted, so a context held for it is let
/// go rather than left hanging on a device that was unplugged.
/// </param>
public sealed record Reading(
    IReadOnlyList<string> CameraApps,
    IReadOnlyList<string> MicrophoneApps,
    bool? MicrophoneMuted = null)
{
    /// <summary>Nothing open anywhere, nothing muted.</summary>
    public static Reading Idle { get; } = new([], []);

    /// <summary>Takes a reading from a store. The mute state comes from elsewhere; see the host.</summary>
    public static Reading From(IConsentStore store, bool? microphoneMuted = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        return new Reading(InUse(store.Read(DeviceKind.Camera)), InUse(store.Read(DeviceKind.Microphone)), microphoneMuted);
    }

    /// <summary>Whether a fact holds in this reading.</summary>
    public bool Holds(Fact fact) => fact switch
    {
        Fact.CameraInUse => CameraApps.Count > 0,
        Fact.MicrophoneInUse => MicrophoneApps.Count > 0,
        Fact.MicrophoneMuted => MicrophoneMuted == true,
        _ => false,
    };

    /// <summary>The programs using a device, or empty when it is not watched.</summary>
    public IReadOnlyList<string> AppsFor(DeviceKind device) => device switch
    {
        DeviceKind.Camera => CameraApps,
        DeviceKind.Microphone => MicrophoneApps,
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
