using System.Runtime.CompilerServices;
using Shubbak.Core.Wm;
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
        //
        // Derived from the event types rather than written out. It was written out,
        // and that is precisely how wm.suspended came to be published by the daemon
        // and rejected by the subscription check at the same time: a new event was
        // added, the hand-kept list was not, and the test that exists to catch exactly
        // this passed because it was reading the same hand-kept list.
        //
        // Reflection is the only way to ask "what subtypes exist"; C# has no closed
        // hierarchy and so no compile-time exhaustiveness over one. The trim analyser
        // would object, and is off for test projects - which are never trimmed, never
        // published and never AOT compiled.
        string[] published =
        [
            .. typeof(WmEvent).Assembly
                .GetTypes()
                .Where(t => t.IsSealed && !t.IsAbstract && t.IsSubclassOf(typeof(WmEvent)))
                .Select(t => RuntimeHelpers.GetUninitializedObject(t) as WmEvent)
                .Where(e => e is not null)
                .Select(e => e!.Topic)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(topic => topic, StringComparer.Ordinal),
        ];

        Assert.NotEmpty(published);

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
    public void TheShutdownTopicIsDeclaredToo()
    {
        // A bar has to be able to subscribe to the one that says the window manager
        // is going, or it sits there attached to nothing.
        Assert.Contains(IpcProtocol.ShutdownTopic, IpcProtocol.Topics);
    }

    [Fact]
    public void TheTwoLifecycleTopicsAreNamespacedApartFromTheStateOnes()
    {
        // wm.* is about the window manager itself; everything else is about what it
        // manages. A client can subscribe to one without the other.
        Assert.StartsWith("wm.", IpcProtocol.ResyncTopic, StringComparison.Ordinal);
        Assert.StartsWith("wm.", IpcProtocol.ShutdownTopic, StringComparison.Ordinal);
        Assert.NotEqual(IpcProtocol.ResyncTopic, IpcProtocol.ShutdownTopic);
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
