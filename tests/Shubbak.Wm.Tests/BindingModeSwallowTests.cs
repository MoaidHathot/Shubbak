using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Native;
using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// Which keystrokes a binding mode claims, and whether it can always be left.
/// </summary>
/// <remarks>
/// <para>
/// A mode that does not pass through swallows every key, which is the entire point of
/// a pause mode. It also makes this the most dangerous code in the program: get it
/// wrong and the keyboard is inert with no way back, on a machine whose window manager
/// is the thing that has stopped listening.
/// </para>
/// <para>
/// That is not hypothetical. Entering pause left the keyboard dead and the binding
/// that leaves the mode did nothing, because the mode swallowed the modifier keys its
/// own escape depended on.
/// </para>
/// </remarks>
public sealed class BindingModeSwallowTests
{
    private const ushort VkP = 0x50;
    private const ushort VkA = 0x41;

    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkAlt = 0x12;
    private const ushort VkLeftAlt = 0xA4;
    private const ushort VkRightAlt = 0xA5;
    private const ushort VkLeftShift = 0xA0;
    private const ushort VkLeftWin = 0x5B;

    private static BindingTable Paused()
    {
        var table = new BindingTable();

        table.Load(new ShubbakConfig
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
        });

        Assert.True(table.SetMode("pause"));

        return table;
    }

    [Theory]
    [InlineData(VkAlt)]
    [InlineData(VkLeftAlt)]
    [InlineData(VkRightAlt)]
    [InlineData(VkShift)]
    [InlineData(VkLeftShift)]
    [InlineData(VkControl)]
    [InlineData(VkLeftWin)]
    public void AModifierIsNeverSwallowed(ushort modifierKey)
    {
        // Swallowing a modifier stops it reaching the input state the hook consults,
        // so the very next keystroke reports no modifiers held - and a mode whose only
        // way out is alt+shift+p can then never match it.
        BindingTable table = Paused();

        Assert.False(table.IsBound(modifierKey, KeyModifiers.None, isKeyDown: true));
    }

    [Fact]
    public void TheEscapeStillResolvesWithItsModifiers()
    {
        BindingTable table = Paused();

        Assert.True(table.IsBound(VkP, KeyModifiers.Alt | KeyModifiers.Shift, isKeyDown: true));

        Keybinding? binding = table.Resolve(VkP, KeyModifiers.Alt | KeyModifiers.Shift);

        Assert.NotNull(binding);
        Assert.Contains(binding!.Commands, c => c is DisableBindingModeCommand);
    }

    [Fact]
    public void EverythingElseIsStillSwallowed()
    {
        // The mode has to remain useful: an ordinary key must not reach the
        // application underneath.
        BindingTable table = Paused();

        Assert.True(table.IsBound(VkA, KeyModifiers.None, isKeyDown: true));
        Assert.Null(table.Resolve(VkA, KeyModifiers.None));
    }

    [Fact]
    public void APassThroughModeClaimsOnlyItsOwnBindings()
    {
        var table = new BindingTable();

        table.Load(new ShubbakConfig
        {
            BindingModes =
            [
                new BindingMode(
                    "resize",
                    [new Keybinding(new KeyBinding(0, VkP, "p"), [new EqualiseCommand()], default)],
                    PassThrough: true),
            ],
        });

        table.SetMode("resize");

        Assert.True(table.IsBound(VkP, KeyModifiers.None, isKeyDown: true));
        Assert.False(table.IsBound(VkA, KeyModifiers.None, isKeyDown: true));
    }

    [Fact]
    public void LeavingTheModeRestoresTheDefaultBindings()
    {
        var table = new BindingTable();

        table.Load(new ShubbakConfig
        {
            Keybindings =
            [
                new Keybinding(
                    new KeyBinding((int)KeyModifiers.Alt, VkA, "alt+a"),
                    [new EqualiseCommand()],
                    default),
            ],
            BindingModes = [new BindingMode("pause", [], PassThrough: false)],
        });

        table.SetMode("pause");
        Assert.Null(table.Resolve(VkA, KeyModifiers.Alt));

        table.SetMode(null);
        Assert.NotNull(table.Resolve(VkA, KeyModifiers.Alt));
    }

    [Fact]
    public void AKeyUpIsNeverClaimed()
    {
        // The hook decides key-up from what it swallowed on the way down, not from
        // asking again - by then the modifiers may already have been released.
        BindingTable table = Paused();

        Assert.False(table.IsBound(VkP, KeyModifiers.Alt | KeyModifiers.Shift, isKeyDown: false));
        Assert.False(table.IsBound(VkA, KeyModifiers.None, isKeyDown: false));
    }
}
