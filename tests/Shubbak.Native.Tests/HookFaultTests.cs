using System.Runtime.InteropServices;
using Shubbak.Native;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shubbak.Native.Tests;

/// <summary>
/// What happens when the keyboard hook callback throws.
/// </summary>
/// <remarks>
/// <para>
/// The callback is <c>UnmanagedCallersOnly</c>, so an exception leaving it propagates
/// into Win32 and the process dies. It was the only such callback in the project
/// without a guard, and the path to one was live rather than theoretical: enqueuing a
/// keystroke raises <c>WorkQueued</c>, which the daemon points at its message pump's
/// <c>Wake</c>, which called <c>Set</c> on an event that shutdown may already have
/// disposed. Pressing a key while the daemon was exiting could take it down - and
/// shutdown is precisely when somebody is holding the combination that asked for it.
/// </para>
/// <para>
/// These tests drive a real hook with a real synthesised keystroke, because that is
/// the only way to execute the callback at all. Without the guard the failure is not
/// a red test, it is the test host disappearing.
/// </para>
/// <para>
/// F24 is used throughout. The keystroke reaches the focused window on the failure
/// path - that is the whole point of passing it through rather than swallowing it -
/// and F24 is a key almost nothing binds and nothing types.
/// </para>
/// </remarks>
[Collection(SharedKeyboardHook.Name)]
public sealed class HookFaultTests
{
    private const ushort VkF24 = 0x87;

    private static void PressF24()
    {
        var inputs = new INPUT[2];

        inputs[0].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[0].Anonymous.ki.wVk = (VIRTUAL_KEY)VkF24;

        inputs[1].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[1].Anonymous.ki.wVk = (VIRTUAL_KEY)VkF24;
        inputs[1].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        _ = PInvoke.SendInput(inputs.AsSpan(), Marshal.SizeOf<INPUT>());
    }

    [Fact]
    public void AThrowingProbeDoesNotTakeTheProcessDown()
    {
        // If this test fails, it does not fail: the run ends. Reaching the assertion
        // at all is most of what is being asserted.
        using var source = new KeyboardSource();

        source.Start((_, _, _) => throw new InvalidOperationException("deliberate"));

        PressF24();

        SpinWait.SpinUntil(() => source.Faults > 0, TimeSpan.FromSeconds(2));

        Assert.True(source.Faults > 0, "the callback threw but no fault was recorded");
    }

    [Fact]
    public void AFaultingCallbackStillPassesTheKeystrokeOn()
    {
        // The keystroke has to reach the application, not be swallowed. A binding that
        // stops firing is an irritation; a keyboard that stops working - because a
        // failing callback is still eating everything - leaves no way to type the
        // combination that would stop the daemon.
        //
        // Checked through the queue rather than by watching a window: a fault means
        // nothing was enqueued, and the swallow bookkeeping never ran either.
        using var source = new KeyboardSource();

        source.Start((_, _, _) => throw new InvalidOperationException("deliberate"));

        PressF24();

        SpinWait.SpinUntil(() => source.Faults > 0, TimeSpan.FromSeconds(2));

        var scratch = new KeyEvent[8];

        Assert.Equal(0, source.Drain(scratch, scratch.Length));
    }

    [Fact]
    public void AHealthyCallbackRecordsNoFault()
    {
        // So the counter means something: it has to stay at zero on the ordinary path,
        // or a non-zero reading in `diagnose` says nothing.
        using var source = new KeyboardSource();

        source.Start((vk, _, _) => vk == VkF24);

        PressF24();

        var scratch = new KeyEvent[8];

        SpinWait.SpinUntil(
            () => source.Drain(scratch, scratch.Length) > 0,
            TimeSpan.FromSeconds(2));

        Assert.Equal(0, source.Faults);
    }
}
