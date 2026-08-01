using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Shubbak.Ipc;

/// <summary>
/// Named-pipe client, used by the CLI and by Taj.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId = 1;

    /// <summary>Whether a window manager is listening.</summary>
    public static bool IsServerRunning()
    {
        // The pipe appears in the filesystem namespace, so this needs no connection
        // attempt and therefore no timeout.
        return File.Exists($@"\\.\pipe\{IpcProtocol.PipeName}");
    }

    /// <summary>Connects, or throws if no window manager is running.</summary>
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken token = default)
    {
        _pipe = new NamedPipeClientStream(
            ".", IpcProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await _pipe.ConnectAsync((int)timeout.TotalMilliseconds, token).ConfigureAwait(false);

        _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>Sends a request and waits for its response.</summary>
    public async Task<IpcResponse> SendAsync(
        string method, string? payload = null, CancellationToken token = default)
    {
        if (_writer is null || _reader is null)
            throw new InvalidOperationException("Not connected.");

        var request = new IpcRequest(method, payload, _nextId++);

        await _writer.WriteLineAsync(
            JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest).AsMemory(), token)
            .ConfigureAwait(false);

        while (true)
        {
            string? line = await _reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) throw new IOException("The window manager closed the connection.");
            if (line.Length == 0) continue;

            // Events can interleave with responses on a subscribed connection, so
            // anything that is not our response is skipped.
            IpcResponse? response;
            try
            {
                response = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcResponse);
            }
            catch (JsonException)
            {
                continue;
            }

            if (response is not null && response.Id == request.Id) return response;
        }
    }

    /// <summary>Subscribes and yields events until cancelled.</summary>
    /// <param name="topics">Comma-separated topics, or null for everything.</param>
    /// <param name="token">Stops the stream.</param>
    public async IAsyncEnumerable<IpcEvent> SubscribeAsync(
        string? topics,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        if (_reader is null) throw new InvalidOperationException("Not connected.");

        await SendAsync("subscribe", topics ?? "*", token).ConfigureAwait(false);

        while (!token.IsCancellationRequested)
        {
            string? line = await _reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) yield break;
            if (line.Length == 0) continue;

            IpcEvent? notification = null;

            try
            {
                notification = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcEvent);
            }
            catch (JsonException)
            {
            }

            if (notification is { Topic.Length: > 0 }) yield return notification;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null) await _writer.DisposeAsync().ConfigureAwait(false);
        _reader?.Dispose();
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
