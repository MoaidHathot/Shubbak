namespace Rasid.Core;

/// <summary>
/// One thing to tell the window manager.
/// </summary>
/// <param name="Context">The context concerned.</param>
/// <param name="Hold">True to hold it on with a lease; false to hand it back to its conditions.</param>
/// <param name="Because">Why, for the log: <c>camera in use by Teams.exe</c>.</param>
public sealed record ProviderAction(string Context, bool Hold, string Because)
{
    /// <summary>
    /// The command, spelled the way the window manager's parser reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--lease</c> on the way up and nothing on the way down. The lease is what
    /// makes a crashed watcher harmless: the pin dies with the connection that made
    /// it, so the window manager is never left believing a meeting is on. Handing back
    /// with <c>--auto</c> rather than <c>--clear</c> leaves no pin at all, so the
    /// report says <c>nothing has set it</c> rather than <c>cleared by rasid.exe</c>
    /// - and a context somebody else also sets is theirs again rather than held off by
    /// us.
    /// </para>
    /// <para>
    /// Quoted, because a context may be called anything the file can spell.
    /// </para>
    /// </remarks>
    public string Command => Hold
        ? $"context --set \"{Context}\" --lease"
        : $"context --auto \"{Context}\"";
}

/// <summary>
/// Decides what to tell the window manager from what the consent store says.
/// </summary>
/// <remarks>
/// <para>
/// Pure: readings and a clock in, commands out. The host feeds it every time the
/// registry changes and asks what is due; the debounce, the recovery after a lost
/// connection and a rename of the context under a running watcher are all here, where
/// a test can hold them to account, and the registry and the pipe are not.
/// </para>
/// <para>
/// Two states per device: what the store says (<em>wanted</em>) and what the window
/// manager has been told (<em>asserted</em>). A change to the first is believed only
/// after it has held for the settle time, because a call opens and closes the camera
/// several times while it sets up, and a context that flapped with it would run its
/// on-enter and on-exit twice - the same reason the window manager's own contexts
/// linger. A change that reverses itself inside the settle time is never reported.
/// </para>
/// </remarks>
public sealed class Provider
{
    private readonly Device[] _devices;
    private RasidConfig _config;

    public Provider(RasidConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _devices = [new Device(DeviceKind.Camera), new Device(DeviceKind.Microphone)];
    }

    /// <summary>The settings in force.</summary>
    public RasidConfig Config => _config;

    /// <summary>
    /// A fresh reading of the store.
    /// </summary>
    /// <param name="reading">What the store says now.</param>
    /// <param name="now">The clock, in milliseconds; only differences matter.</param>
    public void Observe(Reading reading, long now)
    {
        ArgumentNullException.ThrowIfNull(reading);

        foreach (Device device in _devices)
        {
            IReadOnlyList<string> apps = reading.AppsFor(device.Kind);
            bool wanted = apps.Count > 0;

            device.Apps = apps;

            if (wanted == device.Wanted) continue;

            device.Wanted = wanted;
            device.WantedSince = now;
        }
    }

    /// <summary>
    /// What to tell the window manager now: every device whose wanted state has held
    /// for the settle time and differs from what was last asserted.
    /// </summary>
    /// <remarks>
    /// Handing an action out marks it asserted. The host sends it; if the send fails
    /// the connection is gone, and <see cref="Forget"/> is how the host says so.
    /// </remarks>
    public IReadOnlyList<ProviderAction> Due(long now)
    {
        List<ProviderAction>? due = null;

        foreach (Device device in _devices)
        {
            if (_config.ContextFor(device.Kind) is not { } context) continue;
            if (device.Wanted == device.Asserted) continue;
            if (now - device.WantedSince < SettleMilliseconds) continue;

            device.Asserted = device.Wanted;

            (due ??= []).Add(new ProviderAction(
                context,
                device.Wanted,
                device.Wanted
                    ? $"{Name(device.Kind)} in use by {string.Join(", ", device.Apps)}"
                    : $"{Name(device.Kind)} no longer in use"));
        }

        return due ?? (IReadOnlyList<ProviderAction>)[];
    }

    /// <summary>
    /// How long until something becomes due, or null when nothing is pending.
    /// </summary>
    /// <remarks>
    /// The host waits exactly this long and no longer, so a change is reported the
    /// moment it has held long enough rather than on the next unrelated wake-up -
    /// and, when nothing is pending, the host waits for the registry alone.
    /// </remarks>
    public TimeSpan? Pending(long now)
    {
        long? soonest = null;

        foreach (Device device in _devices)
        {
            if (_config.ContextFor(device.Kind) is null) continue;
            if (device.Wanted == device.Asserted) continue;

            long due = device.WantedSince + SettleMilliseconds - now;
            if (soonest is null || due < soonest) soonest = due;
        }

        return soonest is { } wait ? TimeSpan.FromMilliseconds(Math.Max(0, wait)) : null;
    }

    /// <summary>
    /// The connection is gone, and every lease with it.
    /// </summary>
    /// <remarks>
    /// Nothing is asserted any more; the window manager has already released the pins
    /// itself, or has restarted and never had them. The next <see cref="Due"/> hands
    /// back a hold for every device still in use, at once - the settle time was
    /// served the first time round - and nothing for a device that has gone quiet,
    /// since there is no pin left to hand back.
    /// </remarks>
    public void Forget()
    {
        foreach (Device device in _devices) device.Asserted = false;
    }

    /// <summary>
    /// The file changed under a running watcher.
    /// </summary>
    /// <returns>
    /// The hand-backs for contexts this watcher held under a name the new settings no
    /// longer use, so a renamed context is not left pinned under its old name. The
    /// holds under the new names follow from <see cref="Due"/>.
    /// </returns>
    public IReadOnlyList<ProviderAction> Reconfigure(RasidConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<ProviderAction>? released = null;

        foreach (Device device in _devices)
        {
            string? before = _config.ContextFor(device.Kind);
            string? after = config.ContextFor(device.Kind);

            if (string.Equals(before, after, StringComparison.Ordinal)) continue;

            if (device.Asserted && before is not null)
            {
                (released ??= []).Add(new ProviderAction(
                    before, false, $"{Name(device.Kind)} is now reported as \"{after ?? "nothing"}\""));
            }

            // Whatever was asserted was under the old name. The new one starts from
            // nothing, and the settle time has already been served.
            device.Asserted = false;
        }

        _config = config;

        return released ?? (IReadOnlyList<ProviderAction>)[];
    }

    /// <summary>Whether a device is currently believed to be in use.</summary>
    public bool IsHeld(DeviceKind kind)
    {
        foreach (Device device in _devices)
            if (device.Kind == kind) return device.Asserted;

        return false;
    }

    private long SettleMilliseconds => (long)_config.EffectiveSettle.TotalMilliseconds;

    private static string Name(DeviceKind kind) => kind switch
    {
        DeviceKind.Camera => "camera",
        DeviceKind.Microphone => "microphone",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private sealed class Device(DeviceKind kind)
    {
        public DeviceKind Kind { get; } = kind;
        public bool Wanted { get; set; }
        public long WantedSince { get; set; }
        public bool Asserted { get; set; }
        public IReadOnlyList<string> Apps { get; set; } = [];
    }
}
