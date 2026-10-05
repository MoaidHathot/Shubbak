using Ayn.Core;
using Shubbak.Companion;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using System.Collections.Concurrent;

namespace Ayn;

/// <summary>
/// The watcher's connection to the window manager.
/// </summary>
/// <remarks>
/// <para>
/// One connection, for both jobs. The events - a reload of the file, a signal, the
/// window manager leaving - arrive over the shared <see cref="EventPump"/>, which
/// reconnects for as long as this process runs; and the commands go over the same
/// connection, which is what a lease wants: the pin lives exactly as long as the
/// subscription does, and the stream ending is the leases ending, with nothing to
/// infer. There used to be two, because a subscribed connection refused to carry
/// requests - the pipe's one rough edge for a provider, and the reason a watcher
/// held a connection it only ever learned two things on.
/// </para>
/// <para>
/// Used from one thread. The watcher has no message loop and no reason for
/// concurrency: it sleeps until the registry or the window manager says something,
/// then does a few hundred microseconds of work. The pump runs on its own task and
/// only ever sets events for the loop to find; the client it holds is safe to send
/// on from the loop while the pump reads it.
/// </para>
/// </remarks>
internal sealed class WmConnection : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private const string Topics = "config.reloaded," + IpcProtocol.SignalTopic + "," + IpcProtocol.ShutdownTopic;

    /// <summary>The signal this process answers to: <c>signal "ayn" ...</c>.</summary>
    public const string SignalName = "ayn";

    private readonly CancellationTokenSource _stopping = new();
    private readonly EventPump _pump;
    private string? _lastRefusal;

    /// <param name="pipeName">The window manager's pipe, unless a test says otherwise.</param>
    public WmConnection(string? pipeName = null)
    {
        _pump = new EventPump(Topics)
        {
            ProgramName = "ayn",
            PipeName = pipeName ?? IpcProtocol.PipeName,
            ConnectTimeout = ConnectTimeout,
            Subscribed = (_, _) =>
            {
                // The loop is asleep between polls; woken, it holds again at once
                // rather than on its next look, so a window manager that restarts is
                // holding the watcher's contexts within the pump's own retry.
                Found.Set();
                return Task.CompletedTask;
            },
            Event = (_, raised, _) =>
            {
                OnEvent(raised);
                return Task.CompletedTask;
            },
            Disconnected = reason =>
            {
                // The stream ending is the signal. Every lease was on this connection
                // and died with the window manager that granted it, and the loop
                // needs to know so it can hold them again on the next one. A refusal
                // is not that: the window manager is there and disagrees, and nothing
                // was held on a connection that never subscribed.
                if (reason != DisconnectReason.Refused) Lost.Set();
            },
        };
    }

    /// <summary>The connection ended, which means the window manager is gone or restarting, and every lease with it.</summary>
    public AutoResetEvent Lost { get; } = new(false);

    /// <summary>The connection is up and subscribed; whatever is due can be sent.</summary>
    public AutoResetEvent Found { get; } = new(false);

    /// <summary>
    /// The window manager left and asked everything to leave with it, which is
    /// <c>exit-all</c>; the watcher should stop rather than wait for it.
    /// </summary>
    /// <remarks>
    /// Manual-reset, unlike its neighbours, because it is answered by leaving and
    /// nothing after it matters; a request to stop that could be consumed and lost
    /// by an unlucky wake would be worse than one that stays raised.
    /// </remarks>
    public ManualResetEvent Dismissed { get; } = new(false);

    /// <summary>The window manager re-read the configuration file.</summary>
    public AutoResetEvent Reloaded { get; } = new(false);

    /// <summary>Somebody raised <c>signal "ayn" ...</c>; the requests are in <see cref="Requests"/>.</summary>
    public AutoResetEvent Signalled { get; } = new(false);

    /// <summary>
    /// Somebody raised <c>signal "announce"</c>: a listener that wants every published
    /// value said again. The loop answers by forgetting what it has said and flushing.
    /// </summary>
    /// <remarks>
    /// A flag rather than a queue, because two announces in one wake - three bars
    /// connecting within a moment of each other, each asking - want one answer, not
    /// three. Read and cleared by the loop with <see cref="TakeAnnounceRequested"/>.
    /// </remarks>
    private volatile bool _announceRequested;

    /// <summary>Whether an announce arrived since this was last asked; clears it.</summary>
    public bool TakeAnnounceRequested()
    {
        if (!_announceRequested) return false;

        _announceRequested = false;
        return true;
    }

    /// <summary>
    /// What the signals asked for, in order. Queued by the pump, drained by the loop.
    /// </summary>
    /// <remarks>
    /// The bar's mute button and a keybinding both arrive here: the window manager
    /// carries <c>signal "ayn" "microphone" "toggle-mute"</c> without reading it, as it
    /// carries the palette's, which is how the bar gets a mute button without the
    /// window manager learning the word.
    /// </remarks>
    public ConcurrentQueue<SignalRequest> Requests { get; } = new();

    public void Start() => _pump.Start();

    /// <summary>
    /// Sends one command over the connection, if there is one.
    /// </summary>
    /// <remarks>
    /// Synchronous by design; see the type's remarks. Unreachable between the pump's
    /// rounds, which the loop answers by trying again on <see cref="Found"/> or on its
    /// next look, whichever is first. A refusal is logged once per distinct message,
    /// because the likeliest one - a context the file does not declare - will be
    /// repeated on every change until somebody declares it, and one line saying so is
    /// worth more than a hundred.
    /// </remarks>
    public SendOutcome Send(string command)
    {
        if (_pump.Current is not { Events: { } client }) return SendOutcome.Unreachable;

        try
        {
            IpcResponse response = client.SendAsync("command", command, _stopping.Token).GetAwaiter().GetResult();

            if (response.Ok)
            {
                _lastRefusal = null;
                return SendOutcome.Accepted;
            }

            string refusal = response.Error ?? "refused without a reason";

            if (!string.Equals(refusal, _lastRefusal, StringComparison.Ordinal))
            {
                _lastRefusal = refusal;
                Log.Warn(LogCategory.Ipc, $"'{command}' was refused: {refusal}");
            }

            return SendOutcome.Refused;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The connection went under the request; the pump notices the same end and
            // raises Lost, which is where the loop learns to forget and hold again.
            Log.Info(LogCategory.Ipc, $"lost the window manager while sending '{command}': {ex.Message}");
            return SendOutcome.Unreachable;
        }
    }

    private void OnEvent(IpcEvent raised)
    {
        switch (raised.Topic)
        {
            case "config.reloaded":
                // A reload the window manager refused - the file had errors and it kept
                // what it had - is not followed either. Following it meant re-reading a
                // file that does not parse and running on the defaults it yields, which
                // for a watcher is letting go of every context it holds: a stray brace
                // saved mid-call dropped `microphone-in-use`, and with it whatever the
                // config disarms during a call. The loop is not woken; nothing changed.
                if (!ConfigReloadNotice.Parse(raised.Data).Accepted)
                {
                    Log.Info(LogCategory.Config, "the window manager refused the saved file and kept its configuration; so does the watcher");
                    break;
                }

                Reloaded.Set();
                break;

            case IpcProtocol.SignalTopic:
                OnSignal(raised.Data);
                break;

            case IpcProtocol.ShutdownTopic:
                if (ShutdownNotice.IsForEveryone(raised.Data))
                {
                    Log.Info(LogCategory.Ipc, "the window manager is shutting down and asked everything to go with it; the watcher leaves");
                    Dismissed.Set();
                }
                else
                {
                    Log.Info(LogCategory.Ipc, "the window manager is shutting down; the watcher stays and reconnects when it returns");
                }
                break;
        }
    }

    /// <summary>Reads a signal payload; ours are queued, an announce is noted, everyone else's are ignored.</summary>
    private void OnSignal(string json)
    {
        if (SignalPayload.Parse(json) is not { } signal) return;

        if (signal.IsFor(IpcProtocol.AnnounceSignal))
        {
            // The bar, most likely, having just connected with a signal source in its
            // file. Whatever this watcher publishes is said again on the next wake.
            _announceRequested = true;
            Signalled.Set();
            return;
        }

        if (!signal.IsFor(SignalName)) return;

        if (SignalRequest.Parse(signal.Arguments, out string? refusal) is { } request)
        {
            Requests.Enqueue(request);
            Signalled.Set();
        }
        else
        {
            Log.Warn(LogCategory.Ipc, $"signal \"{SignalName}\" {string.Join(" ", signal.Arguments)}: {refusal}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        // Closing the pump closes the connection, and that is what releases the
        // leases. Nothing needs to be sent for it; that is the point of a lease.
        await _pump.DisposeAsync().ConfigureAwait(false);

        _stopping.Dispose();
        Lost.Dispose();
        Found.Dispose();
        Dismissed.Dispose();
        Reloaded.Dispose();
        Signalled.Dispose();
    }
}
