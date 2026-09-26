using Shubbak.Config;
using Shubbak.Ipc;

namespace Taj.Core.Sources;

/// <summary>
/// A value another program puts on the bar by raising a <c>signal</c>:
/// <c>shubbak signal battery 87</c> makes <c>{{ battery }}</c> read <c>87</c>.
/// </summary>
/// <remarks>
/// <para>
/// The third way onto the bar, beside a clock and a program the bar runs itself. A
/// <c>kind="command"</c> source is the bar's own worker: the bar starts it, one copy
/// per desk, and reads its stdout. This is for a program that already exists and
/// already knows the value - the watcher, which reads the battery for its
/// <c>battery-low</c> fact and had nowhere to put the percentage; a script of your own
/// that runs for other reasons - and that would otherwise have to be started a second
/// time by the bar to say it. It says it once, over the pipe every program already
/// has, and the window manager carries the word without reading it, as it carries
/// the palette's and the watcher's.
/// </para>
/// <para>
/// The value is the signal's arguments joined by a space: one argument is itself, and
/// <c>shubbak signal weather Sunny 21C</c> - unquoted, split by the shell - still
/// reads <c>Sunny 21C</c>. No arguments is the empty string, which hides a widget, so
/// a publisher clears its readout by raising the bare name. Which signal is listened
/// for is the source's name unless <c>signal=</c> says otherwise, so two bars can
/// call one value two things.
/// </para>
/// <para>
/// A signal is fire-and-forget and the window manager keeps none of it, so a bar that
/// starts after the value was last sent would be blank until it next changed. The
/// bar therefore raises <see cref="IpcProtocol.AnnounceSignal"/> when it connects
/// with one of these in its file, and a publisher that hears it says its values
/// again; <see cref="AnnounceCommand"/> is the command it sends. Nothing here needs
/// to run for that: this source has no timer, no thread and no process. It is a slot
/// the host fills, which is why it is the one source kind that costs nothing while
/// nobody is publishing.
/// </para>
/// </remarks>
public sealed class SignalSource : SourceBase
{
    /// <summary>
    /// What the bar sends to have every published value said again; see the remarks.
    /// </summary>
    public static string AnnounceCommand { get; } = $"signal {CommandParser.Quote(IpcProtocol.AnnounceSignal)}";

    /// <param name="name">The value's name, as templates read it.</param>
    /// <param name="signal">The signal to listen for, or null for the value's own name.</param>
    public SignalSource(string name, string? signal = null) : base(name)
    {
        Signal = string.IsNullOrWhiteSpace(signal) ? name : signal;
    }

    /// <summary>The signal's name, compared without regard for case as every signal is.</summary>
    public string Signal { get; }

    /// <summary>Nothing to start; the value arrives when somebody raises the signal.</summary>
    public override void Start() { }

    /// <summary>A signal with this source's name arrived; its arguments are the value.</summary>
    public void Receive(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Publish(Join(arguments));
    }

    /// <summary>The arguments as one value. One is itself, so the common case allocates nothing.</summary>
    internal static string Join(IReadOnlyList<string> arguments) => arguments.Count switch
    {
        0 => string.Empty,
        1 => arguments[0],
        _ => string.Join(' ', arguments),
    };
}
