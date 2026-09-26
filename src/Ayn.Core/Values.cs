using Shubbak.Config;

namespace Ayn.Core;

/// <summary>
/// The readings the watcher can publish as words rather than as facts: a number or a
/// name the bar shows, where a <see cref="Fact"/> is a yes or a no the window manager
/// holds.
/// </summary>
/// <remarks>
/// <para>
/// A fact is a context, and a context is a boolean by design: it is something the
/// file can hang behaviour on. The battery's percentage is not that. Nothing in the
/// window manager should change at 41 percent that did not change at 42, and yet the
/// number is worth showing, and the watcher already reads it for <c>battery-low</c>
/// and threw it away. A value goes to the bar as a <c>signal</c> the window manager
/// carries without reading - <c>signal "battery" "41"</c> - and a
/// <c>source "battery" kind="signal"</c> in the bar's section shows it. The window
/// manager learns no new word, and holds nothing: the bar asks for the values again
/// when it connects, and the watcher says them again.
/// </para>
/// <para>
/// Three so far, each a reading the watcher takes anyway. The next one is another
/// member here and another key in the file, not another thing the bar knows how to
/// poll.
/// </para>
/// </remarks>
public enum Value
{
    /// <summary>How much battery is left, as a whole number of percent; empty when there is none.</summary>
    BatteryPercent,

    /// <summary>The default speaker's name as Windows shows it; empty when there is none.</summary>
    SpeakerDeviceName,

    /// <summary>The default microphone's name; empty when there is none.</summary>
    MicrophoneDeviceName,
}

/// <summary>The names a <see cref="Value"/> goes by in the file and the log.</summary>
public static class ValueNames
{
    /// <summary>The key the file writes for a value, under its subject's block.</summary>
    public static string Wire(this Value value) => value switch
    {
        Value.BatteryPercent => "battery-percent",
        Value.SpeakerDeviceName => "speaker device-name",
        Value.MicrophoneDeviceName => "microphone device-name",
        _ => value.ToString().ToLowerInvariant(),
    };

    /// <summary>Every value, in a stable order.</summary>
    public static IReadOnlyList<Value> All { get; } =
        [Value.BatteryPercent, Value.SpeakerDeviceName, Value.MicrophoneDeviceName];

    /// <summary>
    /// What a value reads as in a reading: the words the bar will show, or the empty
    /// string when there is nothing - no battery, no speaker - which hides the widget.
    /// </summary>
    public static string Text(this Value value, Reading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        return value switch
        {
            Value.BatteryPercent => reading.Power?.BatteryPercent is { } percent
                ? percent.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty,
            Value.SpeakerDeviceName => reading.SpeakerDeviceName ?? string.Empty,
            Value.MicrophoneDeviceName => reading.MicrophoneDeviceName ?? string.Empty,
            _ => string.Empty,
        };
    }
}

/// <summary>One value to tell the bar, by way of the window manager.</summary>
/// <param name="Value">Which value, so the outcome can be booked against it.</param>
/// <param name="Signal">The signal's name, as the file gave it.</param>
/// <param name="Text">The words; empty to clear the readout.</param>
public sealed record ValueAction(Value Value, string Signal, string Text)
{
    /// <summary>
    /// The command, spelled the way the window manager's parser reads it: the signal's
    /// name and one argument, each quoted when it needs to be.
    /// </summary>
    /// <remarks>
    /// An empty value is the bare name - <c>signal battery</c> - because the command
    /// language has no spelling for an empty argument: <c>""</c> is read back as no
    /// token at all. The bar reads a signal with nothing after it as the empty value,
    /// which is what clears the readout, so the two are one thing to it. A device
    /// name that holds both kinds of quote has no spelling in this language either;
    /// its double quotes are turned into the typographic kind rather than the value
    /// being dropped, since a name shown slightly wrong beats a widget that is blank
    /// for the one headset with a pun in its name.
    /// </remarks>
    public string Command => Text.Length == 0
        ? $"signal {CommandParser.Quote(Signal)}"
        : $"signal {CommandParser.Quote(Signal)} {CommandParser.Quote(CommandParser.CanQuote(Text) ? Text : Text.Replace('"', '\u201D'))}";
}

