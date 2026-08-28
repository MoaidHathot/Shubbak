using Shubbak.Ipc;

namespace Shubbak.Wm.Tests;

/// <summary>
/// The names that scope Shubbak to one desktop.
/// </summary>
/// <remarks>
/// Two questions that look alike and are not. The pipe asks "can these two understand
/// each other", and carries a version so that two builds which cannot will fail to
/// connect. An instance mutex asks "is one of these already running", and the answer
/// does not depend on what they can say to one another - two bars reserve the same
/// strip of screen twice whether or not they speak the same protocol.
/// </remarks>
public class InstanceNameTests
{
    [Fact]
    public void ThePipeCarriesTheProtocolVersion()
    {
        // So a new CLI and an old daemon fail to find each other rather than
        // misunderstand each other.
        Assert.Contains($"-v{IpcProtocol.ProtocolVersion}-", IpcProtocol.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstanceMutexDoesNotCarryTheProtocolVersion()
    {
        // The regression this exists for. The window manager's mutex used to be the
        // pipe name with a prefix, so raising the protocol version silently opened the
        // hole the mutex exists to close: a daemon on the old version held a different
        // name from one on the new, and both could run.
        Assert.DoesNotContain(
            $"-v{IpcProtocol.ProtocolVersion}-",
            IpcProtocol.InstanceMutexName,
            StringComparison.Ordinal);

        foreach (string component in new[] { "wm", "taj", "dalil" })
        {
            Assert.DoesNotContain(
                $"-v{IpcProtocol.ProtocolVersion}-",
                IpcProtocol.InstanceMutexNameFor(component),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheWindowManagerUsesTheWmName()
    {
        Assert.Equal(IpcProtocol.InstanceMutexNameFor("wm"), IpcProtocol.InstanceMutexName);
    }

    [Fact]
    public void EachProgramGetsANameOfItsOwn()
    {
        // Otherwise starting the bar would block the palette, or worse, the window
        // manager.
        string[] names =
        [
            IpcProtocol.InstanceMutexNameFor("wm"),
            IpcProtocol.InstanceMutexNameFor("taj"),
            IpcProtocol.InstanceMutexNameFor("dalil"),
        ];

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryNameIsSessionLocalRatherThanMachineWide()
    {
        // Global would need privilege, could be squatted from another session, and
        // would mean two people signed in at once could not each run Shubbak.
        Assert.StartsWith(@"Local\", IpcProtocol.InstanceMutexName, StringComparison.Ordinal);

        foreach (string component in new[] { "wm", "taj", "dalil" })
            Assert.StartsWith(@"Local\", IpcProtocol.InstanceMutexNameFor(component), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNameIsScopedToTheAccount()
    {
        // One window manager per logged-in account, not one per machine. The pipe and
        // the mutexes have to agree about which account that is, or a daemon could hold
        // one account's mutex while serving another's pipe.
        string account = IpcProtocol.PipeName.Split('-', 3)[2];

        Assert.False(string.IsNullOrWhiteSpace(account));

        foreach (string component in new[] { "wm", "taj", "dalil" })
            Assert.EndsWith(account, IpcProtocol.InstanceMutexNameFor(component), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameQuestionAlwaysGetsTheSameName()
    {
        // Computed once and reused. A name that varied between calls would let a
        // process fail to see the claim it made itself.
        Assert.Equal(IpcProtocol.InstanceMutexNameFor("taj"), IpcProtocol.InstanceMutexNameFor("taj"));
        Assert.Equal(IpcProtocol.PipeName, IpcProtocol.PipeName);
    }
}
