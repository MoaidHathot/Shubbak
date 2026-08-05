using Shubbak.Core.Animation;
using Shubbak.Core.Geometry;

namespace Shubbak.Core.Tests;

/// <summary>
/// Which frames are worth sending, and which of those need a resize.
/// </summary>
/// <remarks>
/// <para>
/// Every frame used to be emitted and every frame carried a size. Neither is free at
/// the far end: a <c>DeferWindowPos</c> entry is a real window move and a real repaint
/// request, and a resize additionally makes DWM reallocate the window's redirection
/// surface and makes the application process <c>WM_SIZE</c> and lay its own contents
/// out again.
/// </para>
/// <para>
/// The engine is the only thing that knows what the previous frame said, so it is the
/// only thing that can answer either question.
/// </para>
/// </remarks>
public sealed class AnimationFrameEconomyTests
{
    private const long Handle = 42;

    private static AnimationEngine Engine(TimeSpan duration, Easing curve) =>
        new(AnimationOptions.Default with
        {
            WindowMove = new AnimationProfile(duration, curve),

            // Off, so a short move is actually animated. At the default of eight
            // pixels a small move is dismissed as negligible and no track is created
            // at all - which made the first version of the skip test pass with zero
            // frames emitted, for entirely the wrong reason.
            MinimumAnimatedDistance = 0,
        });

    /// <summary>Runs to completion, returning every frame the engine chose to emit.</summary>
    private static List<AnimationFrame> Collect(AnimationEngine engine, double step)
    {
        var emitted = new List<AnimationFrame>();
        var scratch = new AnimationFrame[8];

        for (int i = 0; i < 1000 && engine.IsAnimating; i++)
        {
            int count = engine.Tick(step, scratch);

            for (int f = 0; f < count; f++) emitted.Add(scratch[f]);
        }

        return emitted;
    }

    [Fact]
    public void AFrameThatChangesNothingIsNotSent()
    {
        // Ten pixels over half a second, ticked every five milliseconds. That is a
        // hundred frames for ten distinct positions, so ninety of them round to the
        // same rectangle as the frame before and ask a window to move to where it
        // already is.
        AnimationEngine engine = Engine(TimeSpan.FromMilliseconds(500), Easing.Linear);

        engine.Retarget(Handle, new Rect(0, 0, 100, 100), new Rect(10, 0, 100, 100), AnimationKind.WindowMove);

        List<AnimationFrame> emitted = Collect(engine, step: 5);

        Assert.True(emitted.Count is > 0 and < 20, $"{emitted.Count} frames sent for a ten-pixel move");
    }

    [Fact]
    public void TheFinalFrameIsAlwaysSentEvenIfItChangesNothing()
    {
        // IsFinal is what makes the committer record where the window came to rest.
        // Drop that record and the next layout pass has no position for the window and
        // places it again - a visible jump, arriving from the fix for a visible jump.
        AnimationEngine engine = Engine(TimeSpan.FromMilliseconds(500), Easing.Linear);

        engine.Retarget(Handle, new Rect(0, 0, 100, 100), new Rect(10, 0, 100, 100), AnimationKind.WindowMove);

        List<AnimationFrame> emitted = Collect(engine, step: 5);

        Assert.Single(emitted, frame => frame.IsFinal);
        Assert.True(emitted[^1].IsFinal, "the last frame emitted was not the final one");
        Assert.Equal(new Rect(10, 0, 100, 100), emitted[^1].Rect);
    }

    [Fact]
    public void APureTranslationNeedsNoResize()
    {
        // The case this exists for: equally-sized tiles swapping, a workspace slide, a
        // move between monitors of the same resolution. Nothing about the window's size
        // changes, so nothing should ask it to lay itself out again.
        AnimationEngine engine = Engine(TimeSpan.FromMilliseconds(100), Easing.Linear);

        engine.Retarget(Handle, new Rect(0, 0, 300, 200), new Rect(800, 0, 300, 200), AnimationKind.WindowMove);

        List<AnimationFrame> emitted = Collect(engine, step: 10);

        Assert.All(
            emitted.Where(frame => !frame.IsFinal),
            frame => Assert.True(frame.SizeUnchanged, $"frame at {frame.Rect} asked for a resize"));
    }

    [Fact]
    public void AMotionThatResizesSaysSo()
    {
        AnimationEngine engine = Engine(TimeSpan.FromMilliseconds(100), Easing.Linear);

        engine.Retarget(Handle, new Rect(0, 0, 300, 200), new Rect(0, 0, 900, 600), AnimationKind.WindowMove);

        List<AnimationFrame> emitted = Collect(engine, step: 10);

        Assert.Contains(emitted, frame => !frame.SizeUnchanged);
    }

    [Fact]
    public void TheFinalFrameAlwaysCarriesTheSize()
    {
        // Deliberate, even for a pure translation. The committer records what it
        // intended rather than what it observed, so if an application resisted an
        // intermediate resize, skipping the resize on the resting frame would leave it
        // permanently the wrong size with nothing left to notice. One full resize per
        // motion costs nothing.
        AnimationEngine engine = Engine(TimeSpan.FromMilliseconds(100), Easing.Linear);

        engine.Retarget(Handle, new Rect(0, 0, 300, 200), new Rect(800, 0, 300, 200), AnimationKind.WindowMove);

        List<AnimationFrame> emitted = Collect(engine, step: 10);

        AnimationFrame last = emitted[^1];

        Assert.True(last.IsFinal);
        Assert.False(last.SizeUnchanged, "the resting frame skipped the resize");
    }

    [Fact]
    public void TracksFinishingOutOfOrderStayAddressable()
    {
        // The case the discarded index dictionary existed for. Tracks are compacted in
        // place as they finish, so a lookup that assumed stable positions would return
        // another window's rectangle - or miss one that is still moving.
        var engine = new AnimationEngine(AnimationOptions.Default);

        engine.Retarget(1, new Rect(0, 0, 100, 100), new Rect(500, 0, 100, 100), AnimationKind.WindowMove);
        engine.Retarget(2, new Rect(0, 0, 100, 100), new Rect(500, 0, 100, 100), AnimationKind.WindowMove);
        engine.Retarget(3, new Rect(0, 0, 100, 100), new Rect(500, 0, 100, 100), AnimationKind.WindowMove);

        // Take the middle one out, which is what moves the last into its slot.
        engine.Remove(2);

        Assert.True(engine.TryGetCurrent(1, out _));
        Assert.False(engine.TryGetCurrent(2, out _));
        Assert.True(engine.TryGetCurrent(3, out _));
        Assert.Equal(2, engine.ActiveCount);

        engine.Remove(1);

        Assert.False(engine.TryGetCurrent(1, out _));
        Assert.True(engine.TryGetCurrent(3, out _));
        Assert.Equal(1, engine.ActiveCount);
    }
}
