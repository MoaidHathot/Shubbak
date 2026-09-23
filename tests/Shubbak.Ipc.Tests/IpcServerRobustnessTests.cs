using Shubbak.Ipc;

namespace Shubbak.Ipc.Tests;

/// <summary>
/// What the server does with a request it cannot answer properly.
/// </summary>
/// <remarks>
/// Each test binds a pipe of its own, for the reason <see cref="IpcSubscriberGateTests"/>
/// gives: the real name is fixed per account and a running window manager would answer
/// instead of the server under test.
/// </remarks>
public sealed class IpcServerRobustnessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static string IsolatedPipe() => $"shubbak-test-{Guid.NewGuid():N}";

    private static async Task<IpcClient> ConnectAsync(string pipe)
    {
        var client = new IpcClient { PipeName = pipe };
        await client.ConnectAsync(Timeout);
        return client;
    }

    [Fact]
    public async Task TheServerIsListeningTheMomentStartReturns()
    {
        // The listener pipes used to be created inside the accept loops' tasks, so
        // Start returned with nothing yet listening and a client connecting in the
        // next few milliseconds - a test, a script - was told the server was not
        // running. Asked a hundred times, because the race was a race.
        for (int i = 0; i < 100; i++)
        {
            string pipe = IsolatedPipe();

            await using var server = new IpcServer { PipeName = pipe };
            server.Start(request => Task.FromResult(new IpcResponse(request.Id, Ok: true)));

            Assert.True(IpcClient.IsServerRunning(pipe), $"attempt {i}: no pipe the instant Start returned");
        }
    }

    [Fact]
    public async Task AHandlerThatThrowsAnswersTheRequestAndKeepsTheConnection()
    {
        // It used to close the pipe with no reply and no log line: the exception left
        // the line handler, the connection loop read it as the client having gone, and
        // the client waited out its ten-second timeout for an answer that was never
        // coming.
        string pipe = IsolatedPipe();
        (string Method, Exception Error)? faulted = null;

        await using var server = new IpcServer
        {
            PipeName = pipe,
            HandlerFaulted = (method, ex) => faulted = (method, ex),
        };

        server.Start(request => request.Method == "boom"
            ? throw new InvalidOperationException("the handler is broken")
            : Task.FromResult(new IpcResponse(request.Id, Ok: true, Data: "fine")));

        await using IpcClient client = await ConnectAsync(pipe);

        IpcResponse refused = await client.SendAsync("boom");

        Assert.False(refused.Ok);
        Assert.Contains("boom", refused.Error, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", refused.Error, StringComparison.Ordinal);
        Assert.Contains("the handler is broken", refused.Error, StringComparison.Ordinal);

        Assert.NotNull(faulted);
        Assert.Equal("boom", faulted.Value.Method);
        Assert.IsType<InvalidOperationException>(faulted.Value.Error);

        // The connection is still good.
        IpcResponse next = await client.SendAsync("ping");
        Assert.True(next.Ok);
        Assert.Equal("fine", next.Data);
    }

    [Fact]
    public async Task AMalformedRequestIsAnsweredNotIgnored()
    {
        string pipe = IsolatedPipe();

        await using var server = new IpcServer { PipeName = pipe };
        server.Start(request => Task.FromResult(new IpcResponse(request.Id, Ok: true)));

        // Below the client API on purpose: the client only ever writes well-formed JSON.
        using var raw = new System.IO.Pipes.NamedPipeClientStream(
            ".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await raw.ConnectAsync((int)Timeout.TotalMilliseconds);

        using var writer = new StreamWriter(raw, new System.Text.UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(raw, System.Text.Encoding.UTF8, leaveOpen: true);

        await writer.WriteLineAsync("this is not json");
        string? answer = await reader.ReadLineAsync().WaitAsync(Timeout);

        Assert.NotNull(answer);
        Assert.Contains("malformed request", answer, StringComparison.Ordinal);

        // The literal null deserialises to no request at all, which used to get no
        // reply - and the client then waited out its whole timeout.
        await writer.WriteLineAsync("null");
        string? nothing = await reader.ReadLineAsync().WaitAsync(Timeout);

        Assert.NotNull(nothing);
        Assert.Contains("malformed request", nothing, StringComparison.Ordinal);

        // And the connection still answers a proper request afterwards.
        await writer.WriteLineAsync("""{"method":"ping","payload":null,"id":7}""");
        string? pong = await reader.ReadLineAsync().WaitAsync(Timeout);

        Assert.NotNull(pong);
        Assert.Contains("\"ok\":true", pong, StringComparison.Ordinal);
    }
}
