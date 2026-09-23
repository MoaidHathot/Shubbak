using System.IO.Pipes;
using Shubbak.Ipc;

namespace Shubbak.Ipc.Tests;

/// <summary>
/// Disposing a client does not throw, whatever state the connection is in.
/// </summary>
/// <remarks>
/// <para>
/// It did, in two ways, and every caller had grown a different catch around it. A
/// client whose last request had failed because the window manager left threw
/// <see cref="IOException"/>: disposing the writer flushes, a flush asks the pipe how it
/// is, and a write to a closed pipe had marked it broken. A client disposed while its
/// last write was still being accounted for threw <see cref="InvalidOperationException"/>
/// - the bytes were in the pipe and the server had already acted on them, but the task
/// that wrote them had not run its last continuation, and the writer refuses a second
/// operation until it has.
/// </para>
/// <para>
/// The second is what failed <c>IpcSubscriberGateTests.OnlyTheTopicsAskedForCount</c>
/// on a loaded ARM64 runner, about one run in ten: that test disposes the moment the
/// server has registered the subscription, which under load is before the client's own
/// write has settled. Dalil caught the same exception and read it as "the subscription
/// was refused", with a thirty-second back-off and advice to restart the window
/// manager.
/// </para>
/// <para>
/// Each test here builds the state by construction rather than by timing, so that it
/// fails every time on the old code and not one time in ten.
/// </para>
/// </remarks>
public sealed class IpcClientDisposalTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string IsolatedPipe() => $"shubbak-test-{Guid.NewGuid():N}";

    [Fact]
    public async Task DisposingWhileTheLastWriteIsStillOnTheWireDoesNotThrow()
    {
        // A server that accepts and never reads, with a pipe buffer smaller than the
        // request. The write cannot complete until somebody reads, so it is in flight
        // when the client is disposed - by construction, not by racing the server.
        string pipe = IsolatedPipe();

        using var server = new NamedPipeServerStream(
            pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096, outBufferSize: 4096);

        Task accepted = server.WaitForConnectionAsync();

        var client = new IpcClient { PipeName = pipe };
        await client.ConnectAsync(Timeout);
        await accepted.WaitAsync(Timeout);

        Task<IpcResponse> inFlight = client.SendAsync("query", new string('x', 64 * 1024));

        Assert.False(inFlight.IsCompleted, "the request finished with nobody reading it, so this test is not testing what it thinks");

        // The assertion. On the old code: "The stream is currently in use by a previous
        // operation on the stream."
        await client.DisposeAsync();

        // The request fails where it was awaited, which is the right place for it, and
        // it fails rather than hanging.
        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => inFlight.WaitAsync(Timeout));

        Assert.True(
            ex is IOException or ObjectDisposedException or OperationCanceledException,
            $"the abandoned request failed with {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public async Task DisposingAfterARequestFailedBecauseTheServerLeftDoesNotThrow()
    {
        // What the command line, and Taj's command connection, meet when the window
        // manager exits under them: a request fails, and then the client is disposed.
        //
        // It is the failed request that matters, not the server leaving. A read that
        // meets the closed end of a pipe reports the end of the stream and nothing
        // else; a write to it fails at once, and that failure is what marks the pipe
        // broken. So a client whose subscription simply ended was never in the state
        // under test here, and one whose request failed always was.
        string pipe = IsolatedPipe();

        var server = new IpcServer { PipeName = pipe };
        server.Start(request => Task.FromResult(new IpcResponse(request.Id, Ok: true)));

        var client = new IpcClient { PipeName = pipe };
        await client.ConnectAsync(Timeout);

        Assert.True((await client.SendAsync("ping")).Ok, "the connection did not work before the server left");

        await server.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => client.SendAsync("ping"));

        // The assertion. On the old code: "Pipe is broken."
        await client.DisposeAsync();
    }
}
