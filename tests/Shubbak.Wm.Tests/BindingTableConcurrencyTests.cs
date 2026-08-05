using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Native;
using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// What the keyboard hook sees while the table is being rebuilt underneath it.
/// </summary>
/// <remarks>
/// <para>
/// <c>IsBound</c> runs on the hook thread; <c>Load</c> runs on the daemon thread when
/// the config file changes. The table used to hold four separate fields and a reload
/// wrote them one after another, clearing the active mode and then re-selecting it
/// against the new tables.
/// </para>
/// <para>
/// Between those two writes the active mode was null, so the hook resolved against the
/// defaults. In a non-pass-through mode - a <c>pause</c> mode, whose entire purpose is
/// to make the keyboard inert - that meant keystrokes briefly stopped being swallowed
/// and reached the focused application instead.
/// </para>
/// <para>
/// The window is a few instructions wide, which is why it is provoked here rather than
/// waited for: one thread reloading in a loop while another probes in a loop.
/// </para>
/// </remarks>
public sealed class BindingTableConcurrencyTests
{
    private const ushort VkP = 0x50;

    /// <summary>An unbound key, so only a swallowing mode can claim it.</summary>
    private const ushort VkZ = 0x5A;

    private static ShubbakConfig PausedConfig() => new()
    {
        BindingModes =
        [
            new BindingMode(
                "pause",
                [new Keybinding(
                    new KeyBinding((int)(KeyModifiers.Alt | KeyModifiers.Shift), VkP, "alt+shift+p"),
                    [new DisableBindingModeCommand()],
                    default)],
                PassThrough: false),
        ],
    };

    [Fact]
    public void AReloadNeverLetsAKeystrokeEscapeASwallowingMode()
    {
        var table = new BindingTable();

        table.Load(PausedConfig());
        Assert.True(table.SetMode("pause"));

        // The mode swallows everything, so an unbound key must stay claimed for the
        // whole run. Seeing false means the hook was looking at the default table.
        Assert.True(table.IsBound(VkZ, KeyModifiers.None, isKeyDown: true));

        var escaped = false;
        var stop = false;

        var probe = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (!table.IsBound(VkZ, KeyModifiers.None, isKeyDown: true))
                {
                    Volatile.Write(ref escaped, true);
                    return;
                }
            }
        })
        { IsBackground = true };

        probe.Start();

        try
        {
            for (int i = 0; i < 2000; i++) table.Load(PausedConfig());
        }
        finally
        {
            Volatile.Write(ref stop, true);
            probe.Join(TimeSpan.FromSeconds(5));
        }

        Assert.False(
            Volatile.Read(ref escaped),
            "a keystroke escaped the swallowing mode while the table was reloading");
    }

    [Fact]
    public void AReloadKeepsTheActiveMode()
    {
        // The single-threaded half of the same property, and the one that says the
        // snapshot is published with the mode already re-selected rather than cleared
        // and restored.
        var table = new BindingTable();

        table.Load(PausedConfig());
        Assert.True(table.SetMode("pause"));

        Assert.Null(table.Load(PausedConfig()));
        Assert.True(table.IsBound(VkZ, KeyModifiers.None, isKeyDown: true));
    }

    [Fact]
    public void AReloadThatDropsTheModeReportsIt()
    {
        // The other outcome: the mode is gone from the new config, so the keyboard
        // falls back to the defaults and the caller is told which name was lost.
        var table = new BindingTable();

        table.Load(PausedConfig());
        Assert.True(table.SetMode("pause"));

        Assert.Equal("pause", table.Load(new ShubbakConfig()));
        Assert.False(table.IsBound(VkZ, KeyModifiers.None, isKeyDown: true));
    }
}
