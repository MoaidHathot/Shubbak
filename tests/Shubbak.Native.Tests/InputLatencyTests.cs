using System.Diagnostics;

namespace Shubbak.Native.Tests;

/// <summary>
/// Guards on input latency.
/// </summary>
/// <remarks>
/// <para>
/// A <c>WH_KEYBOARD_LL</c> callback runs on the thread that installed the hook, and
/// <b>until it returns, the keystroke has not reached the focused application</b>.
/// Windows allows it <c>LowLevelHooksTimeout</c> - 300ms by default - before giving
/// up and silently unhooking.
/// </para>
/// <para>
/// That makes the hook thread the most latency-sensitive code in the process, and the
/// failure mode is unusually cruel: installing the hook on the window manager's own
/// message loop made typing sluggish in every application on the machine, with
/// nothing about the symptom pointing at a window manager. It was reported as "the
/// keyboard feels slow", which is not something a test suite would ever have said.
/// </para>
/// <para>
/// These are the regression guards. They are deliberately loose - they are not
/// benchmarks, and they must not fail on a busy build agent. They exist to catch the
/// hook being moved back onto a shared thread, or work creeping into the callback.
/// </para>
/// </remarks>
[Collection(SharedKeyboardHook.Name)]
public sealed class InputLatencyTests
{
    /// <summary>
    /// A probe that never claims a key, so nothing is swallowed while testing.
    /// </summary>
    private static bool NeverBound(ushort virtualKey, KeyModifiers modifiers, bool isKeyDown) => false;

    [Fact]
    public void TheHookRunsOnItsOwnThread()
    {
        // The property that makes latency independent of everything else the window
        // manager does. GlazeWM installs its hook on the dispatcher's event loop, so
        // this is stricter than the reference implementation rather than copying it.
        using var source = new KeyboardSource();

        source.Start(NeverBound);

        // Named so it is identifiable in a hang dump, which is the first thing anyone
        // reaches for when asked why input is late.
        Assert.Equal("Shubbak keyboard hook", source.ThreadName);

        Assert.NotEqual(Environment.CurrentManagedThreadId, source.ThreadId);
    }

    [Fact]
    public void StartingAndStoppingIsClean()
    {
        // Disposal has to remove the hook on the thread that installed it. Getting
        // this wrong leaves a dead hook registered, which Windows charges the whole
        // system for until the process exits.
        for (int i = 0; i < 3; i++)
        {
            using var source = new KeyboardSource();
            source.Start(NeverBound);

            Assert.True(source.IsRunning);
        }
    }

    [Fact]
    public void OnlyOneSourceCanBeActiveAtATime()
    {
        // Two hooks would double the per-keystroke cost for no benefit, and the
        // second would silently win the static callback.
        using var first = new KeyboardSource();
        first.Start(NeverBound);

        using var second = new KeyboardSource();

        Assert.Throws<InvalidOperationException>(() => second.Start(NeverBound));
    }

    [Fact]
    public void DrainingAnIdleSourceCostsNothing()
    {
        // Called every tick of the message loop whether or not anything was typed,
        // so it must not become a place work accumulates.
        using var source = new KeyboardSource();
        source.Start(NeverBound);

        var scratch = new KeyEvent[64];

        long start = Stopwatch.GetTimestamp();

        for (int i = 0; i < 10_000; i++) source.Drain(scratch, scratch.Length);

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(500),
            $"10,000 idle drains took {elapsed.TotalMilliseconds:F0}ms, which suggests " +
            "Drain is doing more than checking two indices.");
    }
}
