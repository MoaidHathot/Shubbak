using System.Text;

namespace Shubbak.Ipc.Tests;

public sealed class BoundedLineReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task FragmentedUtf8AndSeveralFramesPreserveEveryCharacter(int fragment)
    {
        string longLine = string.Concat(Enumerable.Repeat("نافذة🙂", 2000));
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes("\uFEFFone\r\n\n" + longLine + "\nlast"), fragment);
        using var reader = new BoundedLineReader(stream, 100_000);
        Assert.Equal("one", await reader.ReadLineAsync());
        Assert.Equal("", await reader.ReadLineAsync());
        Assert.Equal(longLine, await reader.ReadLineAsync());
        Assert.Equal("last", await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    [Theory]
    [InlineData("1234\n", "1234")]
    [InlineData("1234\r\n", "1234")]
    [InlineData("🙂\n", "🙂")]
    [InlineData("12345\n", null)]
    [InlineData("🙂x\n", null)]
    public async Task TheLimitCountsUtf8BytesNotCharacters(string input, string? expected)
    {
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(input), 1);
        using var reader = new BoundedLineReader(stream, 4);
        Assert.Equal(expected, await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task AnUnterminatedOversizedFrameEndsBeforeReadingTheWholeStream()
    {
        using var stream = new MemoryStream(new byte[100_000]);
        using var reader = new BoundedLineReader(stream, 8000);
        Assert.Null(await reader.ReadLineAsync());
        Assert.True(stream.Position <= 8192);
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task CancellationIsObservedEvenForAlreadyBufferedFrames()
    {
        using var stream = new MemoryStream("first\nsecond\n"u8.ToArray());
        using var reader = new BoundedLineReader(stream, 100);
        Assert.Equal("first", await reader.ReadLineAsync());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadLineAsync(stop.Token).AsTask());
    }

    private sealed class FragmentedStream(byte[] bytes, int fragment) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, fragment)], cancellationToken);
    }
}
