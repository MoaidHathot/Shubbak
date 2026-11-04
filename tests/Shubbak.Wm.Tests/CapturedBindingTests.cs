using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Native;

namespace Shubbak.Wm.Tests;

public sealed class CapturedBindingTests
{
    [Fact]
    public void ResubmittingAMutableOverlayAppliesItsNewContents()
    {
        var table = new BindingTable();
        ShubbakConfig config = ConfigLoader.Load("keybindings { bind \"alt+x\" { close; }; }").Config;
        table.Load(config);
        Keybinding close = Assert.Single(config.Keybindings);
        List<Keybinding> overlay = [close with { Commands = [] }];
        table.SetOverlay(overlay);
        Assert.Empty(table.Resolve(0x58, KeyModifiers.Alt)!.Commands);
        overlay.Clear();
        table.SetOverlay(overlay);
        Assert.IsType<CloseWindowCommand>(Assert.Single(table.Resolve(0x58, KeyModifiers.Alt)!.Commands));
    }

    [Fact]
    public void OverlayCompilationCannotReadLaterMutationsOfTheLoadedDefaults()
    {
        var table = new BindingTable();
        Keybinding original = Assert.Single(ConfigLoader.Load("keybindings { bind \"alt+x\" { close; }; }").Config.Keybindings);
        List<Keybinding> defaults = [original];
        table.Load(new ShubbakConfig { Keybindings = defaults });
        defaults.Clear();
        Keybinding overlay = original with { Key = original.Key with { VirtualKey = 0x59 } };
        table.SetOverlay([overlay]);
        Assert.Same(original, table.Resolve(0x58, KeyModifiers.Alt));
    }

    [Fact]
    public void AReloadKeepsThePublishedOverlayRatherThanAnUnsubmittedMutation()
    {
        var table = new BindingTable();
        ShubbakConfig config = ConfigLoader.Load("keybindings { bind \"alt+x\" { close; }; }").Config;
        table.Load(config);
        List<Keybinding> overlay = [config.Keybindings[0] with { Commands = [] }];
        table.SetOverlay(overlay);
        overlay.Clear();
        table.Load(config);
        Assert.Empty(table.Resolve(0x58, KeyModifiers.Alt)!.Commands);
    }

    [Fact]
    public void QueuedCommandsAndTheirReportedModeSurviveTableReplacement()
    {
        var table = new BindingTable();
        table.Load(ConfigLoader.Load("""
            binding-modes {
                mode "original" pass-through=#true { bind "ctrl+x" { signal "before"; }; }
            }
            """).Config);
        table.SetMode("original");
        var captured = Assert.IsType<BoundShortcut>(table.Capture(0x58, KeyModifiers.Control));
        var queued = new KeyEvent(0x58, KeyModifiers.Control, true, false, false, captured);

        table.Load(ConfigLoader.Load("""
            keybindings { bind "ctrl+x" { signal "after"; }; }
            """).Config);
        var latest = Assert.IsType<BoundShortcut>(table.Capture(0x58, KeyModifiers.Control));
        Assert.Equal("after", Assert.IsType<SignalCommand>(Assert.Single(latest.Binding.Commands)).Signal);
        var dispatched = Assert.IsType<BoundShortcut>(queued.Binding);
        Assert.Equal("before", Assert.IsType<SignalCommand>(Assert.Single(dispatched.Binding.Commands)).Signal);
        Assert.Equal("original", dispatched.Mode);
    }

    [Fact]
    public void AnOverlayCannotTurnAnAlreadyClaimedNoOpIntoClose()
    {
        var table = new BindingTable();
        ShubbakConfig config = ConfigLoader.Load("keybindings { bind \"alt+x\" { close; }; }").Config;
        table.Load(config);
        Keybinding close = Assert.Single(config.Keybindings);
        table.SetOverlay([close with { Commands = [] }]);
        var captured = Assert.IsType<BoundShortcut>(table.Capture(0x58, KeyModifiers.Alt));
        table.SetOverlay([]);
        Assert.Empty(captured.Binding.Commands);
        Assert.IsType<CloseWindowCommand>(Assert.Single(table.Resolve(0x58, KeyModifiers.Alt)!.Commands));
    }
}
