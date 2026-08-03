using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// How often the session file is actually written.
/// </summary>
/// <remarks>
/// The periodic save fired every thirty seconds whether anything had changed or not,
/// and announced each one at info level. An untouched desktop rewrote the file nearly
/// three thousand times a day, and half of everything the log had to say was that it
/// had done so again.
/// </remarks>
public sealed class SessionSaveTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"shubbak-session-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static WindowManager Create()
    {
        WindowManager wm = WmFixture.Create(monitors: 1, workspaceNames: ["1", "2"]);
        wm.Open("a");
        return wm;
    }

    private DateTime WriteTimeAfter(Action<WindowManager> change)
    {
        WindowManager wm = Create();

        SessionStore.Save(wm.Root, _path, routine: true);
        DateTime first = File.GetLastWriteTimeUtc(_path);

        // Coarse timestamps on some file systems; a routine save that does write must
        // be distinguishable from one that does not.
        Thread.Sleep(30);

        change(wm);
        SessionStore.Save(wm.Root, _path, routine: true);

        return first;
    }

    [Fact]
    public void AnUnchangedSessionIsNotRewritten()
    {
        DateTime first = WriteTimeAfter(_ => { });

        Assert.Equal(first, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void ANewWindowIsWritten()
    {
        DateTime first = WriteTimeAfter(wm => wm.Open("b"));

        Assert.NotEqual(first, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void AWindowChangingWorkspaceIsWritten()
    {
        // The whole point of the file. Skipping this write would lose the one thing
        // it exists to remember.
        DateTime first = WriteTimeAfter(wm => wm.MoveToWorkspace("2"));

        Assert.NotEqual(first, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void AWindowChangingStateIsWritten()
    {
        DateTime first = WriteTimeAfter(wm => wm.SetFocusedWindowState(WindowState.Floating));

        Assert.NotEqual(first, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void ADeliberateSaveAlwaysWrites()
    {
        // Shutdown is not routine. Skipping it because nothing had changed since the
        // last poll would be exactly the wrong moment to be clever.
        WindowManager wm = Create();

        SessionStore.Save(wm.Root, _path, routine: true);
        DateTime first = File.GetLastWriteTimeUtc(_path);

        Thread.Sleep(30);
        SessionStore.Save(wm.Root, _path);

        Assert.NotEqual(first, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void WhatWasWrittenStillLoads()
    {
        // Skipping writes must not leave the file behind the tree.
        WindowManager wm = Create();
        wm.MoveToWorkspace("2");

        SessionStore.Save(wm.Root, _path, routine: true);

        Session? loaded = SessionStore.Load(_path);

        Assert.NotNull(loaded);
        Assert.Equal("2", Assert.Single(loaded!.Windows).Workspace);
    }

    [Fact]
    public void ATitleChangeAloneIsNotWritten()
    {
        // The one that defeated the check entirely. A browser tab, an unread count or
        // a terminal's directory rewrites the title constantly, and none of it moves a
        // window anywhere - yet the file was still written every thirty seconds.
        WindowManager wm = Create();
        WindowNode window = wm.Root.DescendantWindows().First();

        SessionStore.Save(wm.Root, _path, routine: true);
        DateTime first = File.GetLastWriteTimeUtc(_path);

        Thread.Sleep(30);

        wm.UpdateTitle(window, "a completely different title");
        SessionStore.Save(wm.Root, _path, routine: true);

        Assert.Equal(first, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void TheTitleIsStillRecordedWhenSomethingElseChanges()
    {
        // It is not being dropped, only stopped from forcing a write on its own. It
        // is what tells two windows of the same application apart when restoring.
        WindowManager wm = Create();
        WindowNode window = wm.Root.DescendantWindows().First();

        wm.UpdateTitle(window, "the title that matters");
        SessionStore.Save(wm.Root, _path);

        Session? loaded = SessionStore.Load(_path);

        Assert.NotNull(loaded);
        Assert.NotEqual(0, Assert.Single(loaded!.Windows).TitleHash);
    }

    [Fact]
    public void SwitchingWorkspaceIsWritten()
    {
        // Which workspace a monitor shows is part of what gets restored, so changing
        // it has to reach the file.
        WindowManager wm = Create();

        SessionStore.Save(wm.Root, _path, routine: true);
        DateTime first = File.GetLastWriteTimeUtc(_path);

        Thread.Sleep(30);

        wm.FocusWorkspace("2");
        SessionStore.Save(wm.Root, _path, routine: true);

        Assert.NotEqual(first, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void TheWorkspaceEachMonitorWasShowingIsRemembered()
    {
        WindowManager wm = Create();
        wm.FocusWorkspace("2");

        SessionStore.Save(wm.Root, _path, focusedMonitor: wm.FocusedMonitor);

        Session? loaded = SessionStore.Load(_path);

        RememberedMonitor monitor = Assert.Single(loaded!.Monitors!);

        Assert.Equal("2", monitor.ActiveWorkspace);
        Assert.True(monitor.Focused);
    }

    [Fact]
    public void AFileWrittenBeforeMonitorsWereRememberedStillLoads()
    {
        // The field is optional so an older session is not thrown away, which would
        // lose every window placement to gain nothing.
        File.WriteAllText(_path, """
            {
              "version": 1,
              "saved_at": "2026-08-03T00:00:00+00:00",
              "windows": [
                {
                  "process_name": "firefox",
                  "class_name": "MozillaWindowClass",
                  "title_hash": 123,
                  "workspace": "3",
                  "tags": [],
                  "sticky": false,
                  "state": "Tiling"
                }
              ]
            }
            """);

        Session? loaded = SessionStore.Load(_path);

        Assert.NotNull(loaded);
        Assert.Equal("3", Assert.Single(loaded!.Windows).Workspace);
    }
}
