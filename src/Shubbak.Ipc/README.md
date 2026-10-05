# Shubbak.Ipc

The protocol and client for talking to [Shubbak](https://github.com/MoaidHathot/Shubbak),
a tiling window manager for Windows, over its named pipe. The bar, the command
palette and the watcher that ship with Shubbak are clients of this same pipe; so can
anything you write. No dependency on anything else.

```csharp
using Shubbak.Ipc;

await using var client = new IpcClient();
await client.ConnectAsync(TimeSpan.FromSeconds(2));

// Ask, and tell. Anything a keybinding can run, a request can.
IpcResponse state = await client.SendAsync("query", "state");
await client.SendAsync("command", "focus --workspace 2");

// Listen. The same connection keeps answering requests while events stream.
await client.BeginSubscriptionAsync("signal,config.reloaded,wm.shutdown");

await client.SendAsync("command", "context --set meeting --lease");   // held while connected

await foreach (IpcEvent raised in client.ReadEventsAsync())
{
    if (raised.Topic == IpcProtocol.SignalTopic && SignalPayload.Parse(raised.Data) is { } signal)
        Console.WriteLine($"{signal.Name}: {string.Join(' ', signal.Arguments)}");
}
```

## What is here

- `IpcClient` — connect, `SendAsync(method, payload)`, `BeginSubscriptionAsync(topics)`,
  `ReadEventsAsync()`. Safe to call from several threads; one request on the wire at
  a time. A subscribed connection still answers requests.
- `IpcProtocol` — the pipe's name for this account, the thirty event topics, the
  limits, and the protocol version.
- The payload shapes, as records with source-generated JSON: `StateSnapshot`,
  `WindowInfo`, `WorkspaceInfo`, `MonitorInfoDto`, `ContextReport`, `RuleChange`,
  `WindowIcon`, `ConfigFileInfo` and the rest — `IpcJsonContext.Default.X` is how to
  read each.
- The three hand-read notices both ends share: `SignalPayload` (a `signal` event),
  `ConfigReloadNotice` (`config.reloaded`), `ShutdownNotice` (`wm.shutdown`).
- `IpcServer` — the window manager's side, so a test can stand in for one.

## The wire

Newline-delimited JSON over `\\.\pipe\shubbak-v2-<SID>`. A request is
`{"method":"query","payload":"state","id":1}`; the reply is
`{"id":1,"ok":true,"data":"..."}` or `{"id":1,"ok":false,"error":"..."}`; an event is
`{"topic":"window.focused","data":"..."}`. `payload` and `data` are strings, and
usually strings containing JSON. The full account — every method, every topic, the
security gates, signals and contexts — is in
[docs/scripting.md](https://github.com/MoaidHathot/Shubbak/blob/main/docs/scripting.md)
and
[docs/extending.md](https://github.com/MoaidHathot/Shubbak/blob/main/docs/extending.md).

## Versions

The package carries the product's version: a package and a window manager with the
same number were built from the same tree. Compatibility is a separate number, the
protocol's, and it is in the pipe name — `IpcProtocol.ProtocolVersion`, 2 today — so a
client and a daemon that disagree about the wire fail to find each other rather than
misread each other. Fields are only ever appended, optional, so a package older than
the window manager reads everything it knows about and ignores the rest.
