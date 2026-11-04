using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Native;

namespace Shubbak.Wm.Tests;

public sealed class SidedBindingTests
{
    [Theory]
    [InlineData("ctrl", "lctrl", "rctrl", KeyModifiers.Control, KeyModifiers.LeftControl, KeyModifiers.RightControl)]
    [InlineData("alt", "lalt", "ralt", KeyModifiers.Alt, KeyModifiers.LeftAlt, KeyModifiers.RightAlt)]
    [InlineData("shift", "lshift", "rshift", KeyModifiers.Shift, KeyModifiers.LeftShift, KeyModifiers.RightShift)]
    [InlineData("win", "lwin", "rwin", KeyModifiers.Windows, KeyModifiers.LeftWindows, KeyModifiers.RightWindows)]
    public void SpecificSidesWinAndTheGenericChordRemainsAFallback(
        string generic, string leftName, string rightName, KeyModifiers basic, KeyModifiers left, KeyModifiers right)
    {
        var table = new BindingTable();
        table.Load(ConfigLoader.Load($$"""
            keybindings {
                bind "{{generic}}+h" { signal "generic" }
                bind "{{leftName}}+h" { signal "left" }
            }
            """).Config);
        Assert.Equal("left", Signal(table, basic | left));
        Assert.Equal("generic", Signal(table, basic | right));
        Assert.Equal("left", Signal(table, basic | left | right));
        Assert.Null(table.Resolve(0x48, KeyModifiers.None));
        Assert.Null(table.Resolve(0x48, basic | left | KeyModifiers.AltGraph));

        table.Load(ConfigLoader.Load($$"""
            keybindings {
                bind "{{leftName}}+h" { signal "left" }
                bind "{{rightName}}+h" { signal "right" }
                bind "{{leftName}}+{{rightName}}+h" { signal "both" }
            }
            """).Config);
        Assert.Equal("left", Signal(table, basic | left));
        Assert.Equal("right", Signal(table, basic | right));
        Assert.Equal("both", Signal(table, basic | left | right));
    }

    [Fact]
    public void ExpansionCoversEveryCombinationOfFourModifierFamilies()
    {
        var table = new BindingTable();
        table.Load(ConfigLoader.Load("""
            keybindings {
                bind "ctrl+alt+shift+win+h" { signal "generic" }
                bind "lctrl+lalt+lshift+lwin+j" { signal "sided" }
            }
            """).Config);
        KeyModifiers[][] choices =
        [
            [KeyModifiers.LeftAlt, KeyModifiers.RightAlt, KeyModifiers.LeftAlt | KeyModifiers.RightAlt],
            [KeyModifiers.LeftControl, KeyModifiers.RightControl, KeyModifiers.LeftControl | KeyModifiers.RightControl],
            [KeyModifiers.LeftShift, KeyModifiers.RightShift, KeyModifiers.LeftShift | KeyModifiers.RightShift],
            [KeyModifiers.LeftWindows, KeyModifiers.RightWindows, KeyModifiers.LeftWindows | KeyModifiers.RightWindows],
        ];
        foreach (KeyModifiers a in choices[0])
        foreach (KeyModifiers c in choices[1])
        foreach (KeyModifiers s in choices[2])
        foreach (KeyModifiers w in choices[3])
            Assert.Equal("generic", Signal(table, (KeyModifiers)15 | a | c | s | w));
    }

    [Fact]
    public void AGenericContextDisarmsSidedBindingsAndRemovalRestoresThem()
    {
        var table = new BindingTable();
        table.Load(ConfigLoader.Load("keybindings { bind \"lctrl+h\" { close; }; }").Config);
        Keybinding overlay = Assert.Single(ConfigLoader.Load("keybindings { bind \"ctrl+h\" { signal \"overlay\"; }; }").Config.Keybindings);
        table.SetOverlay([overlay with { Commands = [] }]);
        Assert.Empty(table.Resolve(0x48, KeyModifiers.Control | KeyModifiers.LeftControl)!.Commands);
        Assert.Empty(table.Resolve(0x48, KeyModifiers.Control | KeyModifiers.RightControl)!.Commands);
        table.SetOverlay([]);
        Assert.IsType<CloseWindowCommand>(Assert.Single(table.Resolve(0x48, KeyModifiers.Control | KeyModifiers.LeftControl)!.Commands));
        Assert.Null(table.Resolve(0x48, KeyModifiers.Control | KeyModifiers.RightControl));
    }

    [Fact]
    public void AltGrRequiresAnExplicitBindingAndCaptureAllocatesNothing()
    {
        var table = new BindingTable();
        table.Load(ConfigLoader.Load("keybindings { bind \"altgr+q\" { signal \"explicit\"; }; }").Config);
        Assert.Null(table.Capture(0x51, KeyModifiers.Control | KeyModifiers.Alt));
        Assert.NotNull(table.Capture(0x51, KeyModifiers.AltGraph));
        for (int i = 0; i < 1000; i++) _ = table.Capture(0x51, KeyModifiers.AltGraph);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) _ = table.Capture(0x51, KeyModifiers.AltGraph);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static string Signal(BindingTable table, KeyModifiers modifiers) =>
        Assert.IsType<SignalCommand>(Assert.Single(table.Resolve(0x48, modifiers)!.Commands)).Signal;
}
