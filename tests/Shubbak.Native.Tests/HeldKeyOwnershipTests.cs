using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;
using Xunit.Abstractions;

namespace Shubbak.Native.Tests;

/// <summary>Observe what an application receives, not just what the command ring held.</summary>
[Collection(SharedKeyboardHook.Name)]
[Trait("Requires", Desktop.RequiresForeground)]
public sealed class HeldKeyOwnershipTests(ITestOutputHelper output) : IDisposable
{
    private const ushort Key = 0x87;
    private const ushort Barrier = 0x86;
    private const ushort Control = 0xA2;
    private static readonly bool[] PassedSequence = [true, true, false];
    private readonly nint _original = Win32Window.GetForeground();

    [Fact]
    public void ReleasingAModifierCannotLeakAClaimedKeysRepeatWithoutItsRelease()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        using var source = new KeyboardSource();
        source.Start((key, modifiers, down) => key == Key && down && (modifiers & KeyModifiers.Control) != 0);

        try
        {
            Send(Control, true);
            Send(Key, true);
            Flush(window);
            output.WriteLine($"async F24 state after swallowed press: {(PInvoke.GetAsyncKeyState(Key) & 0x8000) != 0}");
            Send(Control, false);
            Send(Key, true); // Repeat of the press Shubbak owns.
            Send(Key, false);
            Flush(window);

            Assert.Empty(window.KeyDowns);
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void PressingAModifierCannotStealAnApplicationsHeldKeyAndItsRelease()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        using var source = new KeyboardSource();
        source.Start((key, modifiers, down) => key == Key && down && (modifiers & KeyModifiers.Control) != 0);

        try
        {
            Send(Key, true);
            Flush(window);
            Send(Control, true);
            Send(Key, true); // The application, not Shubbak, owns this held key.
            Send(Control, false);
            Send(Key, false);
            Flush(window);

            Assert.Equal(PassedSequence, window.KeyDowns);
            Assert.Equal(0, source.Drain(new KeyEvent[8], 8));
        }
        finally { ReleaseAll(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangingBindingsMidHoldDoesNotChangeTheOwner(bool initiallyClaimed)
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        bool claim = initiallyClaimed;
        using var source = new KeyboardSource();
        source.Start((key, _, down) => key == Key && down && Volatile.Read(ref claim));

        try
        {
            Send(Key, true);
            Flush(window);
            Volatile.Write(ref claim, !initiallyClaimed);
            Send(Key, true);
            Send(Key, false);
            Flush(window);

            Assert.Equal(initiallyClaimed ? [] : PassedSequence, window.KeyDowns);
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void AKeyHeldBeforeTheHookStartsKeepsItsApplicationRelease()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        try
        {
            Send(Key, true);
            Flush(window);
            using var source = new KeyboardSource();
            source.Start((key, _, down) => key == Key && down);
            Send(Key, true);
            Send(Key, false);
            Flush(window);

            Assert.Equal(PassedSequence, window.KeyDowns);
            Assert.Equal(0, source.Drain(new KeyEvent[8], 8));
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void ForegroundChangesDoNotTurnAHeldShortcutIntoFreshPresses()
    {
        using var first = new TestWindow();
        using var second = new TestWindow();
        Assert.True(first.Activate(), Desktop.WhyNotInFront("the first receiver"));
        using var source = new KeyboardSource();
        source.Start((key, _, down) => key == Key && down);
        try
        {
            Send(Key, true);
            Flush(first);
            Assert.True(second.Activate(), Desktop.WhyNotInFront("the second receiver"));
            Send(Key, true);
            Send(Key, false);
            Flush(second);

            Assert.Empty(first.KeyDowns);
            Assert.Empty(second.KeyDowns);
            var events = new KeyEvent[8];
            Assert.Equal(2, source.Drain(events, events.Length));
            Assert.False(events[0].IsRepeat);
            Assert.True(events[1].IsRepeat);
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public unsafe void AnInputDesktopChangeRecoversAReleaseTheHookCouldNotSee()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        bool claim = true;
        using var source = new KeyboardSource();
        source.Start((key, _, down) => key == Key && down && Volatile.Read(ref claim));
        try
        {
            Send(Key, true);
            Flush(window);
            // Model a release on the secure desktop: no matching up reaches this hook.
            Volatile.Write(ref claim, false);
            long before = source.DesktopResets;
            // NotifyWinEvent with a null HWND does not synthesize this system event.
            // Use an actual disposable input desktop and always switch back, without
            // locking the workstation or entering a credential/security desktop.
            HDESK original = PInvoke.OpenInputDesktop(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_SWITCHDESKTOP);
            HDESK other = default;
            try
            {
                Assert.False(original.IsNull);
                fixed (char* name = $"ShubbakInputTest-{Guid.NewGuid():N}")
                    other = PInvoke.CreateDesktop(name, null, null, 0,
                        (uint)(DESKTOP_ACCESS_FLAGS.DESKTOP_CREATEWINDOW | DESKTOP_ACCESS_FLAGS.DESKTOP_SWITCHDESKTOP), null);
                Assert.False(other.IsNull);
                bool restored;
                try { Assert.True(PInvoke.SwitchDesktop(other)); }
                finally { restored = PInvoke.SwitchDesktop(original); }
                Assert.True(restored, "the original input desktop could not be restored");
            }
            finally
            {
                if (!other.IsNull) PInvoke.CloseDesktop(other);
                if (!original.IsNull) PInvoke.CloseDesktop(original);
            }
            Assert.True(SpinWait.SpinUntil(() => source.DesktopResets > before, TimeSpan.FromSeconds(5)), "desktop reset was not delivered");
            Assert.True(window.Activate(), Desktop.WhyNotInFront("the receiver after restoring the desktop"));
            Send(Key, true);
            Send(Key, false);
            Flush(window);
            Assert.Equal([true, false], window.KeyDowns);
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void AFaultingRepeatProbeCannotLeaveItsForwardedPressStuck()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        bool fail = false;
        using var source = new KeyboardSource();
        source.Start((key, _, down) =>
        {
            if (key != Key || !down) return false;
            if (Volatile.Read(ref fail)) throw new InvalidOperationException("deliberate repeat failure");
            return true;
        });
        try
        {
            Send(Key, true);
            Flush(window);
            Volatile.Write(ref fail, true);
            Send(Key, true);
            Send(Key, false);
            Flush(window);
            Assert.Equal([true, false], window.KeyDowns);
            Assert.Equal(1, source.Faults);
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void AFailedWakeDoesNotBothPublishTheCommandAndForwardItsKey()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        using var source = new KeyboardSource();
        source.Start((key, _, down) => key == Key && down);
        source.WorkQueued = () => throw new InvalidOperationException("deliberate wake failure");
        try
        {
            Send(Key, true);
            Send(Key, false);
            Flush(window);
            Assert.Empty(window.KeyDowns);
            Assert.Equal(1, source.Drain(new KeyEvent[8], 8));
            Assert.Equal(1, source.Faults);
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void AFaultingReleaseProbeDoesNotLeakAnUnmatchedApplicationRelease()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        using var source = new KeyboardSource();
        source.Start((key, _, down) =>
        {
            if (key != Key) return false;
            if (!down) throw new InvalidOperationException("deliberate release failure");
            return true;
        });
        try
        {
            Send(Key, true);
            Send(Key, false);
            Flush(window);
            Assert.Empty(window.KeyDowns);
            Assert.Equal(1, source.Faults);
        }
        finally { ReleaseAll(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheHookThreadAllocatesNothingAcrossHeldKeyInput(bool claimed)
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        long allocatedAtBarrier = 0;
        using var source = new KeyboardSource();
        source.Start((key, _, down) =>
        {
            if (key == Barrier && down)
                Volatile.Write(ref allocatedAtBarrier, GC.GetAllocatedBytesForCurrentThread());
            return key == Key && down && claimed;
        });
        try
        {
            // Warm both first-press and repeat paths, including tiered JIT/interop
            // setup. Then empty the ring so a claimed repeat run tests allocation,
            // rather than the intentionally bounded queue's overflow policy.
            for (int i = 0; i < 200; i++) { Send(Key, true); Send(Key, true); Send(Key, false); Flush(window); }
            source.Drain(new KeyEvent[1024], 1024);
            long before = Volatile.Read(ref allocatedAtBarrier);
            Send(Key, true);
            for (int i = 0; i < 1000; i++) Send(Key, true);
            Send(Key, false);
            Flush(window);

            Assert.Equal(0, Volatile.Read(ref allocatedAtBarrier) - before);
            Assert.Equal(0, source.Dropped);
            Assert.Equal(0, source.Faults);
        }
        finally { ReleaseAll(); }
    }

    private static void Flush(TestWindow window)
    {
        int next = window.KeyboardBarriers + 1;
        Send(Barrier, true);
        Send(Barrier, false);
        TestWindow.PumpUntil(() => window.KeyboardBarriers >= next);
        Assert.Equal(next, window.KeyboardBarriers);
    }

    private static void Send(ushort key, bool down)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki.wVk = (VIRTUAL_KEY)key;
        input.Anonymous.ki.dwFlags = down ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        input.Anonymous.ki.dwExtraInfo = TestWindow.KeyboardMarker;
        Assert.Equal(1u, PInvoke.SendInput(new ReadOnlySpan<INPUT>(in input), Marshal.SizeOf<INPUT>()));
    }

    private static void ReleaseAll()
    {
        Send(Key, false);
        Send(Control, false);
        Send(Barrier, false);
    }

    public void Dispose()
    {
        if (_original != 0 && Win32Window.Exists(_original)) WindowActions.Focus(_original);
    }
}
