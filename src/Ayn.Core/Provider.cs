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
public sealed record ProviderAction(Fact Fact, string Context, bool Hold, string Because)
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
    /// Handing an action out marks it asserted. The host sends it and reports how that
    /// went through <see cref="Sent"/>, which is where a refusal or a lost connection
    /// is booked.
    /// </remarks>
    public IReadOnlyList<ProviderAction> Due(long now)
    {
        List<ProviderAction>? due = null;

        foreach (Slot slot in _slots)
        {
            if (_config.ContextFor(slot.Fact) is not { } context) continue;
            if (!Outstanding(slot)) continue;
            if (slot.Fact.Settles() && now - slot.WantedSince < SettleMilliseconds) continue;

            slot.Asserted = slot.Wanted;

            (due ??= []).Add(new ProviderAction(slot.Fact, context, slot.Wanted, Because(slot)));
        }

        return due ?? (IReadOnlyList<ProviderAction>)[];
    }

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
            if (_config.ContextFor(slot.Fact) is null) continue;
            if (!Outstanding(slot)) continue;

            long due = slot.Fact.Settles() ? slot.WantedSince + SettleMilliseconds - now : 0;
            if (soonest is null || due < soonest) soonest = due;
        }

        return soonest is { } wait ? TimeSpan.FromMilliseconds(Math.Max(0, wait)) : null;
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
                Slot slot = SlotFor(action.Fact);
                slot.Asserted = false;
                slot.Refused = true;
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
    /// A context that kept its name is held again too, if it is still true. The window
    /// manager drops every pin on a context the reloaded file no longer declares, and
    /// says nothing to whoever set it; and a hold it refused before the file declared the
    /// context is exactly what a reload exists to ask again. Asserting a pin the window
    /// manager already holds replaces it with itself, so the cost of being sure is one
    /// command per held context per reload. A hand-back that is waiting out its settle
    /// is left booked, so it still goes out on time.
    /// </remarks>
    public IReadOnlyList<ProviderAction> Reconfigure(AynConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<ProviderAction>? released = null;

        foreach (Slot slot in _slots)
        {
            string? before = _config.ContextFor(slot.Fact);
            string? after = config.ContextFor(slot.Fact);

            slot.Refused = false;

            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                if (slot.Asserted && slot.Wanted) slot.Asserted = false;
                continue;
            }

            if (slot.Asserted && before is not null)
            {
                (released ??= []).Add(new ProviderAction(
                    slot.Fact, before, false, $"{slot.Fact.Wire()} is now reported as \"{after ?? "nothing"}\""));
            }

            // Whatever was asserted was under the old name. The new one starts from
            // nothing, and the settle time has already been served.
            slot.Asserted = false;
        }

        _config = config;

        return released ?? (IReadOnlyList<ProviderAction>)[];
    }

    /// <summary>Whether a fact is currently held on the window manager.</summary>
    public bool IsHeld(Fact fact) => SlotFor(fact).Asserted;

    private long SettleMilliseconds => (long)_config.EffectiveSettle.TotalMilliseconds;

    /// <summary>Whether the window manager has yet to be told what the desk says about a fact.</summary>
    private static bool Outstanding(Slot slot) => slot.Wanted != slot.Asserted && !slot.Refused;

    private Slot SlotFor(Fact fact)
    {
        foreach (Slot slot in _slots)
            if (slot.Fact == fact) return slot;

        throw new ArgumentOutOfRangeException(nameof(fact), fact, "Not a fact this provider knows.");
    }

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
