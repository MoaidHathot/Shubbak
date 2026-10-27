using System.Buffers;
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
    private const int RetainedLimit = 16 * 1024;
    private ArrayBufferWriter<byte> _buffer = new(512);
    internal int RetainedCapacity => _buffer.Capacity;

    internal async ValueTask WriteAsync<T>(T value, JsonTypeInfo<T> shape, CancellationToken token = default)
    {
        // A reply may follow buffered events. Never discard or interleave them.
        if (_buffer.WrittenCount != 0) await FlushAsync(token).ConfigureAwait(false);
        using (var json = new Utf8JsonWriter(_buffer)) JsonSerializer.Serialize(json, value, shape);
        Terminate();
        await FlushAsync(token).ConfigureAwait(false);
    }

    internal async ValueTask WriteLineAsync(string json, CancellationToken token = default)
    {
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (_buffer.WrittenCount != 0 && bytes + _buffer.WrittenCount >= RetainedLimit)
            await FlushAsync(token).ConfigureAwait(false);
        int written = Encoding.UTF8.GetBytes(json, _buffer.GetSpan(bytes));
        _buffer.Advance(written);
        Terminate();
        if (_buffer.WrittenCount >= RetainedLimit) await FlushAsync(token).ConfigureAwait(false);
    }

    private void Terminate()
    {
        _buffer.GetSpan(1)[0] = (byte)'\n';
        _buffer.Advance(1);
    }

    internal async ValueTask FlushAsync(CancellationToken token = default)
    {
        if (_buffer.WrittenCount == 0) return;
        await stream.WriteAsync(_buffer.WrittenMemory, token).ConfigureAwait(false);
        // An exceptional large config/report must not make every idle connection
        // retain a large array. Typical request/event batches reuse their small buffer.
        if (_buffer.Capacity > RetainedLimit) _buffer = new ArrayBufferWriter<byte>(512);
        else _buffer.Clear();
    }
}
