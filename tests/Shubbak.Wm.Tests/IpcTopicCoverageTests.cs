using System.Runtime.CompilerServices;
using Shubbak.Core.Wm;
using Shubbak.Ipc;

namespace Shubbak.Wm.Tests;

public sealed class IpcTopicCoverageTests
{
    [Fact]
    public void EveryPublishedTopicIsDeclared()
    {
        // Derive this from the actual event types: a hand-kept list once let
        // wm.suspended be published but refused by the subscription validator.
        // This cross-layer check belongs with the current daemon; the standalone
        // IPC suite also runs on the package's older supported runtime.
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
        foreach (string topic in published) Assert.Contains(topic, IpcProtocol.Topics);
    }
}
