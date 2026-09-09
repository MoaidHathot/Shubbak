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
    public void TheMonitorsPathIsRememberedWhenKnownAndMatchedFirst()
    {
        // The GDI name is handed out in enumeration order and a replug can renumber it,
        // so a view saved on \\.\DISPLAY2 belongs to the panel, not to the name.
        WindowManager wm = Create();
        wm.Root.Monitors[0].DevicePath = @"\\?\DISPLAY#DELA124#5&38500b75&0&UID4355#{e6f07b5f}";

        SessionStore.Save(wm.Root, _path, focusedMonitor: wm.FocusedMonitor);

        RememberedMonitor remembered = Assert.Single(SessionStore.Load(_path)!.Monitors!);
        Assert.Equal(wm.Root.Monitors[0].DevicePath, remembered.DevicePath);

        // Next session: the same panel has come back as DISPLAY2, and a different one
        // has taken DISPLAY1. The remembered view finds the panel.
        var root = new Core.Tree.RootNode();
        var other = TreeBuilder.Monitor("\\\\.\\DISPLAY1");
        other.DevicePath = @"\\?\DISPLAY#SHP1523#4&1c3f2a1&0&UID8388688#{e6f07b5f}";
        var same = TreeBuilder.Monitor("\\\\.\\DISPLAY2", x: 1920);
        same.DevicePath = remembered.DevicePath;
        root.AddMonitor(other);
        root.AddMonitor(same);

        Assert.Same(same, SessionStore.FindRemembered(root, remembered));
    }

    [Fact]
    public void ARememberedMonitorWithoutAPathFallsBackToTheName()
    {
        // A session written before the path was recorded, or a display whose path
        // could not be read - a remote session's - restores the way it always did.
        var remembered = new RememberedMonitor("\\\\.\\DISPLAY2", "3", Focused: false);

        var root = new Core.Tree.RootNode();
        root.AddMonitor(TreeBuilder.Monitor("\\\\.\\DISPLAY1"));
        root.AddMonitor(TreeBuilder.Monitor("\\\\.\\DISPLAY2", x: 1920));

        Assert.Same(root.Monitors[1], SessionStore.FindRemembered(root, remembered));
        Assert.Null(SessionStore.FindRemembered(root, remembered with { DeviceId = "\\\\.\\DISPLAY9" }));
    }

    [Fact]
    public void RecordingThePathCountsAsAChangeWorthWriting()
    {
        // The routine save skips a fingerprint it has already written. The path is
        // part of the fingerprint, so the first save after it starts being known does
        // not find the file "unchanged" and leave it without one.
        WindowManager wm = Create();

        SessionStore.Save(wm.Root, _path, routine: true);
        DateTime first = File.GetLastWriteTimeUtc(_path);

        Thread.Sleep(30);

        wm.Root.Monitors[0].DevicePath = @"\\?\DISPLAY#DELA124#5&38500b75&0&UID4355#{e6f07b5f}";
        SessionStore.Save(wm.Root, _path, routine: true);

        Assert.NotEqual(first, File.GetLastWriteTimeUtc(_path));
        Assert.NotNull(Assert.Single(SessionStore.Load(_path)!.Monitors!).DevicePath);
    }

    [Fact]
    public void ADeletedSessionIsWrittenAgain()
    {
        // The skip remembered what had been written and not whether it was still
        // there, so deleting the file convinced Shubbak it was already saved and it
        // never came back - the state survived exactly until someone tidied up.
        WindowManager wm = Create();

        SessionStore.Save(wm.Root, _path, routine: true);
        Assert.True(File.Exists(_path));

        File.Delete(_path);

        SessionStore.Save(wm.Root, _path, routine: true);

        Assert.True(File.Exists(_path), "an unchanged session must still be written when the file is gone");
    }

    [Fact]
    public void TwoPathsEachGetTheirOwnFile()
    {
        // One shared fingerprint meant the second path was considered already written
        // because the first had been, so it silently never appeared.
        WindowManager wm = Create();
        string other = _path + ".other";

        try
        {
            SessionStore.Save(wm.Root, _path, routine: true);
            SessionStore.Save(wm.Root, other, routine: true);

            Assert.True(File.Exists(other));
        }
        finally
        {
            if (File.Exists(other)) File.Delete(other);
        }
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
