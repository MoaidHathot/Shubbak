using System.Text;
using System.Text.Json;

namespace Shubbak.Ipc.Tests;

public sealed class Utf8LineWriterTests
{
    [Fact]
    public async Task RepeatedEscapedFramesDoNotAllocateAfterWarmup()
    {
        using var stream = new MemoryStream();
        var writer = new Utf8LineWriter(stream);
        var response = new IpcResponse(1, true, new string('"', 4096));
        for (int i = 0; i < 20; i++)
        {
            stream.SetLength(0);
            await writer.WriteAsync(response, IpcJsonContext.Default.IpcResponse);
        }
        long length = stream.Length;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            stream.SetLength(0);
            await writer.WriteAsync(response, IpcJsonContext.Default.IpcResponse);
            if (stream.Length != length) throw new InvalidOperationException("The frame was not written completely.");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(response, JsonSerializer.Deserialize(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)), IpcJsonContext.Default.IpcResponse));
    }

    [Fact]
    public async Task ABufferIsNotReturnedWhileTheStreamStillUsesIt()
    {
        using var stream = new HeldWriteStream();
        var writer = new Utf8LineWriter(stream);
        var response = new IpcResponse(7, true, new string('"', 4096));
        Task writing = writer.WriteAsync(response, IpcJsonContext.Default.IpcResponse).AsTask();
        try
        {
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(writing.IsCompleted);
            foreach (int length in new[] { 16 * 1024, 32 * 1024, 64 * 1024, 128 * 1024 })
            {
                byte[] other = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
                other.AsSpan().Fill(0xCC);
                System.Buffers.ArrayPool<byte>.Shared.Return(other);
            }
        }
        finally { stream.Release.TrySetResult(); }
        await writing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(response, JsonSerializer.Deserialize(stream.Bytes, IpcJsonContext.Default.IpcResponse));
    }

    [Fact]
    public async Task ReusedFramesPreserveEscapedUnicodeAndRequestIdentity()
    {
        using var stream = new MemoryStream();
        var writer = new Utf8LineWriter(stream);
        var first = new IpcRequest("query", "نافذة🙂 \"quoted\"\nsecond line", 7);
        var second = new IpcRequest("ping", null, 8);
        await writer.WriteAsync(first, IpcJsonContext.Default.IpcRequest);
        await writer.WriteAsync(second, IpcJsonContext.Default.IpcRequest);
        string[] lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal(first, JsonSerializer.Deserialize(lines[0], IpcJsonContext.Default.IpcRequest));
        Assert.Equal(second, JsonSerializer.Deserialize(lines[1], IpcJsonContext.Default.IpcRequest));
        Assert.Empty(lines[2]);
    }

    [Fact]
    public async Task ALargeReportDoesNotRemainPinnedByAnIdleConnection()
    {
        using var stream = new MemoryStream();
        var writer = new Utf8LineWriter(stream);
        var large = new IpcResponse(1, true, new string('x', 200_000));
        await writer.WriteAsync(large, IpcJsonContext.Default.IpcResponse);
        Assert.InRange(writer.RetainedCapacity, 1, 16 * 1024);
        await writer.WriteAsync(new IpcResponse(2, true, "small"), IpcJsonContext.Default.IpcResponse);
        string[] lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
        Assert.Equal(large, JsonSerializer.Deserialize(lines[0], IpcJsonContext.Default.IpcResponse));
        Assert.Equal("small", JsonSerializer.Deserialize(lines[1], IpcJsonContext.Default.IpcResponse)!.Data);
    }

    [Fact]
    public async Task AReplyAfterBufferedEventsPreservesWireOrder()
    {
        using var stream = new MemoryStream();
        var writer = new Utf8LineWriter(stream);
        await writer.WriteLineAsync("{\"topic\":\"first\"}");
        await writer.WriteLineAsync("{\"topic\":\"second\"}");
        await writer.WriteAsync(new IpcResponse(1, true), IpcJsonContext.Default.IpcResponse);
        string[] lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
        Assert.Equal("{\"topic\":\"first\"}", lines[0]);
        Assert.Equal("{\"topic\":\"second\"}", lines[1]);
        Assert.True(JsonSerializer.Deserialize(lines[2], IpcJsonContext.Default.IpcResponse)!.Ok);
        Assert.Equal(4, lines.Length);
    }

    private sealed class HeldWriteStream : Stream
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[] Bytes { get; private set; } = [];
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Bytes = buffer.ToArray();
        }
    }
}
