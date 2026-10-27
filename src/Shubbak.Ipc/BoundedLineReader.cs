using System.Buffers;
using System.Text;

namespace Shubbak.Ipc;

/// <summary>Buffered UTF-8 framing with a byte limit and no per-character async calls.</summary>
internal sealed class BoundedLineReader(Stream stream, int maximumBytes) : IDisposable
{
    private readonly byte[] _buffer = new byte[4096];
    private int _position, _end, _length;
    private byte[]? _fragment;
    private bool _first = true, _ended;

    internal async ValueTask<string?> ReadLineAsync(CancellationToken token = default)
    {
        if (_ended) return null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (_position == _end)
            {
                _end = await stream.ReadAsync(_buffer, token).ConfigureAwait(false);
                _position = 0;
                if (_end == 0)
                {
                    _ended = true;
                    return _length == 0 ? null : Finish(ReadOnlySpan<byte>.Empty);
                }
            }

            int newline = _buffer.AsSpan(_position, _end - _position).IndexOf((byte)'\n');
            int count = newline < 0 ? _end - _position : newline;
            // Room for the optional stream BOM and CRLF, which are framing bytes.
            if (_length + count > maximumBytes + 4)
            {
                _ended = true;
                ReleaseFragment();
                return null;
            }
            ReadOnlySpan<byte> part = _buffer.AsSpan(_position, count);
            _position += count + (newline < 0 ? 0 : 1);
            if (newline >= 0) return Finish(part);
            Append(part);
        }
    }

    private string? Finish(ReadOnlySpan<byte> last)
    {
        try
        {
            ReadOnlySpan<byte> line = last;
            if (_length != 0)
            {
                Append(last);
                line = _fragment.AsSpan(0, _length);
            }
            if (_first && line.StartsWith("\uFEFF"u8)) line = line[3..];
            _first = false;
            if (!line.IsEmpty && line[^1] == '\r') line = line[..^1];
            if (line.Length > maximumBytes)
            {
                _ended = true;
                return null;
            }
            // Preserve the reader's existing CR treatment, including unusual embedded
            // CRs. Ordinary LF/CRLF messages take the single string allocation path.
            string text = Encoding.UTF8.GetString(line);
            return text.Replace("\r", string.Empty, StringComparison.Ordinal);
        }
        finally { ReleaseFragment(); }
    }

    private void Append(ReadOnlySpan<byte> part)
    {
        int needed = _length + part.Length;
        if (_fragment is null || _fragment.Length < needed)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, Math.Min(maximumBytes + 4, (_fragment?.Length ?? 4096) * 2)));
            if (_fragment is not null)
            {
                _fragment.AsSpan(0, _length).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(_fragment);
            }
            _fragment = larger;
        }
        part.CopyTo(_fragment.AsSpan(_length));
        _length = needed;
    }

    private void ReleaseFragment()
    {
        if (_fragment is { } fragment) ArrayPool<byte>.Shared.Return(fragment);
        _fragment = null;
        _length = 0;
    }

    // The owner cancels/joins its pending read before disposing. The pipe remains
    // the connection's, and large fragments are returned after each line, not held
    // for the lifetime of a connection that once sent a large request.
    public void Dispose() => ReleaseFragment();
}
