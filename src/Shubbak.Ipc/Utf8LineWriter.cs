using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Shubbak.Ipc;

/// <summary>
/// Serializes a complete frame directly to UTF-8. One connection owns the writer;
/// its caller serializes access through the entire awaited write.
/// </summary>
internal sealed class Utf8LineWriter(Stream stream)
{
    private readonly FrameBufferWriter _buffer = new();
    private Utf8JsonWriter? _json;
    internal int RetainedCapacity => _buffer.RetainedCapacity;

    internal async ValueTask WriteAsync<T>(T value, JsonTypeInfo<T> shape, CancellationToken token = default)
    {
        // A reply may follow buffered events. Never discard or interleave them.
        if (_buffer.WrittenCount != 0) await FlushAsync(token).ConfigureAwait(false);
        _json ??= new Utf8JsonWriter(_buffer);
        try
        {
            JsonSerializer.Serialize(_json, value, shape);
            _json.Flush();
            // Drop the writer's borrowed memory before FlushAsync can return a
            // temporary frame buffer to the pool. Reuse its JSON state next time.
            _json.Reset(_buffer);
            Terminate();
            await FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _json.Reset(_buffer);
            _buffer.Reset();
        }
    }

    internal async ValueTask WriteLineAsync(string json, CancellationToken token = default)
    {
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (_buffer.WrittenCount != 0 && bytes + _buffer.WrittenCount >= FrameBufferWriter.RetainedLimit)
            await FlushAsync(token).ConfigureAwait(false);
        int written = Encoding.UTF8.GetBytes(json, _buffer.GetSpan(bytes));
        _buffer.Advance(written);
        Terminate();
        if (_buffer.WrittenCount >= FrameBufferWriter.RetainedLimit) await FlushAsync(token).ConfigureAwait(false);
    }

    private void Terminate()
    {
        _buffer.GetSpan(1)[0] = (byte)'\n';
        _buffer.Advance(1);
    }

    internal async ValueTask FlushAsync(CancellationToken token = default)
    {
        if (_buffer.WrittenCount == 0) return;
        try { await stream.WriteAsync(_buffer.WrittenMemory, token).ConfigureAwait(false); }
        finally
        {
            // The async write owns this memory until completion, including failure.
            // Only small storage remains on an idle connection; larger reports are
            // dropped, and moderate frames can be borrowed again without allocating.
            _buffer.Reset();
        }
    }
}
