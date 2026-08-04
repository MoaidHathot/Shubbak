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
    /// <remarks>
    /// Enumerates the pipe namespace rather than calling <c>File.Exists</c> on the
    /// pipe path. <c>File.Exists</c> on <c>\\.\pipe\name</c> is unreliable: it
    /// reports false while the server is between accepting one client and creating
    /// the next listening instance, which makes back-to-back CLI invocations fail
    /// intermittently.
    /// </remarks>
    public static bool IsServerRunning()
    {
        try
        {
            foreach (string pipe in Directory.EnumerateFiles(@"\\.\pipe\"))
            {
                if (string.Equals(
                        Path.GetFileName(pipe), IpcProtocol.PipeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to the cruder check rather than reporting "not running"
            // for what is really an enumeration failure.
            return File.Exists($@"\\.\pipe\{IpcProtocol.PipeName}");
        }

        return false;
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
    /// <summary>Sends one request and waits for its reply.</summary>
    /// <remarks>
    /// Bounded. The server can decline to reply at all - a payload that deserialises
    /// to JSON null takes that path - and every caller passed no token, so a single
    /// wedged request left the caller waiting for the life of the process rather than
    /// failing with something it could report.
    /// </remarks>
    public async Task<IpcResponse> SendAsync(
        string method, string? payload = null, CancellationToken token = default)
    {
        if (_writer is null || _reader is null)
            throw new InvalidOperationException("Not connected.");

        using var timeout = new CancellationTokenSource(ResponseTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        var request = new IpcRequest(method, payload, _nextId++);

        try
        {
            await _writer.WriteLineAsync(
                JsonSerializer.Serialize(request, IpcJsonContext.Default.IpcRequest).AsMemory(), linked.Token)
                .ConfigureAwait(false);

            while (true)
            {
                string? line = await _reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The window manager did not answer '{method}' within {ResponseTimeout.TotalSeconds:F0} s.");
        }
    }

    /// <summary>
    /// How long to wait for a reply.
    /// </summary>
    /// <remarks>
    /// Generous, because a request is answered on the daemon thread and that thread
    /// may legitimately be busy adopting windows at startup. Long enough never to fire
    /// in normal use, short enough that a wedged daemon is reported rather than waited
    /// on forever.
    /// </remarks>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Subscribes and yields events until cancelled.</summary>
    /// <param name="topics">Comma-separated topics, or null for everything.</param>
    /// <param name="token">Stops the stream.</param>
    public async IAsyncEnumerable<IpcEvent> SubscribeAsync(
        string? topics,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        if (_reader is null) throw new InvalidOperationException("Not connected.");

        // Checked, because the server can refuse - an unknown topic can never fire, so
        // accepting the refusal quietly leaves the caller waiting for something that
        // was never going to arrive.
        IpcResponse response = await SendAsync("subscribe", topics ?? "*", token).ConfigureAwait(false);

        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "the subscription was refused.");

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
