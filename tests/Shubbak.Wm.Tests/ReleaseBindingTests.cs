using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Native;

namespace Shubbak.Wm.Tests;

/// <summary>
/// A binding that runs when the key comes up.
/// </summary>
/// <remarks>
/// <para>
/// <c>release=#true</c> is the shape of a push-to-talk, or of anything that should
/// happen when a held key is let go. The press is still claimed - the key must not
/// reach an application on the way down and then run a command on the way up - and the
/// hook delivers the release only when the table says a release binding is there, so
/// every other binding costs nothing more than it did.
/// </para>
/// <para>
/// The hook itself is exercised in the native tests; this is the table's half.
/// </para>
/// </remarks>
public sealed class ReleaseBindingTests
{
    private const ushort VkT = 0x54;
    private const ushort VkA = 0x41;

    private static BindingTable Table()
    {
        var table = new BindingTable();

        table.Load(new ShubbakConfig
        {
            Keybindings =
            [
                new Keybinding(
                    new KeyBinding((int)KeyModifiers.Alt, VkT, "alt+t"),
                    [new SignalCommand("talk", [])],
                    default,
                    Release: true),
                new Keybinding(
                    new KeyBinding((int)KeyModifiers.Alt, VkA, "alt+a"),
                    [new FocusDirectionCommand(Core.Geometry.Direction.Left)],
                    default),
            ],
        });

        return table;
    }

    [Fact]
    public void TheReleaseOfAReleaseBindingIsClaimed()
    {
        BindingTable table = Table();

        Assert.True(table.IsBound(VkT, KeyModifiers.Alt, isKeyDown: true), "the press is swallowed");
        Assert.True(table.IsBound(VkT, KeyModifiers.Alt, isKeyDown: false), "the release is delivered");
    }

    [Fact]
    public void TheReleaseOfAnOrdinaryBindingIsNot()
    {
        BindingTable table = Table();

        Assert.True(table.IsBound(VkA, KeyModifiers.Alt, isKeyDown: true));
        Assert.False(table.IsBound(VkA, KeyModifiers.Alt, isKeyDown: false));
    }

    [Fact]
    public void AReleaseWithTheModifierAlreadyGoneIsNobodys()
    {
        BindingTable table = Table();

        Assert.False(table.IsBound(VkT, KeyModifiers.None, isKeyDown: false));
    }

    [Fact]
    public void AReleaseBindingNeverRepeats()
    {
        var binding = new Keybinding(
            new KeyBinding((int)KeyModifiers.Alt, VkT, "alt+t"),
            [new FocusDirectionCommand(Core.Geometry.Direction.Left)],
            default,
            Repeat: true,
            Release: true);

        Assert.False(binding.RepeatsOnHold);
    }

    [Fact]
    public void TheLoaderReadsReleaseAndStillRefusesAnythingElse()
    {
        ShubbakConfig config = ConfigLoader.Load("""
            keybindings {
                bind "alt+t" release=#true { signal talk }
                bind "alt+a" { focus --direction left }
            }
            """).Config;

        Keybinding talk = Assert.Single(config.Keybindings, b => b.Key.Display == "alt+t");
        Keybinding focus = Assert.Single(config.Keybindings, b => b.Key.Display == "alt+a");

        Assert.True(talk.Release);
        Assert.False(focus.Release);

        ConfigLoadResult bad = ConfigLoader.Load("""
            keybindings {
                bind "alt+t" release="yes" { signal talk }
            }
            """);

        Assert.Contains(bad.Diagnostics, d => d.Code == "SHB0432" && d.Message.Contains("release", StringComparison.Ordinal));
    }
}
