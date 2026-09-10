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
        _ => fact.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Whether a change to the fact waits out the settle time before it is believed.
    /// </summary>
    /// <remarks>
    /// Use does: a call opens and closes the devices several times while it sets up.
    /// Mute does not: it changes when a person presses a key, and a person who pressed
    /// the key wants the icon now.
    /// </remarks>
    public static bool Settles(this Fact fact) => fact != Fact.MicrophoneMuted;

    /// <summary>Every fact, in a stable order.</summary>
    public static IReadOnlyList<Fact> All { get; } = [Fact.CameraInUse, Fact.MicrophoneInUse, Fact.MicrophoneMuted];
}

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
    /// report says <c>nothing has set it</c> rather than <c>cleared by ayn.exe</c> -
    /// and a context somebody else also sets is theirs again rather than held off by
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
    private readonly Slot[] _slots;
    private AynConfig _config;

    public Provider(AynConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _slots = [.. FactNames.All.Select(fact => new Slot(fact))];
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

        foreach (Slot slot in _slots)
        {
            bool wanted = reading.Holds(slot.Fact);

            slot.Apps = slot.Fact switch
            {
                Fact.CameraInUse => reading.CameraApps,
                Fact.MicrophoneInUse => reading.MicrophoneApps,
                _ => [],
            };

            if (wanted == slot.Wanted) continue;

            slot.Wanted = wanted;
            slot.WantedSince = now;
        }
    }

    /// <summary>
    /// What to tell the window manager now: every fact whose wanted state has held
    /// long enough and differs from what was last asserted.
    /// </summary>
    /// <remarks>
    /// Handing an action out marks it asserted. The host sends it; if the send fails
    /// the connection is gone, and <see cref="Forget"/> is how the host says so.
    /// </remarks>
    public IReadOnlyList<ProviderAction> Due(long now)
    {
        List<ProviderAction>? due = null;

        foreach (Slot slot in _slots)
        {
            if (_config.ContextFor(slot.Fact) is not { } context) continue;
            if (slot.Wanted == slot.Asserted) continue;
            if (slot.Fact.Settles() && now - slot.WantedSince < SettleMilliseconds) continue;

            slot.Asserted = slot.Wanted;

            (due ??= []).Add(new ProviderAction(context, slot.Wanted, Because(slot)));
        }

        return due ?? (IReadOnlyList<ProviderAction>)[];
    }

    /// <summary>
    /// How long until something becomes due, or null when nothing is pending.
    /// </summary>
    /// <remarks>
    /// The host waits exactly this long and no longer, so a change is reported the
    /// moment it has held long enough rather than on the next unrelated wake-up -
    /// and, when nothing is pending, the host waits for the desk alone.
    /// </remarks>
    public TimeSpan? Pending(long now)
    {
        long? soonest = null;

        foreach (Slot slot in _slots)
        {
            if (_config.ContextFor(slot.Fact) is null) continue;
            if (slot.Wanted == slot.Asserted) continue;

            long due = slot.Fact.Settles() ? slot.WantedSince + SettleMilliseconds - now : 0;
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
    /// back a hold for every fact still true, at once - the settle time was served the
    /// first time round - and nothing for a fact that has gone false, since there is no
    /// pin left to hand back.
    /// </remarks>
    public void Forget()
    {
        foreach (Slot slot in _slots) slot.Asserted = false;
    }

    /// <summary>
    /// The file changed under a running watcher.
    /// </summary>
    /// <returns>
    /// The hand-backs for contexts this watcher held under a name the new settings no
    /// longer use, so a renamed context is not left pinned under its old name. The
    /// holds under the new names follow from <see cref="Due"/>.
    /// </returns>
    public IReadOnlyList<ProviderAction> Reconfigure(AynConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<ProviderAction>? released = null;

        foreach (Slot slot in _slots)
        {
            string? before = _config.ContextFor(slot.Fact);
            string? after = config.ContextFor(slot.Fact);

            if (string.Equals(before, after, StringComparison.Ordinal)) continue;

            if (slot.Asserted && before is not null)
            {
                (released ??= []).Add(new ProviderAction(
                    before, false, $"{slot.Fact.Wire()} is now reported as \"{after ?? "nothing"}\""));
            }

            // Whatever was asserted was under the old name. The new one starts from
            // nothing, and the settle time has already been served.
            slot.Asserted = false;
        }

        _config = config;

        return released ?? (IReadOnlyList<ProviderAction>)[];
    }

    /// <summary>Whether a fact is currently held on the window manager.</summary>
    public bool IsHeld(Fact fact)
    {
        foreach (Slot slot in _slots)
            if (slot.Fact == fact) return slot.Asserted;

        return false;
    }

    private long SettleMilliseconds => (long)_config.EffectiveSettle.TotalMilliseconds;

    private static string Because(Slot slot) => (slot.Fact, slot.Wanted) switch
    {
        (Fact.CameraInUse, true) => $"camera in use by {string.Join(", ", slot.Apps)}",
        (Fact.CameraInUse, false) => "camera no longer in use",
        (Fact.MicrophoneInUse, true) => $"microphone in use by {string.Join(", ", slot.Apps)}",
        (Fact.MicrophoneInUse, false) => "microphone no longer in use",
        (Fact.MicrophoneMuted, true) => "microphone muted",
        (Fact.MicrophoneMuted, false) => "microphone unmuted",
        _ => slot.Fact.Wire(),
    };

    private sealed class Slot(Fact fact)
    {
        public Fact Fact { get; } = fact;
        public bool Wanted { get; set; }
        public long WantedSince { get; set; }
        public bool Asserted { get; set; }
        public IReadOnlyList<string> Apps { get; set; } = [];
    }
}

/// <summary>
/// What a <c>signal "ayn" ...</c> asks for.
/// </summary>
/// <param name="Subject">What to act on: <c>microphone</c>.</param>
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
        ["microphone mute", "microphone unmute", "microphone toggle-mute"];

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

        if (subject is not ("microphone" or "mic"))
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
            refusal = $"the signal does not say what to do with the microphone. One of: mute, unmute, toggle-mute.";
            return null;
        }

        return new SignalRequest("microphone", verb);
    }
}
