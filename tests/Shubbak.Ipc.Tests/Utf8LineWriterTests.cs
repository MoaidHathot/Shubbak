using System.Text;
using System.Text.Json;

namespace Shubbak.Ipc.Tests;

public sealed class Utf8LineWriterTests
{
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
}
