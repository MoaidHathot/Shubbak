namespace Ayn.Core;

/// <summary>
/// The facts the watcher can supply, each a context the window manager holds while
/// the fact is true.
/// </summary>
/// <remarks>
/// Named <c>subject-state</c>, so the facts about one device sort together and the
/// next device slots in without renaming: <c>camera-in-use</c>, <c>microphone-in-use</c>,
/// <c>microphone-muted</c>. A fact is a boolean and nothing more. What a meeting is,
/// and what one should do to the desktop, is composed from these in the file.
/// </remarks>
public enum Fact
{
    /// <summary>A program has the camera open.</summary>
    CameraInUse,

    /// <summary>A program has the microphone open.</summary>
    MicrophoneInUse,

    /// <summary>The default microphone is muted at the system level.</summary>
    MicrophoneMuted,

    /// <summary>A program is capturing the screen - sharing it, or recording it.</summary>
    ScreenCaptured,

    /// <summary>The default speaker is muted at the system level.</summary>
    SpeakerMuted,

    /// <summary>The machine is running from its battery.</summary>
    OnBattery,

    /// <summary>The battery is at or below the configured percentage.</summary>
    BatteryLow,

    /// <summary>A laptop's lid is shut.</summary>
    LidClosed,

    /// <summary>Windows judges nobody to be at the keyboard.</summary>
    UserAway,

    /// <summary>Apps are set to the dark theme.</summary>
    DarkTheme,

    /// <summary>
    /// The default speaker is a device whose name matches a rule. Only ever asked with
    /// a pattern - <c>speaker { device "*jabra*" "on-headset" }</c> - since the fact
    /// without one would be "there is a speaker".
    /// </summary>
    SpeakerDevice,

    /// <summary>The default microphone is a device whose name matches a rule; see <see cref="SpeakerDevice"/>.</summary>
    MicrophoneDevice,
}

/// <summary>The names a <see cref="Fact"/> goes by outside the process.</summary>
public static class FactNames
{
    /// <summary>The fact's name, as the file and the log spell it.</summary>
    public static string Wire(this Fact fact) => fact switch
    {
        Fact.CameraInUse => "camera-in-use",
        Fact.MicrophoneInUse => "microphone-in-use",
        Fact.MicrophoneMuted => "microphone-muted",
        Fact.ScreenCaptured => "screen-captured",
        Fact.SpeakerMuted => "speaker-muted",
        Fact.OnBattery => "on-battery",
        Fact.BatteryLow => "battery-low",
        Fact.LidClosed => "lid-closed",
        Fact.UserAway => "user-away",
        Fact.DarkTheme => "dark-theme",
        Fact.SpeakerDevice => "speaker-device",
        Fact.MicrophoneDevice => "microphone-device",
        _ => fact.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Whether a change to the fact waits out the settle time before it is believed.
    /// </summary>
    /// <remarks>
    /// Use does: a call opens and closes the devices several times while it sets up,
    /// and a share is set up the same way. Mute does not: it changes when a person
    /// presses a key, and a person who pressed the key wants the icon now. Nor do the
    /// power facts or the theme: the mains lead is in or it is not, and Windows has
    /// already taken its time deciding that nobody is there.
    /// </remarks>
    public static bool Settles(this Fact fact) =>
        fact is Fact.CameraInUse or Fact.MicrophoneInUse or Fact.ScreenCaptured;

    /// <summary>The device whose use the fact reports, or null for a fact about something else.</summary>
    public static DeviceKind? Device(this Fact fact) => fact switch
    {
        Fact.CameraInUse => DeviceKind.Camera,
        Fact.MicrophoneInUse => DeviceKind.Microphone,
        Fact.ScreenCaptured => DeviceKind.Screen,
        _ => null,
    };

    /// <summary>
    /// Whether the fact is about which device is the default, which is only ever
    /// asked with a name pattern; see <see cref="Fact.SpeakerDevice"/>.
    /// </summary>
    public static bool IsAboutADeviceName(this Fact fact) =>
        fact is Fact.SpeakerDevice or Fact.MicrophoneDevice;

    /// <summary>Every fact, in a stable order.</summary>
    public static IReadOnlyList<Fact> All { get; } =
    [
        Fact.CameraInUse, Fact.MicrophoneInUse, Fact.MicrophoneMuted,
        Fact.ScreenCaptured, Fact.SpeakerMuted,
        Fact.OnBattery, Fact.BatteryLow, Fact.LidClosed, Fact.UserAway,
        Fact.DarkTheme,
        Fact.SpeakerDevice, Fact.MicrophoneDevice,
    ];
}

/// <summary>
/// What a slot is about: a fact, and for a <c>by</c> rule the one program it is about.
/// </summary>
/// <param name="Fact">The fact.</param>
/// <param name="App">
/// The program the rule names, as a pattern - <c>ms-teams.exe</c>, <c>*teams*</c> -
/// or null for the fact about any program. For a fact about which device is the
/// default (<see cref="FactNames.IsAboutADeviceName"/>) it is the device's name
/// pattern instead: <c>*jabra*</c>.
/// </param>
public readonly record struct FactKey(Fact Fact, string? App = null)
{
    /// <summary>
    /// The name, for the log: <c>camera-in-use</c>, <c>camera-in-use by ms-teams.exe</c>,
    /// or <c>speaker-device matching *jabra*</c>.
    /// </summary>
    public override string ToString() => App is null
        ? Fact.Wire()
        : Fact.IsAboutADeviceName() ? $"{Fact.Wire()} matching {App}" : $"{Fact.Wire()} by {App}";
}

/// <summary>How a command sent to the window manager fared.</summary>
public enum SendOutcome
{
    /// <summary>The window manager accepted it.</summary>
    Accepted,

