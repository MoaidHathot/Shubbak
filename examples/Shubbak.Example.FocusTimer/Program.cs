using System.Globalization;
using Shubbak.Ipc;

namespace Shubbak.Example.FocusTimer;

/// <summary>
/// A focus timer for Shubbak, over one pipe connection: the worked example in
/// <c>docs/extending.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three idioms, one connection. It <b>listens</b>: subscribed to <c>signal</c>, it
/// acts on <c>signal "focus" "start" [minutes]</c> and <c>signal "focus" "stop"</c>,
/// from a key, the bar or a terminal. It <b>provides</b>: while a timer runs it holds
/// <c>context "focusing"</c> with a lease on the same connection, so the config can
/// change whatever it likes while that holds and the pin dies if this process does. It
/// <b>publishes</b>: once a second it sends <c>signal focus mm:ss</c>, which the bar's
/// <c>source "focus" kind="signal"</c> shows, and it says the value again when a bar
/// connects and asks (<c>signal "announce"</c>).
/// </para>
/// <para>
/// What the window manager does on its own: a restart ends the connection, which ends
/// the lease; this program notices the stream end, reconnects once a second until the
/// pipe is back, and pins again if a timer is still running. A reload of the config
/// drops the pin of a context the new file no longer declares; this program pins
/// again on <c>config.reloaded</c>, if the reload landed. <c>exit-all</c> is the one
/// shutdown it leaves on.
/// </para>
/// <para>
/// Nothing polls. The program waits on the event stream; while a timer runs, a timer
/// of its own fires once a second - the rate the bar changes - and not otherwise.
/// </para>
/// </remarks>
internal static class Program
{
    private const string ContextName = "focusing";
    private const string SignalName = "focus";
    private const int DefaultMinutes = 25;

    private static async Task<int> Main(string[] args)
    {
        int defaultMinutes = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int given) && given > 0
            ? given
            : DefaultMinutes;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

        var timer = new FocusTimer(defaultMinutes);

        Console.WriteLine($"focus timer: waiting for `signal {SignalName} start [minutes]`; Ctrl+C to leave");

        try
        {
            await RunAsync(timer, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C: leaving is letting go. The connection closes with the process,
            // and the lease with it.
        }

        return 0;
    }

    /// <summary>Connects, serves one connection until it ends, and does it again, for as long as the process runs.</summary>
    private static async Task RunAsync(FocusTimer timer, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // A directory listing, not a connection attempt: an absent window manager
            // costs nothing to look for.
            if (!IpcClient.IsServerRunning())
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                continue;
            }

            bool leaveForGood;

            try
            {
                leaveForGood = await ServeOneConnectionAsync(timer, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
            {
                Console.WriteLine($"lost the window manager: {ex.Message}");
                leaveForGood = false;
            }

            if (leaveForGood) return;

            // The lease went with the connection, whatever the timer thinks; it is
            // pinned again on the next connection if the timer is still running.
            timer.Held = false;

            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        }
    }

    /// <summary>One connection, from subscribe to the stream ending. True when the window manager asked everything to leave.</summary>
    private static async Task<bool> ServeOneConnectionAsync(FocusTimer timer, CancellationToken token)
    {
        await using var client = new IpcClient();
        await client.ConnectAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);

        // Subscribed first, so nothing between here and the first pin is missed.
        await client.BeginSubscriptionAsync(
            string.Join(',', IpcProtocol.SignalTopic, "config.reloaded", IpcProtocol.ShutdownTopic), token).ConfigureAwait(false);

        Console.WriteLine("connected to the window manager");

        // Whatever is true is said now: a timer that was running across a restart is
        // pinned again, and the bar is told the value it would otherwise wait for.
        await timer.SyncAsync(client, token).ConfigureAwait(false);

        // The once-a-second tick, armed only while a timer runs. A PeriodicTimer
        // rather than a sleep in the loop, so the event stream is read the moment
        // something arrives and the tick costs nothing when nothing is running.
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
        Task tick = timer.IsRunning ? ticker.WaitForNextTickAsync(token).AsTask() : Task.Delay(Timeout.Infinite, token);

        IAsyncEnumerator<IpcEvent> events = client.ReadEventsAsync(token).GetAsyncEnumerator(token);
        Task<bool> next = events.MoveNextAsync().AsTask();

