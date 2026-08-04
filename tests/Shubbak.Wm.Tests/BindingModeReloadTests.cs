using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Native;
using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// What happens to the active binding mode when the config is reloaded.
/// </summary>
/// <remarks>
/// <para>
/// Reloading used to drop the mode and tell nobody. The lookup table went back to
/// the default bindings, which is the safe half, while the state machine kept the
/// name - so after reloading inside a mode called <c>pause</c>, the keyboard was on
/// the default bindings while <c>diagnose</c>, the bar and the state machine all
/// still said <c>pause</c>.
/// </para>
/// <para>
/// It then got worse if the user tried the obvious remedy, because
/// <c>SetBindingMode</c> short-circuits on an unchanged name: pressing the key that
/// enables <c>pause</c> found it already active, emitted no event, and never reached
/// the table. The mode could not be entered again until <c>wm-disable-binding-mode</c>
/// was run - which appeared to do nothing, and was the thing that fixed it.
/// </para>
/// </remarks>
public sealed class BindingModeReloadTests
{
    private const ushort VkP = 0x50;
    private const ushort VkJ = 0x4A;

    private static Keybinding Binding(ushort key, string display) =>
        new(new KeyBinding((int)KeyModifiers.Alt, key, display), [new DisableBindingModeCommand()], default);

    private static ShubbakConfig WithModes(params string[] modeNames) => new()
    {
        Keybindings = [Binding(VkJ, "alt+j")],
        BindingModes =
        [
            .. modeNames.Select(name =>
                new BindingMode(name, [Binding(VkP, "alt+p")], PassThrough: false)),
        ],
    };

    [Fact]
    public void AModeThatStillExistsSurvivesTheReload()
    {
        // A reload is not a request to leave the mode you are in.
        var table = new BindingTable();
        _ = table.Load(WithModes("pause"));

        Assert.True(table.SetMode("pause"));

        string? lost = table.Load(WithModes("pause"));

        Assert.Null(lost);

        // Still swallowing, which is what being in a non-pass-through mode means.
        Assert.True(table.IsBound(VkJ, KeyModifiers.None, isKeyDown: true));
    }

    [Fact]
    public void AModeThatWasDeletedIsReportedRatherThanDroppedInSilence()
    {
        // The caller has to be told, because it owns the state machine and the two
        // must not disagree. Returning the name is how it finds out.
        var table = new BindingTable();
        _ = table.Load(WithModes("pause"));

        Assert.True(table.SetMode("pause"));

        string? lost = table.Load(WithModes("resize"));

        Assert.Equal("pause", lost);

        // And the table really is back on the defaults: alt+j is bound, an unbound
        // key is not, which is the ordinary behaviour rather than a swallowing mode.
        Assert.True(table.IsBound(VkJ, KeyModifiers.Alt, isKeyDown: true));
        Assert.False(table.IsBound(VkJ, KeyModifiers.None, isKeyDown: true));
    }

    [Fact]
    public void ReloadingOutsideAnyModeLosesNothing()
    {
        var table = new BindingTable();
        _ = table.Load(WithModes("pause"));

        Assert.Null(table.Load(WithModes("pause")));
        Assert.Null(table.Load(WithModes()));
    }

    [Fact]
    public void AnUndeclaredModeIsRefusedAndChangesNothing()
    {
        // wm-enable-binding-mode --name typo used to report success, log the mode as
        // active and leave the table on the defaults - three components, three beliefs.
        var table = new BindingTable();
        _ = table.Load(WithModes("pause"));

        Assert.False(table.SetMode("typo"));

        // Unchanged: still the default bindings, not a swallowing mode.
        Assert.False(table.IsBound(VkJ, KeyModifiers.None, isKeyDown: true));
        Assert.True(table.IsBound(VkJ, KeyModifiers.Alt, isKeyDown: true));
    }

    [Fact]
    public void ARefusedModeDoesNotDisturbTheOneAlreadyActive()
    {
        var table = new BindingTable();
        _ = table.Load(WithModes("pause"));

        Assert.True(table.SetMode("pause"));
        Assert.False(table.SetMode("typo"));

        // Still in pause, still swallowing.
        Assert.True(table.IsBound(VkJ, KeyModifiers.None, isKeyDown: true));

        // And it is pause that a reload carries across, not the name that was refused.
        Assert.Equal("pause", table.Load(WithModes("resize")));
    }

    [Fact]
    public void LeavingAModeIsAlwaysAccepted()
    {
        // The escape hatch has to work unconditionally, including when no mode is
        // active - it is what the daemon falls back to when anything else goes wrong.
        var table = new BindingTable();
        _ = table.Load(WithModes("pause"));

        Assert.True(table.SetMode(null));
        Assert.True(table.SetMode("pause"));
        Assert.True(table.SetMode(null));

        Assert.False(table.IsBound(VkJ, KeyModifiers.None, isKeyDown: true));
    }
}
