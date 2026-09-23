using System.Collections.Concurrent;
using Shubbak.Ipc;

namespace Shubbak.Companion.Tests;

/// <summary>
/// The reconnecting subscription, against a real pipe.
/// </summary>
/// <remarks>
/// Each test binds a pipe of its own, since the real name is fixed per account and a
/// running window manager would answer instead of the server under test. The
/// intervals are set short so a reconnect is a matter of tens of milliseconds rather
/// than the second it is in production.
/// </remarks>
public sealed class EventPumpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(50);

    private static string IsolatedPipe() => $"shubbak-test-{Guid.NewGuid():N}";

    private static async ValueTask<IpcServer> StartServerAsync(string pipe, IpcServer.RequestHandler? handler = null)
    {
        var server = new IpcServer { PipeName = pipe };
        server.Start(handler ?? (request => Task.FromResult(new IpcResponse(request.Id, Ok: true, Data: "\"ok\""))));
        await Task.Yield();
        return server;
    }

    private static EventPump Pump(string pipe, string? topics = null, bool commands = false) => new(topics ?? IpcProtocol.Topics.First())
    {
        PipeName = pipe,
        ProgramName = "test",
        ConnectTimeout = Timeout,
        RetryDelay = Quick,
        RefusedRetryDelay = Quick,
        OpensCommandsConnection = commands,
    };

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(10);
        }

        return true;
    }

    [Fact]
    public async Task TheSubscriptionIsInPlaceBeforeTheSnapshotRuns()
    {
        // The whole reason Subscribed exists: an event published while the snapshot is
        // being read must reach the pump afterwards, rather than falling into a gap
        // between the two. Published from inside Subscribed, which is the worst moment.
        string pipe = IsolatedPipe();
        string topic = IpcProtocol.Topics.First();

        await using IpcServer server = await StartServerAsync(pipe);

        var received = new ConcurrentQueue<string>();
        var snapshotDone = new TaskCompletionSource();

        await using EventPump pump = Pump(pipe, topic);

        pump.Subscribed = (_, _) =>
        {
            // The server sees the subscription by now, or this publish goes nowhere.
            Assert.True(server.HasSubscribers(topic), "Subscribed ran before the server had the subscription");
            server.Publish(topic, "\"during the snapshot\"");
            snapshotDone.TrySetResult();
            return Task.CompletedTask;
        };

        pump.Event = (_, raised, _) =>
        {
            received.Enqueue(raised.Data);
            return Task.CompletedTask;
        };

        pump.Start();

        await snapshotDone.Task.WaitAsync(Timeout);

        Assert.True(await WaitAsync(() => received.Count == 1), "the event published during the snapshot never arrived");
        Assert.Equal("\"during the snapshot\"", received.Single());
        Assert.True(pump.IsConnected);
    }

    [Fact]
    public async Task TheServerGoingIsAClosedDisconnectAndTheNextOneIsFound()
    {
        string pipe = IsolatedPipe();
        string topic = IpcProtocol.Topics.First();

        var reasons = new ConcurrentQueue<DisconnectReason>();
        int connections = 0;

        await using EventPump pump = Pump(pipe, topic);
        pump.Subscribed = (_, _) => { Interlocked.Increment(ref connections); return Task.CompletedTask; };
        pump.Disconnected = reasons.Enqueue;

        IpcServer first = await StartServerAsync(pipe);
        pump.Start();

        Assert.True(await WaitAsync(() => connections == 1), "never connected to the first server");

        await first.DisposeAsync();

        Assert.True(await WaitAsync(() => reasons.Count == 1), "the server going was not reported");
        Assert.Equal(DisconnectReason.Closed, reasons.Single());
        Assert.False(pump.IsConnected);

        await using IpcServer second = await StartServerAsync(pipe);

        Assert.True(await WaitAsync(() => connections == 2), "never reconnected to the second server");
        Assert.True(pump.IsConnected);
    }

    [Fact]
    public async Task ARefusedSubscriptionIsNotALostConnection()
    {
        // The server refuses a topic it does not know. That is a window manager that is
        // there and disagrees - older than this build - not one that has gone, and a
        // pump that dropped everything on every refusal did so once a second for ever.
        string pipe = IsolatedPipe();

        var reasons = new ConcurrentQueue<DisconnectReason>();
        int subscribed = 0;

        await using IpcServer server = await StartServerAsync(pipe);
        await using EventPump pump = Pump(pipe, "no.such.topic");
        pump.Subscribed = (_, _) => { Interlocked.Increment(ref subscribed); return Task.CompletedTask; };
        pump.Disconnected = reasons.Enqueue;

        pump.Start();

        Assert.True(await WaitAsync(() => reasons.Count >= 2), "the refusal was not reported and retried");
        Assert.All(reasons, r => Assert.Equal(DisconnectReason.Refused, r));
        Assert.Equal(0, subscribed);
        Assert.False(pump.IsConnected);
    }

    [Fact]
    public async Task GivingUpEndsThePumpAndSaysSo()
    {
        // No server, ever. The give-up hook is asked each time the pipe is looked for
        // and not found, with whether the pump has ever connected.
        string pipe = IsolatedPipe();

        var stopped = new TaskCompletionSource();
        bool? everConnectedWhenAsked = null;

        await using EventPump pump = Pump(pipe);
        pump.GiveUp = (everConnected, _) => { everConnectedWhenAsked = everConnected; return true; };
        pump.Stopped = () => stopped.TrySetResult();

        pump.Start();

        await stopped.Task.WaitAsync(Timeout);

        Assert.False(everConnectedWhenAsked);
        Assert.False(pump.IsConnected);
    }

    [Fact]
    public async Task TheLossIsStampedWhenItHappensSoAGiveUpClockCanRunFromIt()
    {
        string pipe = IsolatedPipe();
        long lostAt = 0;
        bool everConnected = false;
        var asked = new TaskCompletionSource();
        int connections = 0;

        await using EventPump pump = Pump(pipe);
        pump.Subscribed = (_, _) => { Interlocked.Increment(ref connections); return Task.CompletedTask; };
        pump.GiveUp = (ever, lost) =>
        {
            everConnected = ever;
            lostAt = lost;
            asked.TrySetResult();
            return false;
        };

        IpcServer server = await StartServerAsync(pipe);
        pump.Start();
        Assert.True(await WaitAsync(() => connections == 1));

        await server.DisposeAsync();
        await asked.Task.WaitAsync(Timeout);

        Assert.True(everConnected);
        Assert.NotEqual(0, lostAt);
    }

    [Fact]
    public async Task ACommandsConnectionIsOpenedAlongsideWhenAskedFor()
    {
        // The bar's shape: a second connection for requests, since a subscribed one
        // carries nothing else.
        string pipe = IsolatedPipe();
        string topic = IpcProtocol.Topics.First();

        await using IpcServer server = await StartServerAsync(pipe,
            request => Task.FromResult(new IpcResponse(request.Id, Ok: true, Data: $"\"answered {request.Method}\"")));

        var answered = new TaskCompletionSource<string?>();

        await using EventPump pump = Pump(pipe, topic, commands: true);
        pump.Subscribed = async (connection, token) =>
        {
            Assert.NotNull(connection.Commands);
            IpcResponse response = await connection.Commands.SendAsync("query", "state", token);
            answered.TrySetResult(response.Data);
        };

        pump.Start();

        Assert.Equal("\"answered query\"", await answered.Task.WaitAsync(Timeout));

        // And the events connection is still the events connection: asking it is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() => pump.Current!.Events.SendAsync("query", "state"));
    }

    [Fact]
    public async Task DisposingStopsThePumpPromptly()
    {
        string pipe = IsolatedPipe();
        await using IpcServer server = await StartServerAsync(pipe);

        int connections = 0;
        var pump = Pump(pipe);
        pump.Subscribed = (_, _) => { Interlocked.Increment(ref connections); return Task.CompletedTask; };
        pump.Start();

        Assert.True(await WaitAsync(() => connections == 1));

        Task disposing = pump.DisposeAsync().AsTask();
        await disposing.WaitAsync(Timeout);

        Assert.False(pump.IsConnected);
    }
}