        try
        {
            while (true)
            {
                Task completed = await Task.WhenAny(next, tick).ConfigureAwait(false);

                if (completed == tick)
                {
                    await tick.ConfigureAwait(false);
                    await timer.TickAsync(client, token).ConfigureAwait(false);
                    tick = timer.IsRunning ? ticker.WaitForNextTickAsync(token).AsTask() : Task.Delay(Timeout.Infinite, token);
                    continue;
                }

                if (!await next.ConfigureAwait(false)) return false;   // the stream ended: the window manager went

                IpcEvent raised = events.Current;
                next = events.MoveNextAsync().AsTask();

                switch (raised.Topic)
                {
                    case IpcProtocol.SignalTopic:
                        bool wasRunning = timer.IsRunning;
                        await OnSignalAsync(timer, client, raised.Data, token).ConfigureAwait(false);

                        // Arm or disarm the tick as the timer starts or stops.
                        if (timer.IsRunning != wasRunning)
                            tick = timer.IsRunning ? ticker.WaitForNextTickAsync(token).AsTask() : Task.Delay(Timeout.Infinite, token);
                        break;

                    case "config.reloaded":
                        // A reload that landed may have dropped the pin - a context the
                        // new file no longer declares - and says nothing; pinning again
                        // is one command. A reload that was refused touched nothing.
                        if (ConfigReloadNotice.Parse(raised.Data).Accepted && timer.IsRunning)
                        {
                            timer.Held = false;
                            await timer.SyncAsync(client, token).ConfigureAwait(false);
                        }
                        break;

                    case IpcProtocol.ShutdownTopic:
                        if (ShutdownNotice.IsForEveryone(raised.Data))
                        {
                            Console.WriteLine("the window manager is shutting everything down; leaving");
                            return true;
                        }

                        Console.WriteLine("the window manager is restarting; reconnecting when it is back");
                        break;
                }
            }
        }
        finally
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Acts on a signal: ours are a request or an echo of our own value; <c>announce</c> is a bar asking.</summary>
    private static async Task OnSignalAsync(FocusTimer timer, IpcClient client, string payload, CancellationToken token)
    {
        if (SignalPayload.Parse(payload) is not { } signal) return;

        if (signal.IsFor(IpcProtocol.AnnounceSignal))
        {
            // A bar connected with a signal source in its file and wants every
            // published value said again.
            await timer.SayAsync(client, token).ConfigureAwait(false);
            return;
        }

        if (!signal.IsFor(SignalName) || signal.Arguments.Count == 0) return;

        // Our own values come back to us - `signal focus 24:59` is a signal like any
        // other, and we are subscribed - so only the words that are a request are acted
        // on. A clear has no words at all and never gets this far.
        switch (signal.Arguments[0])
        {
            case "start":
                int minutes = signal.Arguments.Count > 1 && int.TryParse(signal.Arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int asked) && asked > 0
                    ? asked
                    : timer.DefaultMinutes;

                await timer.StartAsync(client, minutes, token).ConfigureAwait(false);
                break;

            case "stop":
                await timer.StopAsync(client, token).ConfigureAwait(false);
                break;

            default:
                if (!IsClock(signal.Arguments[0]))
                    Console.WriteLine($"signal {SignalName}: unknown word '{signal.Arguments[0]}'; expected start or stop");
                break;
        }
    }

    private static bool IsClock(string word) =>
        word.Length == 5 && word[2] == ':' && char.IsAsciiDigit(word[0]) && char.IsAsciiDigit(word[1]) && char.IsAsciiDigit(word[3]) && char.IsAsciiDigit(word[4]);

    /// <summary>
    /// The timer: when it ends, and whether the pin is in place - so the value can be
    /// said again and the pin made again on a new connection, from the one truth.
    /// </summary>
    private sealed class FocusTimer(int defaultMinutes)
    {
        private DateTime? _endsAt;

        public int DefaultMinutes { get; } = defaultMinutes;

        public bool IsRunning => _endsAt is not null;

        /// <summary>Whether the context is pinned on the current connection.</summary>
        public bool Held { get; set; }

        public async Task StartAsync(IpcClient client, int minutes, CancellationToken token)
        {
            _endsAt = DateTime.UtcNow.AddMinutes(minutes);
            Console.WriteLine($"focusing for {minutes} min, until {_endsAt.Value.ToLocalTime():HH:mm}");
            await SyncAsync(client, token).ConfigureAwait(false);
        }

        public async Task StopAsync(IpcClient client, CancellationToken token)
        {
            if (_endsAt is null) return;

            _endsAt = null;
            Console.WriteLine("stopped");
            await SyncAsync(client, token).ConfigureAwait(false);
        }

        public async Task TickAsync(IpcClient client, CancellationToken token)
        {
            if (_endsAt is not { } endsAt) return;

            if (endsAt <= DateTime.UtcNow)
            {
                _endsAt = null;
                Console.WriteLine("time");
            }

            await SyncAsync(client, token).ConfigureAwait(false);
        }

        /// <summary>Makes the window manager agree with the timer: the pin held or let go, and the bar's value current.</summary>
        public async Task SyncAsync(IpcClient client, CancellationToken token)
        {
            if (IsRunning && !Held)
            {
                // A lease: the pin lives exactly as long as this connection does.
                IpcResponse pinned = await client.SendAsync("command", $"context --set {ContextName} --lease", token).ConfigureAwait(false);

                if (pinned.Ok) Held = true;
                else Console.WriteLine($"could not hold \"{ContextName}\": {pinned.Error} (is it declared in the config?)");
            }
            else if (!IsRunning && Held)
            {
                // --auto hands the context back to its conditions - it has none, so it
                // is simply off - and leaves no pin behind.
                await client.SendAsync("command", $"context --auto {ContextName}", token).ConfigureAwait(false);
                Held = false;
            }

            await SayAsync(client, token).ConfigureAwait(false);
        }

        /// <summary>Puts the time left on the bar, or clears it.</summary>
        public async Task SayAsync(IpcClient client, CancellationToken token)
        {
            string value = string.Empty;

            if (_endsAt is { } endsAt)
            {
                TimeSpan left = endsAt - DateTime.UtcNow;
                if (left > TimeSpan.Zero)
                    value = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
            }

            // A signal with no arguments clears the value, which hides the widget.
            // Quoted, because the command is parsed like a line of config.
            string command = value.Length == 0 ? $"signal {SignalName}" : $"signal {SignalName} \"{value}\"";
            await client.SendAsync("command", command, token).ConfigureAwait(false);
        }
    }
}
