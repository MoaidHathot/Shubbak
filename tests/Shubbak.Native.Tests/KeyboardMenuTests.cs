using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shubbak.Native.Tests;

[Collection(SharedKeyboardHook.Name)]
[Trait("Requires", Desktop.RequiresForeground)]
public sealed class KeyboardMenuTests : IDisposable
{
    private readonly nint _original = Win32Window.GetForeground();
    private static readonly KeyboardBinding Bound = new(repeat: false);

    [Theory]
    [InlineData(0x5B, false)]
    [InlineData(0x5B, true)]
    [InlineData(0x5C, false)]
    [InlineData(0x5C, true)]
    public void WinShortcutsKeepTheReceiverInFront(int modifier, bool modifierFirst)
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the Win receiver"));
        nint receiver = Win32Window.GetForeground();
        using var source = new KeyboardSource();
        source.Start((key, _) => key == 0x87 ? Bound : null);
        try
        {
            Keyboard.Press((VIRTUAL_KEY)modifier);
            Keyboard.Press(VIRTUAL_KEY.VK_F24);
            if (modifierFirst) Keyboard.Release((VIRTUAL_KEY)modifier);
            Keyboard.Release(VIRTUAL_KEY.VK_F24);
            if (!modifierFirst) Keyboard.Release((VIRTUAL_KEY)modifier);
            Flush(window);
            Assert.Equal(receiver, Win32Window.GetForeground());
            Assert.Equal(1, source.MenuMasks);
            Assert.Equal(0, source.MenuMaskFailures);
        }
        finally
        {
            Keyboard.Release(VIRTUAL_KEY.VK_F24);
            Keyboard.Release((VIRTUAL_KEY)modifier);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneMaskCoversAHeldGestureIncludingBlockingModes(bool blocked)
    {
        using var window = new TestWindow(observeKeyMenus: true);
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the menu receiver"));
        using var source = new KeyboardSource();
        KeyboardBinding binding = blocked ? KeyboardBinding.Suppress : Bound;
        source.Start((key, _) => key is 0x87 or 0x85 ? binding : null);
        try
        {
            Keyboard.Press(VIRTUAL_KEY.VK_LMENU);
            for (int i = 0; i < 10; i++) Keyboard.Press(VIRTUAL_KEY.VK_F24);
            Keyboard.Release(VIRTUAL_KEY.VK_F24);
            Keyboard.Press(VIRTUAL_KEY.VK_F22);
            Keyboard.Release(VIRTUAL_KEY.VK_F22);
            Keyboard.Release(VIRTUAL_KEY.VK_LMENU);
            Flush(window);
            Assert.Equal(0, window.KeyMenus);
            Assert.Equal(1, source.MenuMasks);
            Assert.Equal(0, source.MenuMaskFailures);
        }
        finally
        {
            Keyboard.Release(VIRTUAL_KEY.VK_F24);
            Keyboard.Release(VIRTUAL_KEY.VK_F22);
            Keyboard.Release(VIRTUAL_KEY.VK_LMENU);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AltShortcutsDoNotActivateMenusButAnAltTapStillDoes(bool shortcut, bool modifierFirst)
    {
        using var window = new TestWindow(observeKeyMenus: true);
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the menu receiver"));
        using var source = new KeyboardSource();
        source.Start((key, _) => key == 0x87 ? Bound : null);
        try
        {
            Keyboard.Press(VIRTUAL_KEY.VK_LMENU);
            if (shortcut) Keyboard.Press(VIRTUAL_KEY.VK_F24);
            if (modifierFirst) Keyboard.Release(VIRTUAL_KEY.VK_LMENU);
            if (shortcut) Keyboard.Release(VIRTUAL_KEY.VK_F24);
            if (!modifierFirst) Keyboard.Release(VIRTUAL_KEY.VK_LMENU);
            Flush(window);
            Assert.Equal(shortcut ? 0 : 1, window.KeyMenus);
        }
        finally
        {
            Keyboard.Release(VIRTUAL_KEY.VK_F24);
            Keyboard.Release(VIRTUAL_KEY.VK_LMENU);
        }
    }

    private static void Flush(TestWindow window)
    {
        int next = window.KeyboardBarriers + 1;
        Keyboard.Press(VIRTUAL_KEY.VK_F23);
        Keyboard.Release(VIRTUAL_KEY.VK_F23);
        TestWindow.PumpUntil(() => window.KeyboardBarriers >= next);
        Assert.True(window.KeyboardBarriers == next, Desktop.WhyNotInFront("the menu receiver"));
    }

    public void Dispose()
    {
        if (_original != 0 && Win32Window.Exists(_original)) WindowActions.Focus(_original);
    }
}
