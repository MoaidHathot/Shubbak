using Shubbak.Config;
using Shubbak.Native;

namespace Shubbak.Wm.Tests;

public sealed class AltGrBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AltGrTextDoesNotMatchPlainShiftedOrControlAltBindings(bool shift)
    {
        var table = new BindingTable();
        table.Load(ConfigLoader.Load("""
            keybindings {
                bind "q" { signal "plain" }
                bind "shift+q" { signal "shifted" }
                bind "ctrl+alt+q" { signal "control-alt" }
            }
            binding-modes {
                mode "letters" pass-through=#true {
                    bind "q" { signal "plain-mode" }
                    bind "shift+q" { signal "shifted-mode" }
                }
            }
            """).Config);

        KeyModifiers modifiers = KeyboardSource.DeriveModifiers(false, true, true, false, shift, false);
        Assert.False(table.IsBound(0x51, modifiers, true));
        Assert.Null(table.Resolve(0x51, modifiers));
        Assert.True(table.SetMode("letters"));
        Assert.False(table.IsBound(0x51, modifiers, true));
        Assert.Null(table.Resolve(0x51, modifiers));
    }

    [Fact]
    public void ABlockingModeMaySwallowAltGrButCannotRunItsPlainKeyBinding()
    {
        var table = new BindingTable();
        table.Load(ConfigLoader.Load("""
            binding-modes {
                mode "blocked" {
                    bind "q" { signal "wrong" }
                    bind "escape" { wm-disable-binding-mode }
                }
            }
            """).Config);
        table.SetMode("blocked");
        KeyModifiers modifiers = KeyboardSource.DeriveModifiers(false, true, true, false, false, false);
        Assert.True(table.IsBound(0x51, modifiers, true));
        Assert.Null(table.Resolve(0x51, modifiers));
    }
}