/// <summary>The message loop that waits rather than polls.</summary>
public sealed class MessageLoopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static (Thread Thread, MessageLoop Loop, TaskCompletionSource Ended) Start(Func<bool> pass, uint ceiling = 1000)
    {
        var loop = new MessageLoop();
        var ended = new TaskCompletionSource();

        var thread = new Thread(() =>
        {
            try
            {
                loop.Run(pass, () => ceiling);
            }
            finally
            {
                ended.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "message loop under test",
        };

        thread.Start();
        return (thread, loop, ended);
    }

    [Fact]
    public async Task PostedWorkRunsOnTheLoopsThread()
    {
        int loopThread = -1;
        var ran = new TaskCompletionSource<int>();

        (Thread thread, MessageLoop loop, TaskCompletionSource ended) = Start(() =>
        {
            loopThread = Environment.CurrentManagedThreadId;
            return true;
        });

        using (loop)
        {
            loop.Post(() => ran.TrySetResult(Environment.CurrentManagedThreadId));

            int where = await ran.Task.WaitAsync(Timeout);

            Assert.Equal(thread.ManagedThreadId, where);
            Assert.Equal(thread.ManagedThreadId, loopThread);

            loop.Stop();
            await ended.Task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task StopEndsTheLoopFromAnotherThread()
    {
        (_, MessageLoop loop, TaskCompletionSource ended) = Start(() => true, ceiling: 60_000);

        using (loop)
        {
            // A ceiling of a minute: only Stop waking the loop ends it in time.
            await Task.Delay(50);
            loop.Stop();

            await ended.Task.WaitAsync(Timeout);
            Assert.False(loop.Running);
        }
    }

    [Fact]
    public async Task APassReturningFalseEndsTheLoop()
    {
        int passes = 0;
        (_, MessageLoop loop, TaskCompletionSource ended) = Start(() => ++passes < 3, ceiling: 1);

        using (loop)
        {
            await ended.Task.WaitAsync(Timeout);
            Assert.Equal(3, passes);
            Assert.False(loop.Running);
        }
    }

    [Fact]
    public async Task WorkThatThrowsDoesNotEndTheLoop()
    {
        var second = new TaskCompletionSource();
        (_, MessageLoop loop, TaskCompletionSource ended) = Start(() => true);

        using (loop)
        {
            loop.Post(() => throw new InvalidOperationException("deliberate"));
            loop.Post(() => second.TrySetResult());

            await second.Task.WaitAsync(Timeout);
            Assert.True(loop.Running);

            loop.Stop();
            await ended.Task.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task WakeMakesAPassWithoutWaitingForTheCeiling()
    {
        int passes = 0;
        (_, MessageLoop loop, TaskCompletionSource ended) = Start(() => { Interlocked.Increment(ref passes); return true; }, ceiling: 60_000);

        using (loop)
        {
            SpinWait.SpinUntil(() => passes >= 1, Timeout);
            int before = passes;

            loop.Wake();

            Assert.True(SpinWait.SpinUntil(() => passes > before, Timeout), "Wake did not produce a pass");

            loop.Stop();
            await ended.Task.WaitAsync(Timeout);
        }
    }
}
