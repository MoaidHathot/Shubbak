using Shubbak.Core.Animation;
using Shubbak.Core.Geometry;

namespace Shubbak.Core.Tests;

/// <summary>
/// That the animation tick allocates nothing.
/// </summary>
/// <remarks>
/// <para>
/// ADR 0001 constraint 2 says the frame path must not allocate, and gives the reason:
/// a collection suspends every thread in the process, including the one servicing the
/// low-level keyboard hook, and until that thread answers the keystroke has not
/// reached the focused application. The constraint was argued for once and then
/// nothing enforced it.
/// </para>
/// <para>
/// It has since been measured in the shipping daemon - allocation per frame reported
/// as zero across more than a thousand frames - but a measurement is a description of
/// one run and a test is a property. This turns the first into the second.
/// </para>
/// <para>
/// Deliberately not a test of the whole tick. The layout pass allocates on purpose,
/// per container per pass, and pretending otherwise would either fail immediately or
/// force a threshold so loose it asserted nothing. What must be free of allocation is
/// the path taken on every frame between layout passes.
/// </para>
/// </remarks>
public sealed class TickAllocationTests
{
    private static AnimationEngine Engine() =>
        new(AnimationOptions.Default with
        {
            WindowMove = new AnimationProfile(TimeSpan.FromSeconds(30), Easing.EaseOut),
            MinimumAnimatedDistance = 0,
        });

    [Fact]
    public void AdvancingOneWindowAllocatesNothing()
    {
        AnimationEngine engine = Engine();
        var scratch = new AnimationFrame[64];

        engine.Retarget(1, new Rect(0, 0, 100, 100), new Rect(4000, 3000, 900, 700), AnimationKind.WindowMove);

        // Warm up first: the first call through jits the method, and tiered
        // compilation allocates on the runtime's behalf in a way that has nothing to
        // do with what Tick does afterwards.
        for (int i = 0; i < 200; i++) engine.Tick(1, scratch);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++) engine.Tick(1, scratch);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void AdvancingManyWindowsAllocatesNothing()
    {
        // Twenty is the count ADR 0001's frame-time measurements were taken at, and
        // more windows is where a per-window allocation would hide: one buried in the
        // loop is invisible at a batch of one and obvious at a batch of twenty.
        AnimationEngine engine = Engine();
        var scratch = new AnimationFrame[64];

        for (int handle = 1; handle <= 20; handle++)
        {
            engine.Retarget(
                handle,
                new Rect(handle, handle, 100, 100),
                new Rect(4000 - handle, 3000 - handle, 900, 700),
                AnimationKind.WindowMove);
        }

        for (int i = 0; i < 200; i++) engine.Tick(1, scratch);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++) engine.Tick(1, scratch);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void RetargetingAnAlreadyMovingWindowAllocatesNothing()
    {
        // Retarget runs on the layout path rather than the frame path, but a layout
        // pass happens while windows are moving - every focus change is one - and it
        // reaches into the same track array. A lookup that allocated here would do so
        // once per window per pass.
        AnimationEngine engine = Engine();
        var scratch = new AnimationFrame[64];

        for (int handle = 1; handle <= 8; handle++)
            engine.Retarget(handle, new Rect(0, 0, 100, 100), new Rect(900, 700, 300, 200), AnimationKind.WindowMove);

        for (int i = 0; i < 200; i++)
        {
            engine.Tick(1, scratch);
            for (int handle = 1; handle <= 8; handle++)
                engine.Retarget(handle, new Rect(0, 0, 100, 100), new Rect(950, 720, 300, 200), AnimationKind.WindowMove);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 500; i++)
        {
            engine.Tick(1, scratch);

            for (int handle = 1; handle <= 8; handle++)
            {
                engine.Retarget(
                    handle, new Rect(0, 0, 100, 100), new Rect(950, 720, 300, 200), AnimationKind.WindowMove);
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void LookingUpAMovingWindowAllocatesNothing()
    {
        // Asked once per placement per layout pass, to blend a retarget from where the
        // window actually is rather than from where it started. It used to go through
        // a dictionary; it now scans the track array, and the point of the scan was
        // that it costs nothing.
        AnimationEngine engine = Engine();
        var scratch = new AnimationFrame[64];

        for (int handle = 1; handle <= 8; handle++)
            engine.Retarget(handle, new Rect(0, 0, 100, 100), new Rect(900, 700, 300, 200), AnimationKind.WindowMove);

        engine.Tick(1, scratch);

        for (int i = 0; i < 200; i++) engine.TryGetCurrent(4, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++)
        {
            engine.TryGetCurrent(4, out _);
            engine.TryGetCurrent(999, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