    /// <summary>The window manager answered and said no; the connection is fine.</summary>
    Refused,

    /// <summary>Nobody answered. The connection, if there was one, is gone.</summary>
    Unreachable,
}

/// <summary>
/// One thing to tell the window manager.
/// </summary>
/// <param name="Fact">The fact this is about, so the outcome can be booked against it.</param>
/// <param name="Context">The context concerned.</param>
/// <param name="Hold">True to hold it on with a lease; false to hand it back to its conditions.</param>
/// <param name="Because">Why, for the log: <c>camera in use by Teams.exe</c>.</param>
/// <param name="Ttl">
/// How long the window manager may hold it unrenewed, or null for as long as the
/// connection lives. Set when the file asks for renewal; see <see cref="AynConfig.Renew"/>.
/// </param>
/// <param name="App">The program a <c>by</c> rule names, or null for the fact about any program.</param>
public sealed record ProviderAction(
    Fact Fact, string Context, bool Hold, string Because, TimeSpan? Ttl = null, string? App = null)
{
    /// <summary>The slot this books against.</summary>
    public FactKey Key => new(Fact, App);

    /// <summary>
    /// The command, spelled the way the window manager's parser reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--lease</c> on the way up and nothing on the way down. The lease is what
    /// makes a crashed watcher harmless: the pin dies with the connection that made
    /// it, so the window manager is never left believing a meeting is on. Handing back
    /// with <c>--auto</c> rather than <c>--clear</c> leaves no pin at all, so the
    /// report says <c>nothing has set it</c> rather than <c>cleared by ayn.exe</c> -
    /// and a context somebody else also sets is theirs again rather than held off by
    /// us.
    /// </para>
    /// <para>
    /// Quoted, because a context may be called anything the file can spell. A time to
    /// live goes on the hold when the file asks for renewal: then a watcher that is
    /// alive but stuck - connection open, loop wedged - loses its pins too, a little
    /// after it stops renewing them.
    /// </para>
    /// </remarks>
    public string Command => Hold
        ? Ttl is { } ttl
            ? $"context --set \"{Context}\" --lease --ttl {(long)Math.Ceiling(ttl.TotalSeconds)}s"
            : $"context --set \"{Context}\" --lease"
        : $"context --auto \"{Context}\"";
}

/// <summary>
/// Decides what to tell the window manager from what the desk says.
/// </summary>
/// <remarks>
/// <para>
/// Pure: readings and a clock in, commands out. The host feeds it every time the
/// registry or the audio endpoint changes and asks what is due; the debounce, the
/// recovery after a lost connection and a rename of a context under a running watcher
/// are all here, where a test can hold them to account, and the registry, the
/// endpoint and the pipe are not.
/// </para>
/// <para>
/// Two states per fact: what the desk says (<em>wanted</em>) and what the window
/// manager has been told (<em>asserted</em>). For the facts that settle, a change to
/// the first is believed only after it has held for the settle time, because a call
/// opens and closes the camera several times while it sets up, and a context that
/// flapped with it would run its on-enter and on-exit twice - the same reason the
/// window manager's own contexts linger. A change that reverses itself inside the
/// settle time is never reported.
/// </para>
/// </remarks>
public sealed class Provider
{
    private readonly List<Slot> _slots;
    private AynConfig _config;
    private Reading _lastReading = Reading.Idle;

