using System.Diagnostics;
using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;

namespace Shubbak.Companion;

/// <summary>The connections one round of the pump holds while it is connected.</summary>
/// <param name="Events">The subscribed connection, which carries nothing but events.</param>
/// <param name="Commands">
/// A second connection for requests, when the pump was asked to open one. A
/// subscribed connection cannot carry requests - the protocol's rule - so a client
/// that reads a snapshot or asks the window manager things while events stream
/// needs this one.
/// </param>
public sealed record PumpConnection(IpcClient Events, IpcClient? Commands);

/// <summary>Why a round of the pump ended.</summary>
public enum DisconnectReason
{
    /// <summary>The window manager closed the pipe, or went without closing it.</summary>
    Closed,

    /// <summary>Something threw on the connection.</summary>
    Faulted,

    /// <summary>
    /// The window manager refused the subscription. It is there and disagrees, which
    /// is a different thing from having gone: nothing else on other connections was
    /// lost.
    /// </summary>
    Refused,
}

/// <summary>
/// A subscription to the window manager's events that reconnects for as long as the
/// process lives.
/// </summary>
/// <remarks>
/// <para>
/// Every companion had one of these, and no two agreed: on the connect timeout, on
/// whether to check for the pipe before trying it, on whether a refused subscription
/// was a lost connection, on whether a pipe that closed quietly was worth a log line.
/// This is the union of what each got right. The pipe is looked for before it is
/// connected to, so an absent window manager costs a directory listing a second
/// rather than a timeout; a refused subscription is said once at warning and asked
/// again slowly, since it means a window manager older than this build and dropping
/// everything every second would be the worse bug; a pipe that ends without an error
/// is logged, because a read that meets the closed end reports the end of the stream
/// rather than an error and the gap otherwise has no line to account for it.
/// </para>
/// <para>
/// The subscription is in place before <see cref="Subscribed"/> runs, so a client
/// that reads a snapshot there misses nothing: every event between the handshake and
/// the snapshot is queued for this connection and applied on top. The other order
/// had a gap in which a focus change was in neither.
/// </para>
/// <para>
/// Everything raised here runs on the pump's own task. A companion whose windows
/// belong to a message loop posts to it rather than touching them from here.
/// </para>
/// </remarks>
public sealed class EventPump : IAsyncDisposable
{
    private readonly string _topics;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _task;

    /// <param name="topics">Comma-separated topics, or null for everything.</param>
    public EventPump(string? topics)
    {
        _topics = topics ?? "*";
    }

    /// <summary>What to call this program in a log line: <c>taj</c>, <c>dalil</c>, <c>ayn</c>.</summary>
    public string ProgramName { get; init; } = "this program";

    /// <summary>Which pipe to look for and connect to. The window manager's, unless a test says otherwise.</summary>
    public string PipeName { get; init; } = IpcProtocol.PipeName;

    /// <summary>How long to wait for the pipe to accept a connection once it exists.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait before trying again after a round ended.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait before asking again after the subscription was refused.</summary>
    public TimeSpan RefusedRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether each round also opens a connection for requests; see <see cref="PumpConnection.Commands"/>.</summary>
    public bool OpensCommandsConnection { get; init; }

    /// <summary>
    /// Runs once per round, after the subscription is accepted and before events are
    /// read. Where a snapshot belongs.
    /// </summary>
    public Func<PumpConnection, CancellationToken, Task>? Subscribed { get; set; }

    /// <summary>Runs for every event.</summary>
    public Func<PumpConnection, IpcEvent, CancellationToken, Task>? Event { get; set; }

    /// <summary>Runs when a round ends, with why. Not for a refusal on a connection that never subscribed... see <see cref="DisconnectReason"/>.</summary>
    public Action<DisconnectReason>? Disconnected { get; set; }

    /// <summary>
    /// Asked while the window manager's pipe is absent, with whether this pump has
    /// ever connected and when it last lost a connection (Stopwatch ticks; zero if it
    /// has not). True ends the pump for good and raises <see cref="Stopped"/>.
    /// </summary>
    public Func<bool, long, bool>? GiveUp { get; set; }

    /// <summary>The pump gave up; see <see cref="GiveUp"/>.</summary>
    public Action? Stopped { get; set; }

