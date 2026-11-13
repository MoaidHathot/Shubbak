using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Shubbak.Ipc.Tests;

public sealed class IpcCancellationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CancellingASubscribedWriteCannotReuseAPartialFrame()
    {
        string pipe = $"shubbak-cancel-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        using var budget = new CancellationTokenSource(Budget);
        Task accepted = server.WaitForConnectionAsync(budget.Token);
        await using var client = new IpcClient { PipeName = pipe };
        await client.ConnectAsync(Budget, budget.Token);
        await accepted.WaitAsync(budget.Token);
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        Task subscribing = client.BeginSubscriptionAsync("*", budget.Token);
        string line = (await reader.ReadLineAsync(budget.Token))!;
        IpcRequest request = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcRequest)!;
        string reply = JsonSerializer.Serialize(new IpcResponse(request.Id, true), IpcJsonContext.Default.IpcResponse);
        await writer.WriteLineAsync(reply.AsMemory(), budget.Token);
        await subscribing.WaitAsync(budget.Token);

        // The server stops reading after the handshake. This write is larger than
        // the pipe buffer, so cancellation abandons real in-flight I/O.
        using var cancel = new CancellationTokenSource();
        Task<IpcResponse> abandoned = client.SendAsync("query", new string('x', 128 * 1024), cancel.Token);
        Assert.False(abandoned.IsCompleted);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Budget));

        Task<IpcResponse> retry = client.SendAsync("query", "next", budget.Token);
        try
        {
            // Prompt failure, rather than replaying the old buffer or waiting for the
            // normal response timeout on a stream with an unknown frame boundary.
            await Assert.ThrowsAsync<IOException>(() => retry.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await client.DisposeAsync();
            try { await retry.WaitAsync(Budget); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingAQueuedRequestDoesNotPoisonTheConnection(bool subscribed)
    {
        string pipe = $"shubbak-cancel-{Guid.NewGuid():N}";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var budget = new CancellationTokenSource(Budget);
        await using var server = new IpcServer { PipeName = pipe };
        server.Start(async request =>
        {
            if (request.Payload == "hold")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(budget.Token);
            }
            return new IpcResponse(request.Id, true, request.Payload);
        });
        await using var client = new IpcClient { PipeName = pipe };
        await client.ConnectAsync(Budget, budget.Token);
        if (subscribed) await client.BeginSubscriptionAsync("*", budget.Token);
        Task<IpcResponse> held = client.SendAsync("query", "hold", budget.Token);
        try
        {
            await entered.Task.WaitAsync(budget.Token);
            using var cancel = new CancellationTokenSource();
            Task<IpcResponse> queued = client.SendAsync("query", "cancelled", cancel.Token);
            Assert.False(queued.IsCompleted);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(budget.Token));
        }
        finally { release.TrySetResult(); }
        Assert.Equal("hold", (await held.WaitAsync(budget.Token)).Data);
        Assert.Equal("next", (await client.SendAsync("query", "next", budget.Token)).Data);
    }

    [Fact]
    public async Task CancellingOnlyTheReplyWaitKeepsASubscribedConnectionUsable()
    {
        string pipe = $"shubbak-cancel-{Guid.NewGuid():N}";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var budget = new CancellationTokenSource(Budget);
        await using var server = new IpcServer { PipeName = pipe };
        server.Start(async request =>
        {
            if (request.Payload == "hold")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(budget.Token);
            }
            return new IpcResponse(request.Id, true, request.Payload);
        });
        await using var client = new IpcClient { PipeName = pipe };
        await client.ConnectAsync(Budget, budget.Token);
        await client.BeginSubscriptionAsync("*", budget.Token);
        using var cancel = new CancellationTokenSource();
        Task<IpcResponse> held = client.SendAsync("query", "hold", cancel.Token);
        try
        {
            await entered.Task.WaitAsync(budget.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held.WaitAsync(budget.Token));
        }
        finally { release.TrySetResult(); }
        Assert.Equal("next", (await client.SendAsync("query", "next", budget.Token)).Data);
    }
}
