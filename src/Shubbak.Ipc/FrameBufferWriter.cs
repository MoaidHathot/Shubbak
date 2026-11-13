using System.Buffers;

namespace Shubbak.Ipc;

/// <summary>Retains small frames and borrows moderately sized temporary buffers.</summary>
internal sealed class FrameBufferWriter : IBufferWriter<byte>
{
    internal const int RetainedLimit = 16 * 1024;
    // Utf8JsonWriter can reserve ~74 KiB for 4 KiB of quote characters: its
    // UTF-8 upper bound is larger than the eventual escaped frame. Bound the pool
    // separately from the 16 KiB retained on an idle connection.
    private const int PoolLimit = 128 * 1024;
    private byte[] _retained;
    private byte[] _buffer;
    private int _written;
    private bool _rented;

    internal FrameBufferWriter() => _retained = _buffer = new byte[512];

    internal int WrittenCount => _written;
    internal int RetainedCapacity => _retained.Length;
    internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length - _written);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void Ensure(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        sizeHint = Math.Max(sizeHint, 1);
        if (sizeHint <= _buffer.Length - _written) return;
        int required = checked(_written + sizeHint);
        int doubled = (int)Math.Min((long)_buffer.Length * 2, Array.MaxLength);
        int capacity = Math.Max(required, doubled);
        if (required <= RetainedLimit) capacity = Math.Min(capacity, RetainedLimit);

        bool rent = capacity is > RetainedLimit and <= PoolLimit;
        byte[] next = rent ? ArrayPool<byte>.Shared.Rent(capacity) : GC.AllocateUninitializedArray<byte>(capacity);
        _buffer.AsSpan(0, _written).CopyTo(next);
        if (_rented) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
        _rented = rent;
        if (capacity <= RetainedLimit) _retained = next;
    }

    /// <summary>Called only after the awaited write has relinquished the memory.</summary>
    internal void Reset()
    {
        _written = 0;
        if (_rented) ArrayPool<byte>.Shared.Return(_buffer);
        _rented = false;
        _buffer = _retained;
    }
}