    public Provider(AynConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _slots = [.. SlotsFor(config)];
    }

    /// <summary>The settings in force.</summary>
    public AynConfig Config => _config;

    /// <summary>
    /// A fresh reading of the desk.
    /// </summary>
    /// <param name="reading">What the desk says now.</param>
    /// <param name="now">The clock, in milliseconds; only differences matter.</param>
    public void Observe(Reading reading, long now)
    {
        ArgumentNullException.ThrowIfNull(reading);

        _lastReading = reading;

        foreach (Slot slot in _slots)
        {
            (bool wanted, IReadOnlyList<string> apps) = Judge(slot.Key, reading);

            slot.Apps = apps;

            if (wanted == slot.Wanted) continue;

            slot.Wanted = wanted;
            slot.WantedSince = now;
        }
    }

    /// <summary>
    /// What the desk says about one slot: whether its fact holds, and by whose hand.
    /// </summary>
    /// <remarks>
    /// A device's use is the programs using it less the ones the file says to ignore -
    /// a recording tool that keeps the camera open all day is not a meeting. A
    /// <c>by</c> rule holds while one of the programs left matches its pattern. Low
    /// battery is the reading's percentage against the file's threshold, which is why
    /// the reading cannot answer it alone.
    /// </remarks>
    private (bool Wanted, IReadOnlyList<string> Apps) Judge(FactKey key, Reading reading)
    {
        if (key.Fact.Device() is { } device)
        {
            IReadOnlyList<string> apps = Counted(reading.AppsFor(device), _config.IgnoredApps(device));

            if (key.App is { } pattern)
            {
                string[] matching = [.. apps.Where(app => AppMatches(pattern, app))];
                return (matching.Length > 0, matching);
            }

            return (apps.Count > 0, apps);
        }

        if (key.Fact == Fact.BatteryLow)
            return (reading.Power?.BatteryPercent is { } percent && percent <= _config.BatteryLowPercent, []);

        if (key.Fact.IsAboutADeviceName())
        {
            // The device's name is carried whether or not it matches, so the log can
            // say what the default became when a rule lets go: "speaker is now
            // Realtek Speakers", not merely "no longer matches".
            string? name = key.Fact == Fact.SpeakerDevice ? reading.SpeakerDeviceName : reading.MicrophoneDeviceName;

            if (name is null) return (false, []);

            return (key.App is { } pattern && AppMatches(pattern, name), [name]);
        }

        return (reading.Holds(key.Fact), []);
    }

    /// <summary>The programs that count: everything less the ignored.</summary>
    private static IReadOnlyList<string> Counted(IReadOnlyList<string> apps, IReadOnlyList<string> ignored)
    {
        if (ignored.Count == 0 || apps.Count == 0) return apps;

        return [.. apps.Where(app => !ignored.Any(pattern => AppMatches(pattern, app)))];
    }