/// <summary>
/// Decides which values to say, and when: each on a change, all on request.
/// </summary>
/// <remarks>
/// <para>
/// Pure, like <see cref="Provider"/>, and the same shape: readings in, commands out,
/// with the host reporting how each send fared. Simpler, because a value has no
/// settle time - the number is the number - and nothing to hand back: a signal the
/// window manager does not keep needs no releasing. What a value does need is to be
/// said again when someone new is listening, and <see cref="Forget"/> is that: the
/// next <see cref="Due"/> says every value, whether or not it changed.
/// </para>
/// <para>
/// A value whose signal the file stops naming, or renames, is cleared under its old
/// name once - an empty signal - so the bar does not go on showing the last number
/// it was told under a name nothing will say again.
/// </para>
/// </remarks>
public sealed class ValuePublisher
{
    private readonly Dictionary<Value, string> _said = [];
    private AynConfig _config;
    private Reading _last = Reading.Idle;

    public ValuePublisher(AynConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>The settings in force.</summary>
    public AynConfig Config => _config;

    /// <summary>Whether the file names a signal for any value.</summary>
    public bool PublishesAnything => _config.PublishesAnyValue;

    /// <summary>A fresh reading of the desk.</summary>
    public void Observe(Reading reading) => _last = reading ?? throw new ArgumentNullException(nameof(reading));

    /// <summary>
    /// Every value that reads differently from what was last said, or that has not
    /// been said since <see cref="Forget"/>. Handing one out books it as said; the
    /// host reports otherwise through <see cref="Sent"/>.
    /// </summary>
    public IReadOnlyList<ValueAction> Due()
    {
        List<ValueAction>? due = null;

        foreach (Value value in ValueNames.All)
        {
            if (_config.SignalFor(value) is not { } signal) continue;

            string text = value.Text(_last);

            if (_said.TryGetValue(value, out string? said) && string.Equals(said, text, StringComparison.Ordinal))
                continue;

            _said[value] = text;
            (due ??= []).Add(new ValueAction(value, signal, text));
        }

        return due ?? (IReadOnlyList<ValueAction>)[];
    }

    /// <summary>
    /// The host says how a send fared. Nobody answering means the next window manager
    /// has heard nothing, so everything is due again; a refusal is the command's
    /// problem and is not retried, since the same words would be refused again.
    /// </summary>
    public void Sent(ValueAction action, SendOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (outcome == SendOutcome.Unreachable) Forget();
    }

    /// <summary>
    /// Say everything again: the connection was lost, or a listener asked. Nothing is
    /// booked as said any more, so the next <see cref="Due"/> is every value.
    /// </summary>
    public void Forget() => _said.Clear();

    /// <summary>
    /// The file changed under a running watcher.
    /// </summary>
    /// <returns>
    /// One clearing signal for each value whose name the new file dropped or changed,
    /// so a bar still listening under the old name shows nothing rather than the last
    /// number. The values under their new names follow from <see cref="Due"/>.
    /// </returns>
    public IReadOnlyList<ValueAction> Reconfigure(AynConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<ValueAction>? cleared = null;

        foreach (Value value in ValueNames.All)
        {
            string? before = _config.SignalFor(value);
            string? after = config.SignalFor(value);

            if (string.Equals(before, after, StringComparison.Ordinal)) continue;

            // Only a name that was actually said under is worth clearing; one the
            // window manager never heard has nothing on any bar.
            if (before is not null && _said.ContainsKey(value))
                (cleared ??= []).Add(new ValueAction(value, before, string.Empty));

            _said.Remove(value);
        }

        _config = config;

        return cleared ?? (IReadOnlyList<ValueAction>)[];
    }
}
