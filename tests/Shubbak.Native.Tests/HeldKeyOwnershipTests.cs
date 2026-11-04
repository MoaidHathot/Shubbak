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
    private static readonly KeyboardBinding Bound = new();
    private static readonly bool[] PassedSequence = [true, true, false];
    private readonly nint _original = Win32Window.GetForeground();

    [Fact]
    public void APartialDrainRearmsTheCoalescedWakeUntilTheQueueIsEmpty()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        using var wake = new AutoResetEvent(false);
        using var source = new KeyboardSource { WorkQueued = () => wake.Set() };
        source.Start((key, _) => key == Key ? Bound : null);
        try
        {
            for (int i = 0; i < 193; i++) { Send(Key, true); Send(Key, false); }
            Flush(window);
            var scratch = new KeyEvent[64];
            int total = 0;
            while (total < 193)
            {
                Assert.True(wake.WaitOne(TimeSpan.FromSeconds(1)), "queued work lost its wake");
                total += source.Drain(scratch, scratch.Length);
            }
            Assert.Equal(193, total);
            Assert.False(wake.WaitOne(0));
            Assert.Equal(0, source.Dropped);
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void OverflowPassesAFreshPressAndItsReleaseTogether()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        using var source = new KeyboardSource(1);
        source.Start((key, _) => key == Key ? Bound : null);
        try
        {
            Send(Key, true);
            Send(Key, false);
            Send(Key, true);
            Send(Key, false);
            Flush(window);
            Assert.Equal([true, false], window.KeyDowns);
            Assert.Equal(1, source.Drain(new KeyEvent[1], 1));
            Assert.Equal(1, source.Dropped);
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void AClaimedReleaseKeepsItsQueueSlotDuringOverflow()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        var onRelease = new KeyboardBinding(release: true);
        using var source = new KeyboardSource(1);
        source.Start((key, _) => key == Key ? onRelease : key == Barrier ? Bound : null);
        try
        {
            Send(Key, true);
            // The ring has room only for the promised F24 release, so F23 must pass.
            Send(Barrier, true);
            Send(Barrier, false);
            Send(Key, false);
            TestWindow.PumpUntil(() => window.KeyboardBarriers > 0);
            var events = new KeyEvent[1];
            Assert.True(SpinWait.SpinUntil(() => source.Drain(events, 1) == 1, TimeSpan.FromSeconds(2)));
            Assert.Equal(Key, events[0].VirtualKey);
            Assert.False(events[0].IsKeyDown);
            Assert.Same(onRelease, events[0].Binding);
            Assert.Equal(1, source.Dropped);
            Assert.Empty(window.KeyDowns);
        }
        finally { ReleaseAll(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AReleaseKeepsItsPressBindingAcrossModifierOrderAndReload(bool modifierFirst, bool reload)
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        var original = new KeyboardBinding(release: true);
        var replacement = new KeyboardBinding(release: true);
        KeyboardBinding current = original;
        using var source = new KeyboardSource();
        source.Start((key, _) => key == Key ? Volatile.Read(ref current) : null);
        try
        {
            Send(Control, true);
            Send(Key, true);
            Flush(window);
            if (reload) Volatile.Write(ref current, replacement);
            if (modifierFirst) Send(Control, false);
            Send(Key, false);
            if (!modifierFirst) Send(Control, false);
            Flush(window);
            var events = new KeyEvent[8];
            Assert.Equal(1, source.Drain(events, events.Length));
            Assert.False(events[0].IsKeyDown);
            Assert.Equal(KeyModifiers.Control, events[0].Modifiers);
            Assert.Same(original, events[0].Binding);
            Assert.Empty(window.KeyDowns);
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void NonRepeatingHoldsDoNotFillTheQueueOrWakeTheConsumer()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        var binding = new KeyboardBinding(repeat: false);
        int wakes = 0;
        using var source = new KeyboardSource(8) { WorkQueued = () => Interlocked.Increment(ref wakes) };
        source.Start((key, _) => key == Key ? binding : null);
        try
        {
            Send(Key, true);
            for (int i = 0; i < 1000; i++) Send(Key, true);
            Send(Key, false);
            Flush(window);
            Assert.Equal(1, source.Drain(new KeyEvent[8], 8));
            Assert.Equal(1, wakes);
            Assert.Equal(0, source.Dropped);
            Assert.Empty(window.KeyDowns);
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void ReleasingAModifierCannotLeakAClaimedKeysRepeatWithoutItsRelease()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        using var source = new KeyboardSource();
        source.Start((key, modifiers) => key == Key && (modifiers & KeyModifiers.Control) != 0 ? Bound : null);

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
        source.Start((key, modifiers) => key == Key && (modifiers & KeyModifiers.Control) != 0 ? Bound : null);

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
        source.Start((key, _) => key == Key && Volatile.Read(ref claim) ? Bound : null);

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
            source.Start((key, _) => key == Key ? Bound : null);
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
        source.Start((key, _) => key == Key ? Bound : null);
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
        source.Start((key, _) => key == Key && Volatile.Read(ref claim) ? Bound : null);
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
    public void RepeatsDoNotRequeryAPotentiallyChangedOrFaultingProbe()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        bool fail = false;
        using var source = new KeyboardSource();
        source.Start((key, _) =>
        {
            if (key != Key) return null;
            if (Volatile.Read(ref fail)) throw new InvalidOperationException("deliberate repeat failure");
            return Bound;
        });
        try
        {
            Send(Key, true);
            Flush(window);
            Volatile.Write(ref fail, true);
            Send(Key, true);
            Send(Key, false);
            Flush(window);
            Assert.Empty(window.KeyDowns);
            Assert.Equal(0, source.Faults);
            Assert.Equal(2, source.Drain(new KeyEvent[8], 8));
        }
        finally { ReleaseAll(); }
    }

    [Fact]
    public void AFailedWakeDoesNotBothPublishTheCommandAndForwardItsKey()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        using var source = new KeyboardSource();
        source.Start((key, _) => key == Key ? Bound : null);
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
    public void ReleasesUseTheCapturedBindingWithoutRequeryingTheProbe()
    {
        using var window = new TestWindow();
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the key receiver"));
        using var source = new KeyboardSource();
        int probes = 0;
        var binding = new KeyboardBinding(release: true);
        source.Start((key, _) =>
        {
            if (key != Key) return null;
            if (++probes > 1) throw new InvalidOperationException("unexpected second lookup");
            return binding;
        });
        try
        {
            Send(Key, true);
            Send(Key, false);
            Flush(window);
            Assert.Empty(window.KeyDowns);
            Assert.Equal(0, source.Faults);
            var events = new KeyEvent[8];
            Assert.Equal(1, source.Drain(events, events.Length));
            Assert.False(events[0].IsKeyDown);
            Assert.Same(binding, events[0].Binding);
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
        source.Start((key, _) =>
        {
            if (key == Barrier)
                Volatile.Write(ref allocatedAtBarrier, GC.GetAllocatedBytesForCurrentThread());
            return key == Key && claimed ? Bound : null;
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
        Assert.True(window.KeyboardBarriers == next,
            $"Expected input barrier {next}, received {window.KeyboardBarriers}. {Desktop.WhyNotInFront("the receiver")}");
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
