using System.Runtime.InteropServices;
using Shubbak.Native;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shubbak.Native.Tests;

/// <summary>
/// The hook's half of a binding that runs on release.
/// </summary>
/// <remarks>
/// <para>
/// A release binding is two decisions in the hook: the press is claimed and swallowed
/// like any binding's, and the release - which the hook used to swallow silently - is
/// delivered as an event when the table says a release binding is there. The table's
/// half is tested in <c>Shubbak.Wm.Tests</c>; this is the hook, with real input
/// through the real low-level hook, on the F24 key nothing else uses.
/// </para>
/// <para>
/// The probe stands in for the binding table: it is asked on the way down and on the
/// way up, and what it answers on the way up decides whether a release event exists.
/// </para>
/// </remarks>
[Collection(SharedKeyboardHook.Name)]
public sealed class ReleaseBindingHookTests
{
    private const ushort VkF24 = 0x87;

    private static void PressAndReleaseF24()
    {
        var inputs = new INPUT[2];

        inputs[0].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[0].Anonymous.ki.wVk = (VIRTUAL_KEY)VkF24;

        inputs[1].type = INPUT_TYPE.INPUT_KEYBOARD;
        inputs[1].Anonymous.ki.wVk = (VIRTUAL_KEY)VkF24;
        inputs[1].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        _ = PInvoke.SendInput(inputs.AsSpan(), Marshal.SizeOf<INPUT>());
    }

    private static List<KeyEvent> DrainAll(KeyboardSource source, int expected)
    {
        var scratch = new KeyEvent[8];
        List<KeyEvent> events = [];

        SpinWait.SpinUntil(() =>
        {
            int count = source.Drain(scratch, scratch.Length);
            for (int i = 0; i < count; i++) events.Add(scratch[i]);
            return events.Count >= expected;
        }, TimeSpan.FromSeconds(2));

        // One more look, so a release arriving after the press was counted is seen.
        Thread.Sleep(100);
        int late = source.Drain(scratch, scratch.Length);
        for (int i = 0; i < late; i++) events.Add(scratch[i]);

        return events;
    }

    [Fact]
    public void TheReleaseIsDeliveredWhenTheProbeClaimsIt()
    {
        using var source = new KeyboardSource();

        // Claimed both ways: a release binding on F24.
        source.Start((vk, _, _) => vk == VkF24);

        PressAndReleaseF24();

        List<KeyEvent> events = DrainAll(source, expected: 2);

        Assert.Equal(2, events.Count);
        Assert.True(events[0].IsKeyDown, "the press comes first");
        Assert.False(events[1].IsKeyDown, "the release is delivered as an event");
        Assert.Equal(VkF24, events[1].VirtualKey);
        Assert.False(events[1].IsRepeat);
    }

    [Fact]
    public void TheReleaseIsSwallowedButNotDeliveredForAnOrdinaryBinding()
    {
        using var source = new KeyboardSource();

        // Claimed on the way down only, which is every binding that runs on press.
        source.Start((vk, _, isKeyDown) => vk == VkF24 && isKeyDown);

        PressAndReleaseF24();

        List<KeyEvent> events = DrainAll(source, expected: 1);

        KeyEvent only = Assert.Single(events);
        Assert.True(only.IsKeyDown);
    }

    [Fact]
    public void AReleaseWhosePressWasNotClaimedIsNobodys()
    {
        // The release is asked about only for a key whose press was swallowed; an
        // unbound key's release goes through to the application untouched, and the
        // probe is not consulted for it at all.
        using var source = new KeyboardSource();

        int askedOnRelease = 0;

        source.Start((vk, _, isKeyDown) =>
        {
            if (vk == VkF24 && !isKeyDown) Interlocked.Increment(ref askedOnRelease);
            return false;
        });

        PressAndReleaseF24();

        List<KeyEvent> events = DrainAll(source, expected: 0);

        Assert.Empty(events);
        Assert.Equal(0, askedOnRelease);
    }
}
