using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Ayn.Core;
using Shubbak.Core.Diagnostics;
using System.Security.AccessControl;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;

namespace Ayn;

/// <summary>
/// The consent store as Windows keeps it, and a way to be woken when it changes.
/// </summary>
/// <remarks>
/// <para>
/// Four keys: the camera and the microphone, each under the user's hive and the
/// machine's. Programs that run as the user write to the first; a few services and
/// older installers land in the second. Whichever exist are watched; one that cannot
/// be opened is skipped and said so once, since a missing hive is not a failure to
/// watch the other three.
/// </para>
/// <para>
/// Woken, not polled. <c>RegNotifyChangeKeyValue</c> signals an event when anything
/// under a key changes and then forgets, so every wake re-arms it before the store
/// is read - in that order, or a change landing between the read and the re-arm
/// would be missed. Between wakes this process holds no timer and burns nothing,
/// which is what a resident watcher owes the machine it sits on.
/// </para>
/// </remarks>
internal sealed class RegistryConsentStore : IConsentStore, IDisposable
{
    private const string StorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    private readonly Watched[] _watched;

    /// <summary>The leaf under the store for each device.</summary>
    /// <remarks>
    /// <c>graphicsCaptureProgrammatic</c> is the capability behind Windows.Graphics.Capture,
    /// which is what screen sharing and recording go through; the shell's own
    /// "sharing your screen" indicator reads the same key.
    /// </remarks>
    private static readonly (DeviceKind Device, string Leaf)[] Leaves =
    [
        (DeviceKind.Camera, "webcam"),
        (DeviceKind.Microphone, "microphone"),
        (DeviceKind.Screen, "graphicsCaptureProgrammatic"),
    ];

    /// <summary>Watches every device.</summary>
    public RegistryConsentStore() : this(DeviceKinds.All) { }

    /// <summary>
    /// Watches the devices named, so a file that reports nothing about the screen does
    /// not hold its key open or wake for every share somebody else starts.
    /// </summary>
    public RegistryConsentStore(IReadOnlyList<DeviceKind> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        List<Watched> watched = [];

        foreach ((RegistryKey hive, string hiveName) in new[] { (Registry.CurrentUser, "HKCU"), (Registry.LocalMachine, "HKLM") })
        {
            foreach ((DeviceKind device, string leaf) in Leaves)
            {
                if (!devices.Contains(device)) continue;

                RegistryKey? key = Open(hive, StorePath + leaf);

                if (key is null)
                {
                    // The user's hive is where every program that runs as the user
                    // writes, so its absence means the watcher will see none of them and
                    // deserves a warning; the machine's hive carries a few services and
                    // is often not there at all.
                    if (ReferenceEquals(hive, Registry.CurrentUser))
                        Log.Warn(LogCategory.Wm, $"{hiveName}\\...\\{leaf} is not there or cannot be watched; programs running as this user will not be noticed using the {device.Word()}");
                    else
                        Log.Debug(LogCategory.Wm, $"{hiveName}\\...\\{leaf} is not there or cannot be watched; skipping it");

                    continue;
                }

                watched.Add(new Watched(device, key, $"{hiveName}\\...\\{leaf}"));
            }
        }

        _watched = [.. watched];
    }

    /// <summary>The devices with at least one key under watch.</summary>
    public IReadOnlyList<DeviceKind> Devices => [.. _watched.Select(w => w.Device).Distinct()];

    /// <summary>One event per watched key, signalled when anything under it changes.</summary>
    public IReadOnlyList<WaitHandle> Changed => [.. _watched.Select(w => w.Event)];

    /// <summary>How many keys are actually being watched.</summary>
    public int Count => _watched.Length;

    /// <summary>Arms every watch. Called once before the first read and after every wake.</summary>
    public void Arm()
    {
        foreach (Watched watched in _watched) Arm(watched);
    }

    /// <summary>Re-arms the watch whose event fired.</summary>
    public void Arm(int index)
    {
        if (index >= 0 && index < _watched.Length) Arm(_watched[index]);
    }

    public IReadOnlyList<ConsentEntry> Read(DeviceKind device)
    {
        List<ConsentEntry> entries = [];

        foreach (Watched watched in _watched)
        {
            if (watched.Device != device) continue;

            try
            {
                ReadInto(watched.Key, entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A key that vanished or closed between the wake and the read. The next
                // change wakes us again; nothing to do now but say so quietly.
                Log.Debug(LogCategory.Wm, $"could not read {watched.Name}: {ex.Message}");
            }
        }

        return entries;
    }

    public void Dispose()
    {
        foreach (Watched watched in _watched)
        {
            watched.Key.Dispose();
            watched.Event.Dispose();
        }
    }

    private static void Arm(Watched watched)
    {
        // The whole subtree, because the programs sit two levels down under
        // NonPackaged, and both the value writes and the key creations, because a
        // program using the device for the first time creates its key and writes the
        // times in the same breath.
        WIN32_ERROR result = PInvoke.RegNotifyChangeKeyValue(
            watched.Key.Handle,
            true,
            REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_LAST_SET,
            watched.Event.SafeWaitHandle,
            true);

        if (result != WIN32_ERROR.ERROR_SUCCESS && !watched.Complained)
        {
            watched.Complained = true;
            Log.Warn(LogCategory.Wm, $"could not watch {watched.Name} (error {(uint)result}); changes there will be missed");
        }
    }

    /// <summary>
    /// Every program's record under one device key.
    /// </summary>
    /// <remarks>
    /// Packaged programs are keys directly under the device; everything else is a key
    /// under <c>NonPackaged</c>. Both carry the same two values. A key with neither -
    /// a program that was granted access and never used it - is not an entry.
    /// </remarks>
    private static void ReadInto(RegistryKey device, List<ConsentEntry> entries)
    {
        foreach (string name in device.GetSubKeyNames())
        {
            using RegistryKey? child = device.OpenSubKey(name);
            if (child is null) continue;

            if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string program in child.GetSubKeyNames())
                {
                    using RegistryKey? record = child.OpenSubKey(program);
                    if (record is not null) Add(program, record, entries);
                }

                continue;
            }

            Add(name, child, entries);
        }
    }

    private static void Add(string keyName, RegistryKey record, List<ConsentEntry> entries)
    {
        long started = Qword(record, "LastUsedTimeStart");
        long stopped = Qword(record, "LastUsedTimeStop");

        if (started == 0 && stopped == 0) return;

        entries.Add(new ConsentEntry(ConsentEntry.AppName(keyName), started, stopped));
    }

    private static long Qword(RegistryKey key, string name) => key.GetValue(name) switch
    {
        long value => value,
        int value => value,
        _ => 0,
    };

    private static RegistryKey? Open(RegistryKey hive, string path)
    {
        try
        {
            return hive.OpenSubKey(
                path,
                RegistryKeyPermissionCheck.ReadSubTree,
                RegistryRights.ReadKey | RegistryRights.Notify);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private sealed class Watched(DeviceKind device, RegistryKey key, string name)
    {
        public DeviceKind Device { get; } = device;
        public RegistryKey Key { get; } = key;
        public string Name { get; } = name;
        public AutoResetEvent Event { get; } = new(false);
        public bool Complained { get; set; }
    }
}