    /// <summary>
    /// Whether a program's name matches a pattern from the file: case-insensitive, with
    /// <c>*</c> and <c>?</c> as wildcards, so <c>*teams*</c> covers the packaged and the
    /// classic Teams alike.
    /// </summary>
    public static bool AppMatches(string pattern, string app)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(app);

        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, app, ignoreCase: true);
    }

    /// <summary>
    /// What to tell the window manager now: every fact whose wanted state has held
    /// long enough and differs from what was last asserted, and every hold that is due
    /// to be renewed.
    /// </summary>
    /// <remarks>
    /// Handing an action out marks it asserted. The host sends it and reports how that
    /// went through <see cref="Sent"/>, which is where a refusal or a lost connection
    /// is booked.
    /// </remarks>
    public IReadOnlyList<ProviderAction> Due(long now)
    {
        List<ProviderAction>? due = null;

        foreach (Slot slot in _slots)
        {
            if (_config.ContextFor(slot.Key) is not { } context) continue;

            if (Outstanding(slot))
            {
                if (slot.Key.Fact.Settles() && now - slot.WantedSince < SettleMilliseconds) continue;

                slot.Asserted = slot.Wanted;
                slot.AssertedAt = now;

                (due ??= []).Add(Action(slot, context, slot.Wanted, Because(slot)));
                continue;
            }

            // A hold that is due to be renewed: the same command again, which the
            // window manager reads as the pin replaced by itself with a fresh clock.
            if (RenewMilliseconds is { } renew && slot.Asserted && slot.Wanted && now - slot.AssertedAt >= renew)
            {
                slot.AssertedAt = now;
                (due ??= []).Add(Action(slot, context, true, $"renewing {slot.Key}"));
            }
        }

        return due ?? (IReadOnlyList<ProviderAction>)[];
    }

    private ProviderAction Action(Slot slot, string context, bool hold, string because) =>
        new(slot.Key.Fact, context, hold, because, hold ? _config.LeaseTtl : null, slot.Key.App);

    /// <summary>
    /// How long until something becomes due, or null when nothing is pending.
    /// </summary>
    /// <remarks>
    /// The host waits exactly this long and no longer, so a change is reported the
    /// moment it has held long enough rather than on the next unrelated wake-up -
    /// and, when nothing is pending, the host waits for the desk alone. Asks the same
    /// question <see cref="Due"/> asks, or the two would disagree about a slot and the
    /// host would wake for nothing, at once, for ever.
    /// </remarks>
    public TimeSpan? Pending(long now)
    {
        long? soonest = null;

        foreach (Slot slot in _slots)
        {
            if (_config.ContextFor(slot.Key) is null) continue;

            if (Outstanding(slot))
            {
                Consider(slot.Key.Fact.Settles() ? slot.WantedSince + SettleMilliseconds - now : 0);
                continue;
            }

            if (RenewMilliseconds is { } renew && slot.Asserted && slot.Wanted)
                Consider(slot.AssertedAt + renew - now);
        }

        return soonest is { } wait ? TimeSpan.FromMilliseconds(Math.Max(0, wait)) : null;

        void Consider(long due)
        {
            if (soonest is null || due < soonest) soonest = due;
        }
    }

    /// <summary>
    /// How long the host should sleep before looking again.
    /// </summary>
    /// <param name="retrying">Whether the last flush found the window manager unreachable with something to say.</param>
    /// <param name="pending">What <see cref="Pending"/> answered.</param>
    /// <param name="retry">How often to try an unreachable window manager again.</param>
    /// <remarks>
    /// While the window manager cannot be reached, whatever is due cannot be sent, so a
    /// pending time of zero - "due now" - must not be waited for; it would be waited for
    /// zero milliseconds, at once, in a loop, and a watcher that spins a core whenever
    /// the window manager restarts with the microphone muted is what this replaces. The
    /// retry interval is the floor while retrying; a settle that expires sooner is still
    /// honoured, since it may be the window manager that has just come back.
    /// </remarks>
    public static TimeSpan NextWait(bool retrying, TimeSpan? pending, TimeSpan retry)
    {
        if (!retrying) return pending ?? Timeout.InfiniteTimeSpan;

        return pending is { } soon && soon > TimeSpan.Zero && soon < retry ? soon : retry;
    }

    /// <summary>
    /// The host says how a send fared, so the provider's picture of the window manager
    /// stays true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refused hold is unbooked: the window manager holds nothing, so nothing will be
    /// handed back later, and the fact is not asked again until the file is reloaded or
    /// the connection is remade - the likeliest refusal is a context the file does not
    /// declare, and it will be refused on every change until somebody declares it. A
    /// refused hand-back needs nothing; <see cref="Due"/> already booked it as gone.
    /// </para>
    /// <para>
    /// An unreachable window manager means every lease died with the connection, which
    /// is <see cref="Forget"/>.
    /// </para>
    /// </remarks>
    public void Sent(ProviderAction action, SendOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(action);

        switch (outcome)
        {
            case SendOutcome.Refused when action.Hold:
                if (SlotFor(action.Key) is { } slot)
                {
                    slot.Asserted = false;
                    slot.Refused = true;
                }

                break;

            case SendOutcome.Unreachable:
                Forget();
                break;
        }
    }

    /// <summary>
    /// The connection is gone, and every lease with it.
    /// </summary>
    /// <remarks>
    /// Nothing is asserted any more; the window manager has already released the pins
    /// itself, or has restarted and never had them. The next <see cref="Due"/> hands
    /// back a hold for every fact still true, at once - the settle time was served the
    /// first time round - and nothing for a fact that has gone false, since there is no
    /// pin left to hand back. A refusal is forgotten with it: the window manager that
    /// comes back may have a different file.
    /// </remarks>
    public void Forget()
    {
        foreach (Slot slot in _slots)
        {
            slot.Asserted = false;
            slot.Refused = false;
        }
    }

    /// <summary>
    /// The file changed under a running watcher.
    /// </summary>
    /// <returns>
    /// The hand-backs for contexts this watcher held under a name the new settings no
    /// longer use, so a renamed context is not left pinned under its old name. The
    /// holds under the new names follow from <see cref="Due"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A context that kept its name is held again too, if it is still true. The window
    /// manager drops every pin on a context the reloaded file no longer declares, and
    /// says nothing to whoever set it; and a hold it refused before the file declared the
    /// context is exactly what a reload exists to ask again. Asserting a pin the window
    /// manager already holds replaces it with itself, so the cost of being sure is one
    /// command per held context per reload. A hand-back that is waiting out its settle
    /// is left booked, so it still goes out on time.
    /// </para>
    /// <para>
    /// A <c>by</c> rule the new file drops takes its slot with it, handing back what it
    /// held; one the new file adds starts from nothing and is judged against the last
    /// reading at once, so a program already on the camera is noticed without waiting
    /// for it to do something.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ProviderAction> Reconfigure(AynConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<ProviderAction>? released = null;
        HashSet<FactKey> keep = [.. SlotsFor(config).Select(slot => slot.Key)];

        foreach (Slot slot in _slots.ToArray())
        {
            string? before = _config.ContextFor(slot.Key);
            string? after = keep.Contains(slot.Key) ? config.ContextFor(slot.Key) : null;

            slot.Refused = false;

            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                if (slot.Asserted && slot.Wanted) slot.Asserted = false;
                continue;
            }

            if (slot.Asserted && before is not null)
            {
                (released ??= []).Add(new ProviderAction(
                    slot.Key.Fact, before, false,
                    $"{slot.Key} is now reported as \"{after ?? "nothing"}\"", App: slot.Key.App));
            }

            // Whatever was asserted was under the old name. The new one starts from
            // nothing, and the settle time has already been served.
            slot.Asserted = false;

            if (!keep.Contains(slot.Key)) _slots.Remove(slot);
        }

        foreach (Slot slot in SlotsFor(config))
        {
            if (SlotFor(slot.Key) is not null) continue;

            _slots.Add(slot);
        }

        _config = config;

        // The new slots, and the ones whose ignore list changed, judged against what
        // the desk last said - without waiting for it to say something again.
        Observe(_lastReading, long.MinValue / 2);

        return released ?? (IReadOnlyList<ProviderAction>)[];
    }

    /// <summary>Whether a fact is currently held on the window manager.</summary>
    public bool IsHeld(Fact fact, string? app = null) => SlotFor(new FactKey(fact, app))?.Asserted == true;

    private long SettleMilliseconds => (long)_config.EffectiveSettle.TotalMilliseconds;

    private long? RenewMilliseconds =>
        _config.Renew is { } renew && renew > TimeSpan.Zero ? (long)renew.TotalMilliseconds : null;

    /// <summary>Whether the window manager has yet to be told what the desk says about a fact.</summary>
    private static bool Outstanding(Slot slot) => slot.Wanted != slot.Asserted && !slot.Refused;

    private Slot? SlotFor(FactKey key)
    {
        foreach (Slot slot in _slots)
            if (slot.Key == key) return slot;

        return null;
    }

    /// <summary>One slot per fact, one per <c>by</c> rule, and one per <c>device</c> rule.</summary>
    /// <remarks>
    /// The device-name facts get a bare slot with the rest, for uniformity; it is
    /// never due, since the file cannot name a context for the fact without a pattern.
    /// </remarks>
    private static IEnumerable<Slot> SlotsFor(AynConfig config)
    {
        foreach (Fact fact in FactNames.All) yield return new Slot(new FactKey(fact));

        foreach (AppRule rule in config.AppRules)
            yield return new Slot(new FactKey(rule.Device.InUseFact(), rule.App));

        foreach (DeviceRule rule in config.DeviceRules)
            yield return new Slot(new FactKey(rule.Fact, rule.Pattern));
    }

    private static string Because(Slot slot)
    {
        string apps = string.Join(", ", slot.Apps);

        return (slot.Key.Fact, slot.Wanted) switch
        {
            (Fact.CameraInUse, true) => $"camera in use by {apps}",
            (Fact.CameraInUse, false) => "camera no longer in use",
            (Fact.MicrophoneInUse, true) => $"microphone in use by {apps}",
            (Fact.MicrophoneInUse, false) => "microphone no longer in use",
            (Fact.MicrophoneMuted, true) => "microphone muted",
            (Fact.MicrophoneMuted, false) => "microphone unmuted",
            (Fact.ScreenCaptured, true) => $"screen captured by {apps}",
            (Fact.ScreenCaptured, false) => "screen no longer captured",
            (Fact.SpeakerMuted, true) => "speaker muted",
            (Fact.SpeakerMuted, false) => "speaker unmuted",
            (Fact.OnBattery, true) => "running on battery",
            (Fact.OnBattery, false) => "back on the mains",
            (Fact.BatteryLow, true) => "battery low",
            (Fact.BatteryLow, false) => "battery no longer low",
            (Fact.LidClosed, true) => "lid closed",
            (Fact.LidClosed, false) => "lid opened",
            (Fact.UserAway, true) => "user away",
            (Fact.UserAway, false) => "user back",
            (Fact.DarkTheme, true) => "dark theme",
            (Fact.DarkTheme, false) => "light theme",
            (Fact.SpeakerDevice, true) => $"speaker is \"{apps}\"",
            (Fact.SpeakerDevice, false) => apps.Length > 0 ? $"speaker is now \"{apps}\"" : "no speaker",
            (Fact.MicrophoneDevice, true) => $"microphone is \"{apps}\"",
            (Fact.MicrophoneDevice, false) => apps.Length > 0 ? $"microphone is now \"{apps}\"" : "no microphone",
            _ => slot.Key.ToString(),
        } + (slot.Key.App is { } app && slot.Wanted ? $" (rule for {app})" : string.Empty);
    }

    private sealed class Slot(FactKey key)
    {
        public FactKey Key { get; } = key;
        public bool Wanted { get; set; }
        public long WantedSince { get; set; }
        public bool Asserted { get; set; }

        /// <summary>When the hold was last sent, for renewal.</summary>
        public long AssertedAt { get; set; }

        /// <summary>
        /// The window manager refused to hold this; not asked again until a reload or a
        /// reconnect, which are the two things that can change its answer.
        /// </summary>
        public bool Refused { get; set; }

        public IReadOnlyList<string> Apps { get; set; } = [];
    }
}
/// <summary>
/// What a <c>signal "ayn" ...</c> asks for.
/// </summary>
/// <param name="Subject">What to act on: <c>microphone</c> or <c>speaker</c>.</param>
/// <param name="Verb">What to do: <c>mute</c>, <c>unmute</c>, <c>toggle-mute</c>.</param>
/// <remarks>
/// Subject then verb, so the next subject slots in beside this one and a keybinding
/// reads as a sentence: <c>signal "ayn" "microphone" "toggle-mute"</c>. The window
/// manager carries the words without reading them, exactly as it carries the
/// palette's; this is what makes the bar's mute button possible without the window
/// manager learning the word.
/// </remarks>
public sealed record SignalRequest(string Subject, string Verb)
{
    /// <summary>The subjects and verbs understood, for the refusal.</summary>
    public static IReadOnlyList<string> Accepted { get; } =
    [
        "microphone mute", "microphone unmute", "microphone toggle-mute",
        "speaker mute", "speaker unmute", "speaker toggle-mute",
    ];

    /// <summary>
    /// Reads the arguments after the signal's name, or says why they cannot be.
    /// </summary>
    public static SignalRequest? Parse(IReadOnlyList<string> arguments, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        refusal = null;

        if (arguments.Count == 0)
        {
            refusal = $"the signal says nothing to do. One of: {string.Join(", ", Accepted)}.";
            return null;
        }

        string subject = arguments[0].ToLowerInvariant();
        string verb = arguments.Count > 1 ? arguments[1].ToLowerInvariant() : string.Empty;

        subject = subject switch
        {
            "microphone" or "mic" => "microphone",
            "speaker" or "speakers" or "output" => "speaker",
            _ => string.Empty,
        };

        if (subject.Length == 0)
        {
            refusal = $"'{arguments[0]}' is not something ayn acts on. One of: {string.Join(", ", Accepted)}.";
            return null;
        }

        verb = verb switch
        {
            "toggle" or "toggle-mute" => "toggle-mute",
            "mute" or "unmute" => verb,
            _ => string.Empty,
        };

        if (verb.Length == 0)
        {
            refusal = $"the signal does not say what to do with the {subject}. One of: mute, unmute, toggle-mute.";
            return null;
        }

        return new SignalRequest(subject, verb);
    }
}