    /// <summary>Whether a round is currently connected.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>The connections of the current round, or null between rounds.</summary>
    public PumpConnection? Current { get; private set; }

    /// <summary>Starts the pump on its own task.</summary>
    public void Start()
    {
        if (_task is not null) throw new InvalidOperationException("The pump has already been started.");

        _task = Task.Run(() => PumpAsync(_stopping.Token));
    }

    private async Task PumpAsync(CancellationToken token)
    {
        // Zero until the first successful connection, and reset by every one after,
        // so a give-up clock only ever runs against a window manager that was really
        // there.
        long lostAtTicks = 0;
        bool everConnected = false;
        string? lastRefusal = null;
        TimeSpan delay = RetryDelay;

        while (!token.IsCancellationRequested)
        {
            // Whether the pipe opened this round, and how it ended. A pipe that opened
            // and ended for any reason but a refusal is a window manager gone.
            bool connected = false;
            DisconnectReason reason = DisconnectReason.Closed;

            try
            {
                if (!IpcClient.IsServerRunning(PipeName))
                {
                    if (GiveUp?.Invoke(everConnected, lostAtTicks) == true)
                    {
                        Stopped?.Invoke();
                        return;
                    }

                    await Task.Delay(RetryDelay, token).ConfigureAwait(false);
                    continue;
                }

                await using var events = new IpcClient { PipeName = PipeName };
                await events.ConnectAsync(ConnectTimeout, token).ConfigureAwait(false);

                IpcClient? commands = null;

                if (OpensCommandsConnection)
                {
                    commands = new IpcClient { PipeName = PipeName };
                    await commands.ConnectAsync(ConnectTimeout, token).ConfigureAwait(false);
                }

                await using IpcClient? commandsScope = commands;

                connected = true;
                everConnected = true;
                lostAtTicks = 0;

                bool subscribed;

                try
                {
                    await events.BeginSubscriptionAsync(_topics, token).ConfigureAwait(false);
                    subscribed = true;
                }
                catch (InvalidOperationException ex)
                {
                    reason = DisconnectReason.Refused;
                    subscribed = false;

                    if (!string.Equals(lastRefusal, ex.Message, StringComparison.Ordinal))
                    {
                        Log.Warn(LogCategory.Ipc,
                            $"the window manager refused the subscription: {ex.Message}. " +
                            $"This usually means shubbak-wm is older than {ProgramName}; restart it.");

                        lastRefusal = ex.Message;
                    }

                    delay = RefusedRetryDelay;
                }

                if (subscribed)
                {
                    lastRefusal = null;
                    delay = RetryDelay;

                    var connection = new PumpConnection(events, commands);
                    Current = connection;
                    IsConnected = true;

                    Log.Info(LogCategory.Ipc, "connected to the window manager");

                    if (Subscribed is { } onSubscribed)
                        await onSubscribed(connection, token).ConfigureAwait(false);

                    await foreach (IpcEvent notification in events.ReadEventsAsync(token).ConfigureAwait(false))
                    {
                        if (Event is { } handler)
                            await handler(connection, notification, token).ConfigureAwait(false);
                    }

                    // A read that meets the closed end of a pipe reports the end of the
                    // stream, not an error: the window manager went without saying so,
                    // or said so and the notice was lost. Said here because nothing else
                    // says it; the clean case announces itself from the shutdown event.
                    if (!token.IsCancellationRequested)
                        Log.Info(LogCategory.Ipc, "the window manager closed the connection");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Everything, not the two that were expected. This loop is a companion's
                // only source of what the window manager knows, and it runs on a task
                // nothing awaits - so a fault nobody caught was a fault nobody saw, and
                // left the companion drawing a picture frozen at whatever it last read.
                reason = DisconnectReason.Faulted;
                Log.Warn(LogCategory.Ipc, $"disconnected: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Current = null;
                IsConnected = false;

                // Stamped where the connection ended rather than where it was noticed,
                // so a give-up clock is measured from the loss itself.
                if (connected && lostAtTicks == 0) lostAtTicks = Stopwatch.GetTimestamp();

                if (connected && !token.IsCancellationRequested)
                    Disconnected?.Invoke(reason);
            }

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_task is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation is how the pump is asked to stop.
            }
        }

        _stopping.Dispose();
    }
}
