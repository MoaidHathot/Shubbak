using System.Runtime.CompilerServices;

namespace Shubbak.Spike;

/// <summary>
/// Single-producer / single-consumer lock-free ring buffer of unmanaged structs.
/// The whole point of S1: the hook callback must be able to hand work off without
/// allocating, locking, or touching anything the GC can move.
/// Backing store is allocated once, up front.
/// </summary>
internal sealed class RingBuffer<T> where T : unmanaged
{
    private readonly T[] _buffer;
    private readonly int _mask;

    // Padded to avoid false sharing between producer and consumer cache lines.
    private PaddedLong _write;
    private PaddedLong _read;

    private long _dropped;

    public RingBuffer(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two.", nameof(capacityPowerOfTwo));

        _buffer = new T[capacityPowerOfTwo];
        _mask = capacityPowerOfTwo - 1;
    }

    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>Producer side. Allocation-free, wait-free. Called from the hook callback.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        long w = _write.Value;
        long r = Volatile.Read(ref _read.Value);

        if (w - r >= _buffer.Length)
        {
            _dropped++;
            return false;
        }

        _buffer[(int)(w & _mask)] = item;
        Volatile.Write(ref _write.Value, w + 1);
        return true;
    }

    /// <summary>Consumer side. Runs on the worker thread, may allocate freely.</summary>
    public bool TryDequeue(out T item)
    {
        long r = _read.Value;
        if (r >= Volatile.Read(ref _write.Value))
        {
            item = default;
            return false;
        }

        item = _buffer[(int)(r & _mask)];
        Volatile.Write(ref _read.Value, r + 1);
        return true;
    }

    [InlineArray(8)]
    private struct Padding { private long _e0; }

    // Fields exist purely to occupy cache lines either side of Value; they are
    // intentionally never read.
#pragma warning disable CS0169
    private struct PaddedLong
    {
        private Padding _before;
        public long Value;
        private Padding _after;
    }
#pragma warning restore CS0169
}
