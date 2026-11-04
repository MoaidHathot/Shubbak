using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shubbak.Native.Tests;

[Collection(SharedKeyboardHook.Name)]
public sealed class KeyboardLayoutTests : IDisposable
{
    private readonly nint _original = Win32Window.GetForeground();

    [Fact]
    public unsafe void DetectingAltGrDoesNotConsumeAPendingDeadKey()
    {
        using var layout = new LoadedKeyboardLayout("00020409");
        var hkl = new HKL(layout.Handle);
        byte* state = stackalloc byte[256];
        new Span<byte>(state, 256).Clear();
        char* text = stackalloc char[8];
        uint scan = PInvoke.MapVirtualKeyEx(0xDE, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC, hkl);
        try
        {
            Assert.True(PInvoke.ToUnicodeEx(0xDE, scan, state, text, 8, 0, hkl) < 0);
            Assert.True(KeyboardLayouts.DetectAltGr(layout.Handle));
            scan = PInvoke.MapVirtualKeyEx(0x41, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC, hkl);
            Assert.Equal(1, PInvoke.ToUnicodeEx(0x41, scan, state, text, 8, 0, hkl));
            Assert.Equal('á', text[0]);
        }
        finally { _ = PInvoke.ToUnicodeEx(0x20, 0x39, state, text, 8, 0, hkl); }
    }

    [Theory]
    [Trait("Requires", Desktop.RequiresForeground)]
    [InlineData("00000407", 0x51, "@")]
    [InlineData("00000415", 0x43, "ć")]
    [InlineData("00000415", 0x4E, "ń")]
    public void ActualAltGrTextReachesTheReceiverDespiteCollidingBindings(string name, int key, string text)
    {
        using var layout = new LoadedKeyboardLayout(name);
        using var window = new TestWindow(observeText: true);
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the AltGr receiver"));
        nint previous = KeyboardLayouts.ForegroundLayout();
        window.Invoke(() => PInvoke.ActivateKeyboardLayout(new HKL(layout.Handle), 0));
        var binding = new KeyboardBinding();
        KeyModifiers observed = KeyModifiers.None;
        using var source = new KeyboardSource();
        source.Start((vk, mods) =>
        {
            if (vk != key) return null;
            observed = mods;
            return (mods & KeyModifiers.AltGraph) == 0 ? binding : null;
        });
        try
        {
            Keyboard.Press(VIRTUAL_KEY.VK_RMENU);
            Keyboard.Press((VIRTUAL_KEY)key);
            Keyboard.Release((VIRTUAL_KEY)key);
            Keyboard.Release(VIRTUAL_KEY.VK_RMENU);
            Keyboard.Press(VIRTUAL_KEY.VK_F23);
            Keyboard.Release(VIRTUAL_KEY.VK_F23);
            TestWindow.PumpUntil(() => window.KeyboardBarriers > 0);
            Assert.True((observed & KeyModifiers.AltGraph) != 0, $"Observed {observed}; layout 0x{KeyboardLayouts.ForegroundLayout():X}");
            Assert.Equal(0, source.Drain(new KeyEvent[8], 8));
            Assert.Equal(text, window.TypedText);
            Assert.Equal(0, source.MenuMasks);
        }
        finally
        {
            Keyboard.Release((VIRTUAL_KEY)key);
            Keyboard.Release(VIRTUAL_KEY.VK_RMENU);
            Keyboard.Release(VIRTUAL_KEY.VK_LCONTROL);
            window.Invoke(() => PInvoke.ActivateKeyboardLayout(new HKL(previous), 0));
        }
    }

    [Fact]
    [Trait("Requires", Desktop.RequiresForeground)]
    public void TheRealHookRecognizesLeftControlRightAltOnUs()
    {
        using var layout = new LoadedKeyboardLayout("00000409");
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the Ctrl+Alt receiver"));
        nint previous = KeyboardLayouts.ForegroundLayout();
        window.Invoke(() => PInvoke.ActivateKeyboardLayout(new HKL(layout.Handle), 0));
        var binding = new KeyboardBinding();
        using var source = new KeyboardSource();
        source.Start((vk, _) => vk == 0x87 ? binding : null);
        try
        {
            Keyboard.Press(VIRTUAL_KEY.VK_LCONTROL);
            Keyboard.Press(VIRTUAL_KEY.VK_RMENU);
            Keyboard.Press(VIRTUAL_KEY.VK_F24);
            Keyboard.Release(VIRTUAL_KEY.VK_F24);
            Keyboard.Release(VIRTUAL_KEY.VK_RMENU);
            Keyboard.Release(VIRTUAL_KEY.VK_LCONTROL);
            Keyboard.Press(VIRTUAL_KEY.VK_F23);
            Keyboard.Release(VIRTUAL_KEY.VK_F23);
            TestWindow.PumpUntil(() => window.KeyboardBarriers > 0);
            var events = new KeyEvent[8];
            Assert.Equal(1, source.Drain(events, 8));
            Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, events[0].Modifiers);
        }
        finally
        {
            Keyboard.Release(VIRTUAL_KEY.VK_F24);
            Keyboard.Release(VIRTUAL_KEY.VK_RMENU);
            Keyboard.Release(VIRTUAL_KEY.VK_LCONTROL);
            window.Invoke(() => PInvoke.ActivateKeyboardLayout(new HKL(previous), 0));
        }
    }

    [Theory]
    [InlineData("00000409", false)] // US
    [InlineData("00000407", true)]  // German: AltGr+Q
    [InlineData("00000415", true)]  // Polish programmers: AltGr+C/N
    [InlineData("00020409", true)]  // US International
    public void LayoutDetectionUsesRealWindowsTranslations(string name, bool expected)
    {
        using var layout = new LoadedKeyboardLayout(name);
        Assert.Equal(expected, KeyboardLayouts.DetectAltGr(layout.Handle));
        var metadata = new KeyboardLayouts();
        metadata.Refresh();
        Assert.Equal(expected, metadata.UsesAltGr(layout.Handle));
        Assert.False(metadata.RefreshPending);
    }

    [Fact]
    public void ANewLayoutIsRefreshedByTheConsumerWithoutHookSideTranslation()
    {
        var metadata = new KeyboardLayouts();
        using var layout = new LoadedKeyboardLayout("00000409");
        Assert.True(metadata.UsesAltGr(layout.Handle)); // Unknown is conservative.
        Assert.True(metadata.RefreshPending);
        metadata.Refresh();
        Assert.False(metadata.UsesAltGr(layout.Handle));
        Assert.False(metadata.RefreshPending);
    }

    public void Dispose()
    {
        if (_original != 0 && Win32Window.Exists(_original)) WindowActions.Focus(_original);
    }
}

internal sealed class LoadedKeyboardLayout : IDisposable
{
    public nint Handle { get; }
    private readonly bool _owned;
    private readonly UnloadKeyboardLayoutSafeHandle _loaded;

    public unsafe LoadedKeyboardLayout(string name)
    {
        int count = PInvoke.GetKeyboardLayoutList(0, null);
        var before = new HKL[count];
        fixed (HKL* pointer = before) PInvoke.GetKeyboardLayoutList(count, pointer);
        _loaded = PInvoke.LoadKeyboardLayout(name, 0);
        Assert.False(_loaded.IsInvalid);
        Handle = _loaded.DangerousGetHandle();
        _owned = !before.Contains(new HKL(Handle));
    }

    public void Dispose()
    {
        if (!_owned) _loaded.SetHandleAsInvalid();
        _loaded.Dispose();
    }
}
