using System.Text.Json;
using Shubbak.Ipc;

// Hello, world: ask, listen, and be told, on one connection, with the Shubbak.Ipc
// package. The three things every program on the pipe does; docs/extending.md is the
// page that goes with this, and examples/hello/ has each of them as a script.
//
// Needs one line in the config to be told anything: bind "alt+h" { signal "hello" }.
// `shubbak signal hello` from a terminal raises the same signal.

if (!IpcClient.IsServerRunning())
{
    Console.Error.WriteLine("no window manager is running");
    return 2;
}

await using var client = new IpcClient();
await client.ConnectAsync(TimeSpan.FromSeconds(2));

// 1. Ask. `data` is a string that contains JSON; the records in the package read it.
IpcResponse answer = await client.SendAsync("query", "focused");
WindowInfo? focused = answer is { Ok: true, Data: { } json } && json != "null"
    ? JsonSerializer.Deserialize(json, IpcJsonContext.Default.WindowInfo)
    : null;

Console.WriteLine(focused is null
    ? "Hello, world. Nothing Shubbak manages has the focus."
    : $"Hello, world. The focused window is \"{focused.Title}\" ({focused.ProcessName}, {focused.State}).");

// 2. Listen, and 3. be told, on the same connection: the focus as it moves, and any
//    `signal "hello"` - from the key, the bar or a terminal. Ctrl+C to stop.
await client.BeginSubscriptionAsync("window.focused," + IpcProtocol.SignalTopic);
Console.WriteLine("Following the focus and waiting for `signal hello`; Ctrl+C to stop.");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

try
{
    await foreach (IpcEvent raised in client.ReadEventsAsync(stop.Token))
    {
        if (raised.Topic == IpcProtocol.SignalTopic)
        {
            // Every signal arrives here; yours is the one with your name on it.
            if (SignalPayload.Parse(raised.Data) is { } signal && signal.IsFor("hello"))
                Console.WriteLine(signal.Arguments.Count == 0 ? "  hello, world" : $"  hello, world: {string.Join(' ', signal.Arguments)}");
        }
        else
        {
            WindowInfo? window = raised.Data != "null" ? JsonSerializer.Deserialize(raised.Data, IpcJsonContext.Default.WindowInfo) : null;
            Console.WriteLine(window is null ? "  (focus left the managed windows)" : $"  {window.ProcessName,-16} {window.Title}");
        }
    }
}
catch (OperationCanceledException)
{
    // Ctrl+C. Closing the connection is all that leaving takes.
}

return 0;
