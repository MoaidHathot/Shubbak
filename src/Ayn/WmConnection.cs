using Ayn.Core;
using Shubbak.Companion;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using System.Collections.Concurrent;

namespace Ayn;

/// <summary>
/// The watcher's two connections to the window manager.
/// </summary>
/// <remarks>
/// <para>
/// Two, because one cannot do both jobs. A connection that has subscribed to events
/// carries nothing else, by the protocol's rule; and the connection that sets a
/// context with a lease has to stay open for as long as the pin should hold, since
/// the lease is the connection. So the commands go over one client that is opened on
/// first use and kept, and the events - a reload of the file, the window manager
/// leaving - arrive over the shared <see cref="EventPump"/>, which reconnects for as
/// long as this process runs.
/// </para>
/// <para>
/// Used from one thread. The watcher has no message loop and no reason for
/// concurrency: it sleeps until the registry or the window manager says something,
/// then does a few hundred microseconds of work. The pump runs on its own task and
/// only ever sets events for the loop to find.
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
    private readonly string _pipeName;
    private IpcClient? _commands;
    private string? _lastRefusal;

    /// <param name="pipeName">The window manager's pipe, unless a test says otherwise.</param>
    public WmConnection(string? pipeName = null)
    {
        _pipeName = pipeName ?? IpcProtocol.PipeName;

        _pump = new EventPump(Topics)
        {
            ProgramName = "ayn",
            PipeName = _pipeName,
            ConnectTimeout = ConnectTimeout,
            Event = (_, raised, _) =>
            {
                OnEvent(raised);
                return Task.CompletedTask;
            },
            Disconnected = reason =>
            {
                // The stream ending is the signal. Whatever leases the commands
                // connection held died with the window manager that granted them, and
                // the loop needs to know so it can hold them again on the next one. A
                // refusal is not that: the window manager is there and disagrees, and
                // the leases on the other connection are fine.
                if (reason != DisconnectReason.Refused) Lost.Set();
            },
        };
    }

    /// <summary>The events connection ended, which means the window manager is gone or restarting.</summary>
    public AutoResetEvent Lost { get; } = new(false);

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
    /// Sends one command over the held connection, opening it first if need be.
    /// </summary>
    /// <remarks>
    /// Synchronous by design; see the type's remarks. A refusal is logged once per
    /// distinct message, because the likeliest one - a context the file does not
    /// declare - will be repeated on every change until somebody declares it, and one
    /// line saying so is worth more than a hundred.
    /// </remarks>
    public SendOutcome Send(string command)
    {
        if (_commands is null)
        {
            if (!IpcClient.IsServerRunning(_pipeName)) return SendOutcome.Unreachable;

            try
            {
                var client = new IpcClient { PipeName = _pipeName };
                client.ConnectAsync(ConnectTimeout, _stopping.Token).GetAwaiter().GetResult();
                _commands = client;
                Log.Info(LogCategory.Ipc, "connected to the window manager");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug(LogCategory.Ipc, $"could not connect: {ex.Message}");
                return SendOutcome.Unreachable;
            }
        }

        try
        {
            IpcResponse response = _commands.SendAsync("command", command, _stopping.Token).GetAwaiter().GetResult();

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
            Log.Info(LogCategory.Ipc, $"lost the window manager while sending '{command}': {ex.Message}");
            Drop();
            return SendOutcome.Unreachable;
        }
    }

    /// <summary>Lets go of the commands connection, and with it every lease it held.</summary>
    public void Drop()
    {
        if (_commands is not { } client) return;

        _commands = null;

        try
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Already gone, which is what dropping it was for.
        }
    }

    private void OnEvent(IpcEvent raised)
    {
        switch (raised.Topic)
        {
            case "config.reloaded":
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

    /// <summary>Reads a signal payload; ours are queued, everyone else's are ignored.</summary>
    private void OnSignal(string json)
    {
        if (SignalPayload.Parse(json) is not { } signal || !signal.IsFor(SignalName)) return;

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
        await _pump.DisposeAsync().ConfigureAwait(false);

        // Closing the commands connection is what releases the leases. Nothing needs
        // to be sent for it; that is the point of a lease.
        Drop();

        _stopping.Dispose();
        Lost.Dispose();
        Dismissed.Dispose();
        Reloaded.Dispose();
        Signalled.Dispose();
    }
}
