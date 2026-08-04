using Shubbak.Ipc;

namespace Shubbak.Wm.Tests;

/// <summary>
/// The parts of the wire protocol both ends have to agree on.
/// </summary>
/// <remarks>
/// The pipe carried no version, performed no handshake, and was named after the
/// account rather than identified by it. None of that matters until something
/// changes shape - and then it matters silently, because System.Text.Json ignores
/// members it does not know and leaves missing ones at their default, so an old bar
/// against a new window manager misreads the state instead of failing.
/// </remarks>
public sealed class IpcProtocolTests
{
    [Fact]
    public void ThePipeNameCarriesTheProtocolVersion()
    {
        // Turns a silent misreading into "no window manager is running", which is
        // wrong in a way anyone can act on.
        Assert.Contains($"-v{IpcProtocol.ProtocolVersion}-", IpcProtocol.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePipeNameIdentifiesTheAccountRatherThanNamingIt()
    {
        // Two accounts called alice in different domains share a name and nothing
        // else. Lower-casing a name also carries the Turkish-I problem.
        if (!OperatingSystem.IsWindows()) return;

        Assert.Contains("S-1-", IpcProtocol.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPublishedTopicIsDeclared()
    {
        // The list is what a subscription is checked against, so a topic the window
        // manager publishes but the list omits would be refused to its own clients.
        string[] published =
        [
            "window.managed", "window.unmanaged", "window.focused",
            "window.title_changed", "window.state_changed", "window.tags_changed",
            "window.moved", "workspace.activated", "workspace.created",
            "workspace.destroyed", "workspace.moved", "layout.changed",
            "container.resized", "monitor.added", "monitor.removed",
            "monitor.changed", "binding_mode.changed", "command.rejected",
            "config.reloaded",
        ];

        foreach (string topic in published)
            Assert.Contains(topic, IpcProtocol.Topics);
    }

    [Fact]
    public void TheResyncTopicIsDeclaredToo()
    {
        // A client has to be able to subscribe to the one that tells it to re-read.
        Assert.Contains(IpcProtocol.ResyncTopic, IpcProtocol.Topics);
    }

    [Fact]
    public void TheLimitsAreSane()
    {
        // Every one of these was unbounded, and they compound: unbounded clients
        // multiplied by unbounded subscriptions, each costing a lock on the daemon
        // thread for every event.
        Assert.True(IpcProtocol.MaxMessageBytes > 64 * 1024, "a window tree must still fit");
        Assert.True(IpcProtocol.MaxClients >= 8, "a bar per monitor plus tooling must fit");
        Assert.True(IpcProtocol.MaxSubscriptionsPerClient >= IpcProtocol.Topics.Count,
            "subscribing to everything by name must not hit the limit");
    }
}
