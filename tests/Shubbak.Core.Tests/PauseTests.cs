using Shubbak.Core.Commands;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Suspending and resuming tiling, and saying so.
/// </summary>
/// <remarks>
/// Pausing used to change a flag and announce nothing, so no other process could
/// notice it had happened. That is the one state a user most needs told about,
/// because a window manager that has silently stopped arranging windows looks
/// exactly like one that has crashed.
/// </remarks>
public sealed class PauseTests
{
    [Fact]
    public void PausingIsAnnounced()
    {
        WindowManager wm = WmFixture.Create();

        WmResult result = wm.SetPaused(true);

        Assert.True(wm.IsPaused);
        Assert.True(result.Single<PauseChanged>().Paused);
    }

    [Fact]
    public void ResumingIsAnnounced()
    {
        WindowManager wm = WmFixture.Create();
        wm.SetPaused(true);

        WmResult result = wm.SetPaused(false);

        Assert.False(wm.IsPaused);
        Assert.False(result.Single<PauseChanged>().Paused);
    }

    [Fact]
    public void SettingTheStateItIsAlreadyInAnnouncesNothing()
    {
        WindowManager wm = WmFixture.Create();
        wm.SetPaused(true);

        WmResult result = wm.SetPaused(true);

        // A bar that redraws on this event should not be woken by a keybinding that
        // changed nothing, and a repeated key must not produce a stream of them.
        Assert.True(result.Succeeded);
        Assert.False(result.Has<PauseChanged>());
    }

    [Fact]
    public void PausingDoesNotAskForALayoutPass()
    {
        WindowManager wm = WmFixture.Create();

        WmResult result = wm.SetPaused(true);

        // Not arranging windows is the entire point of pausing, so an event that
        // marked the layout dirty would defeat it. Resuming is the daemon's business:
        // it keeps the dirty flag set while paused and applies everything in one pass
        // on the way out.
        Assert.False(result.Events.AffectGeometry());
    }

    [Fact]
    public void TheTogglePassesThroughTheSamePath()
    {
        WindowManager wm = WmFixture.Create();
        var executor = new CommandExecutor(wm);

        CommandOutcome first = executor.Execute(new TogglePauseCommand());
        Assert.True(wm.IsPaused);
        Assert.True(first.Result.Single<PauseChanged>().Paused);

        CommandOutcome second = executor.Execute(new TogglePauseCommand());
        Assert.False(wm.IsPaused);
        Assert.False(second.Result.Single<PauseChanged>().Paused);
    }
}
