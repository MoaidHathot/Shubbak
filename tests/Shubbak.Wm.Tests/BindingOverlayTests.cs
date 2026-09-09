using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Native;
using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// A context's bindings laid over the default table.
/// </summary>
/// <remarks>
/// <para>
/// The keyboard hook does one dictionary lookup per key-down and must go on doing
/// exactly one. So the overlay is merged into the default table when it is set, not
/// consulted as a second table when a key arrives; these tests read through the same
/// two methods the hook and the drain call, and nothing else.
/// </para>
/// <para>
/// A binding with no commands is how a context disarms a key: the chord is claimed, so
/// the application never sees it, and nothing runs.
/// </para>
/// </remarks>
public sealed class BindingOverlayTests
{
    private const ushort VkH = 0x48;
    private const ushort VkQ = 0x51;
    private const ushort VkP = 0x50;

    private static Keybinding Bind(KeyModifiers modifiers, ushort key, params WmCommand[] commands) =>
        new(new KeyBinding((int)modifiers, key, "test"), commands, default);

    private static BindingTable Loaded()
    {
        var table = new BindingTable();

        table.Load(new ShubbakConfig
        {
            Keybindings =
            [
                Bind(KeyModifiers.Alt, VkH, new FocusDirectionCommand(Core.Geometry.Direction.Left)),
                Bind(KeyModifiers.Alt | KeyModifiers.Shift, VkQ, new CloseWindowCommand()),
            ],
            BindingModes =
            [
                new BindingMode("resize",
                    [Bind(KeyModifiers.Alt, VkP, new DisableBindingModeCommand())],
                    PassThrough: false),
            ],
        });

        return table;
    }

    [Fact]
    public void WithNoOverlayTheTableIsExactlyWhatTheConfigSaid()
    {
        BindingTable table = Loaded();

        Assert.True(table.IsBound(VkH, KeyModifiers.Alt, isKeyDown: true));
        Assert.True(table.IsBound(VkQ, KeyModifiers.Alt | KeyModifiers.Shift, isKeyDown: true));
        Assert.False(table.IsBound(VkP, KeyModifiers.Alt, isKeyDown: true));
        Assert.Equal(0, table.OverlayCount);
    }

    [Fact]
    public void AnOverlayAddsAKeyTheDefaultsDidNotHave()
    {
        BindingTable table = Loaded();

        table.SetOverlay([Bind(KeyModifiers.Alt, VkP, new RedrawCommand())]);

        Assert.True(table.IsBound(VkP, KeyModifiers.Alt, isKeyDown: true));
        Assert.IsType<RedrawCommand>(Assert.Single(table.Resolve(VkP, KeyModifiers.Alt)!.Commands));

        // And everything the defaults had is still there.
        Assert.True(table.IsBound(VkH, KeyModifiers.Alt, isKeyDown: true));
    }

    [Fact]
    public void AnOverlayWinsOverADefaultOnTheSameKey()
    {
        BindingTable table = Loaded();

        table.SetOverlay([Bind(KeyModifiers.Alt, VkH, new FocusDirectionCommand(Core.Geometry.Direction.Right))]);

        var focus = Assert.IsType<FocusDirectionCommand>(Assert.Single(table.Resolve(VkH, KeyModifiers.Alt)!.Commands));
        Assert.Equal(Core.Geometry.Direction.Right, focus.Direction);
    }

    [Fact]
    public void AnEmptyOverlayBindingDisarmsTheKey()
    {
        // The presenting case: alt+shift+q closes a window, and on stage that key must
        // do nothing - not close the slides, and not reach the slides either.
        BindingTable table = Loaded();

        table.SetOverlay([Bind(KeyModifiers.Alt | KeyModifiers.Shift, VkQ)]);

        // Still claimed by the hook, so the application never sees it.
        Assert.True(table.IsBound(VkQ, KeyModifiers.Alt | KeyModifiers.Shift, isKeyDown: true));

        // Resolves to a binding that runs nothing.
        Keybinding? disarmed = table.Resolve(VkQ, KeyModifiers.Alt | KeyModifiers.Shift);
        Assert.NotNull(disarmed);
        Assert.Empty(disarmed.Commands);
    }

    [Fact]
    public void LaterOverlayEntriesWinOnTheSameKey()
    {
        BindingTable table = Loaded();

        table.SetOverlay(
        [
            Bind(KeyModifiers.Alt, VkP, new RedrawCommand()),
            Bind(KeyModifiers.Alt, VkP, new ReloadConfigCommand()),
        ]);

        Assert.IsType<ReloadConfigCommand>(Assert.Single(table.Resolve(VkP, KeyModifiers.Alt)!.Commands));
    }

    [Fact]
    public void TakingTheOverlayOffRestoresTheDefaults()
    {
        BindingTable table = Loaded();

        table.SetOverlay(
        [
            Bind(KeyModifiers.Alt, VkH, new FocusDirectionCommand(Core.Geometry.Direction.Right)),
            Bind(KeyModifiers.Alt, VkP, new RedrawCommand()),
        ]);

        table.SetOverlay([]);

        Assert.Equal(0, table.OverlayCount);
        Assert.False(table.IsBound(VkP, KeyModifiers.Alt, isKeyDown: true));

        var focus = Assert.IsType<FocusDirectionCommand>(Assert.Single(table.Resolve(VkH, KeyModifiers.Alt)!.Commands));
        Assert.Equal(Core.Geometry.Direction.Left, focus.Direction);
    }

    [Fact]
    public void TheOverlaySurvivesAReload()
    {
        // A reload is not a request to leave the context you are in, and the daemon
        // re-applies the effective config afterwards anyway - but between the two the
        // hook must not see a table without the overlay.
        BindingTable table = Loaded();
        table.SetOverlay([Bind(KeyModifiers.Alt, VkP, new RedrawCommand())]);

        table.Load(new ShubbakConfig
        {
            Keybindings = [Bind(KeyModifiers.Alt, VkH, new FocusDirectionCommand(Core.Geometry.Direction.Left))],
        });

        Assert.True(table.IsBound(VkP, KeyModifiers.Alt, isKeyDown: true));
        Assert.Equal(1, table.OverlayCount);
    }

    [Fact]
    public void TheOverlayDoesNotReachInsideABindingMode()
    {
        // Inside a mode the mode's table is the whole keyboard, as it always was. A
        // non-pass-through mode swallows the overlay's key like any other.
        BindingTable table = Loaded();
        table.SetOverlay([Bind(KeyModifiers.Alt, VkQ, new RedrawCommand())]);

        Assert.True(table.SetMode("resize"));

        // Swallowed by the mode, not resolved to the overlay.
        Assert.True(table.IsBound(VkQ, KeyModifiers.Alt, isKeyDown: true));
        Assert.Null(table.Resolve(VkQ, KeyModifiers.Alt));

        // Leaving the mode brings the overlay back into force with no further call.
        Assert.True(table.SetMode(null));
        Assert.IsType<RedrawCommand>(Assert.Single(table.Resolve(VkQ, KeyModifiers.Alt)!.Commands));
    }

    [Fact]
    public void TheDefaultsThemselvesAreNotMutated()
    {
        // Merged into a new dictionary, not written into the one the config produced,
        // so taking the overlay off is a swap back rather than an undo.
        BindingTable table = Loaded();

        table.SetOverlay([Bind(KeyModifiers.Alt, VkH, new RedrawCommand())]);
        table.SetOverlay([]);

        Assert.IsType<FocusDirectionCommand>(Assert.Single(table.Resolve(VkH, KeyModifiers.Alt)!.Commands));
    }
}
