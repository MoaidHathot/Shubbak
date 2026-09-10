using Ayn.Core;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Ayn;

/// <summary>How a command fared.</summary>
internal enum SendOutcome
{
    /// <summary>The window manager accepted it.</summary>
    Accepted,

    /// <summary>The window manager answered and said no; the connection is fine.</summary>
    Refused,

    /// <summary>Nobody answered. The connection, if there was one, is gone.</summary>
    Unreachable,
}

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
/// leaving - arrive over a second that reconnects for as long as this process runs,
/// exactly as the palette's does.
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
    private IpcClient? _commands;
    private Task? _pump;
    private string? _lastRefusal;

    /// <summary>The events connection ended, which means the window manager is gone or restarting.</summary>
    public AutoResetEvent Lost { get; } = new(false);

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

    /// <summary>Whether a commands connection is currently open.</summary>
    public bool IsConnected => _commands is not null;

    public void Start() => _pump = Task.Run(() => PumpAsync(_stopping.Token));

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
            if (!IpcClient.IsServerRunning()) return SendOutcome.Unreachable;

            try
            {
                var client = new IpcClient();
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

    private async Task PumpAsync(CancellationToken token)
    {
        TimeSpan wait = TimeSpan.FromSeconds(1);
        bool everConnected = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!IpcClient.IsServerRunning())
                {
                    await Task.Delay(wait, token).ConfigureAwait(false);
                    continue;
                }

                await using IpcClient client = new();
                await client.ConnectAsync(ConnectTimeout, token).ConfigureAwait(false);

                IAsyncEnumerator<IpcEvent> events =
                    client.SubscribeAsync(Topics, token).GetAsyncEnumerator(token);

                try
                {
                    everConnected = true;

                    while (await events.MoveNextAsync().ConfigureAwait(false))
                    {
                        switch (events.Current.Topic)
                        {
                            case "config.reloaded":
                                Reloaded.Set();
                                break;

                            case IpcProtocol.SignalTopic:
                                OnSignal(events.Current.Data);
                                break;

                            case IpcProtocol.ShutdownTopic:
                                Log.Info(LogCategory.Ipc, "the window manager is shutting down; the watcher stays and reconnects when it returns");
                                break;
                        }
                    }
                }
                finally
                {
                    await events.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug(LogCategory.Ipc, $"events connection ended: {ex.Message}");
            }

            // The stream ending is the signal. Whatever leases the commands connection
            // held died with the window manager that granted them, and the loop needs
            // to know so it can hold them again on the next one.
            if (everConnected)
            {
                everConnected = false;
                Lost.Set();
            }

            try
            {
                await Task.Delay(wait, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Reads a signal payload; ours are queued, everyone else's are ignored.</summary>
    /// <remarks>
    /// Hand-parsed, as the palette parses the same payload: two fields, and a DTO in
    /// the protocol for them would make every client that does not care carry it.
    /// </remarks>
    private void OnSignal(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("name", out JsonElement name) ||
                !string.Equals(name.GetString(), SignalName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            List<string> arguments = [];

            if (document.RootElement.TryGetProperty("arguments", out JsonElement list))
                foreach (JsonElement argument in list.EnumerateArray())
                    if (argument.GetString() is { } value)
                        arguments.Add(value);

            if (SignalRequest.Parse(arguments, out string? refusal) is { } request)
            {
                Requests.Enqueue(request);
                Signalled.Set();
            }
            else
            {
                Log.Warn(LogCategory.Ipc, $"signal \"{SignalName}\" {string.Join(" ", arguments)}: {refusal}");
            }
        }
        catch (JsonException ex)
        {
            Log.Debug(LogCategory.Ipc, $"malformed signal payload: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_pump is { } pump)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation is how the pump is asked to stop.
            }
        }

        // Closing the commands connection is what releases the leases. Nothing needs
        // to be sent for it; that is the point of a lease.
        Drop();

        _stopping.Dispose();
        Lost.Dispose();
        Reloaded.Dispose();
        Signalled.Dispose();
    }
}
