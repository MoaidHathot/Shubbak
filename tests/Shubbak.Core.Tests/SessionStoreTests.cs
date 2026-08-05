using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for session persistence.
/// </summary>
/// <remarks>
/// The value being protected here is that a restart or reboot does not scatter a
/// carefully arranged set of workspaces. The interesting cases are all about
/// <i>identification</i>: a window handle does not survive a restart and a title is
/// too volatile, so matching has to be tolerant without being indiscriminate.
/// </remarks>
public sealed class SessionStoreTests
{
    private static WindowNode Window(string process, string className, string title)
    {
        return new WindowNode(0, new WindowIdentity
        {
            ProcessName = process,
            ClassName = className,
            Title = title,
        });
    }

    private static (WindowManager Wm, WorkspaceNode One, WorkspaceNode Two) Setup()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);
        return (wm, wm.Root.FindWorkspace("1")!, wm.Root.FindWorkspace("2")!);
    }

    [Fact]
    public void CaptureRecordsEachWindowsWorkspace()
    {
        (WindowManager wm, _, _) = Setup();

        WindowNode editor = Window("code", "Chrome_WidgetWin_1", "main.cs - Code");
        wm.ManageWindow(editor);

        wm.FocusWorkspace("2");
        WindowNode browser = Window("firefox", "MozillaWindowClass", "Example - Firefox");
        wm.ManageWindow(browser);

        Session session = SessionStore.Capture(wm.Root);

        Assert.Equal(2, session.Windows.Count);
        Assert.Contains(session.Windows, w => w.ProcessName == "code" && w.Workspace == "1");
        Assert.Contains(session.Windows, w => w.ProcessName == "firefox" && w.Workspace == "2");
    }

    [Fact]
    public void TitlesAreHashedRatherThanStored()
    {
        // Titles contain document names, URLs and file paths. Storing them would
        // turn a convenience file into a record of what the user was working on.
        (WindowManager wm, _, _) = Setup();

        wm.ManageWindow(Window("firefox", "MozillaWindowClass", "Confidential Q3 Results - Firefox"));

        Session session = SessionStore.Capture(wm.Root);
        string json = System.Text.Json.JsonSerializer.Serialize(
            session, SessionJsonContext.Default.Session);

        Assert.DoesNotContain("Confidential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Q3 Results", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TagsAndStickinessSurvive()
    {
        (WindowManager wm, _, _) = Setup();

        WindowNode chat = Window("teams", "TeamsWebView", "Chat");
        wm.ManageWindow(chat);
        wm.FocusWindow(chat);
        wm.Tag("2", TagMode.Add);
        wm.ToggleSticky();

        Session session = SessionStore.Capture(wm.Root);
        RememberedWindow remembered = Assert.Single(session.Windows);

        Assert.Contains("2", remembered.Tags);
        Assert.True(remembered.Sticky);
    }

    [Fact]
    public void ScratchpadContentsAreNotRemembered()
    {
        // Restoring them would summon a hidden window into view on the next start,
        // which is the opposite of what stashing it meant.
        (WindowManager wm, _, _) = Setup();

        wm.ManageWindow(Window("notepad", "Notepad", "notes.txt"));
        wm.ToggleScratchpad("notes");

        Session session = SessionStore.Capture(wm.Root);

        Assert.Empty(session.Windows);
    }

    [Fact]
    public void MatchingRequiresProcessAndClassToAgree()
    {
        (WindowManager wm, _, _) = Setup();
        wm.ManageWindow(Window("code", "Chrome_WidgetWin_1", "main.cs"));

        Session session = SessionStore.Capture(wm.Root);
        HashSet<int> claimed = [];

        // Same process, different class - a different kind of window entirely.
        Assert.Null(SessionStore.Match(
            session,
            new WindowIdentity { ProcessName = "code", ClassName = "SomethingElse", Title = "main.cs" },
            handle: 0,
            claimed));

        Assert.Null(SessionStore.Match(
            session,
            new WindowIdentity { ProcessName = "other", ClassName = "Chrome_WidgetWin_1", Title = "main.cs" },
            handle: 0,
            claimed));
    }

    [Fact]
    public void MatchingToleratesAChangedTitle()
    {
        // A browser's title changes with every tab, so requiring it to match exactly
        // would mean browsers never get restored - which is most of the value.
        (WindowManager wm, _, _) = Setup();
        wm.ManageWindow(Window("firefox", "MozillaWindowClass", "Yesterday's page - Firefox"));

        Session session = SessionStore.Capture(wm.Root);

        RememberedWindow? matched = SessionStore.Match(
            session,
            new WindowIdentity
            {
                ProcessName = "firefox",
                ClassName = "MozillaWindowClass",
                Title = "Something entirely different - Firefox",
            },
            handle: 0,
            []);

        Assert.NotNull(matched);
        Assert.Equal("1", matched.Workspace);
    }

    [Fact]
    public void AMatchingTitleWinsOverOneThatOnlyMatchesByClass()
    {
        // This is what puts three browser windows back on three different
        // workspaces rather than all on the first one.
        (WindowManager wm, _, _) = Setup();

        wm.ManageWindow(Window("firefox", "MozillaWindowClass", "Docs"));

        wm.FocusWorkspace("2");
        wm.ManageWindow(Window("firefox", "MozillaWindowClass", "Mail"));

        Session session = SessionStore.Capture(wm.Root);

        RememberedWindow? matched = SessionStore.Match(
            session,
            new WindowIdentity { ProcessName = "firefox", ClassName = "MozillaWindowClass", Title = "Mail" },
            handle: 0,
            []);

        Assert.NotNull(matched);
        Assert.Equal("2", matched.Workspace);
    }

    [Fact]
    public void EachRememberedEntryIsConsumedOnlyOnce()
    {
        // Without this, N windows of one application would all match the first
        // entry and pile onto one workspace.
        (WindowManager wm, _, _) = Setup();

        wm.ManageWindow(Window("firefox", "MozillaWindowClass", "A"));
        wm.FocusWorkspace("2");
        wm.ManageWindow(Window("firefox", "MozillaWindowClass", "B"));

        Session session = SessionStore.Capture(wm.Root);
        HashSet<int> claimed = [];

        var identity = new WindowIdentity
        {
            ProcessName = "firefox",
            ClassName = "MozillaWindowClass",
            Title = "unrecognised",
        };

        RememberedWindow? first = SessionStore.Match(session, identity, handle: 0, claimed);
        RememberedWindow? second = SessionStore.Match(session, identity, handle: 0, claimed);
        RememberedWindow? third = SessionStore.Match(session, identity, handle: 0, claimed);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Workspace, second.Workspace);

        // Only two were saved, so a third window gets no placement rather than
        // stealing one.
        Assert.Null(third);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        (WindowManager wm, _, _) = Setup();
        wm.ManageWindow(Window("code", "Chrome_WidgetWin_1", "main.cs"));

        string path = Path.Combine(Path.GetTempPath(), $"shubbak-session-{Guid.NewGuid():N}.json");

        try
        {
            Assert.True(SessionStore.Save(wm.Root, path));

            Session? loaded = SessionStore.Load(path);

            Assert.NotNull(loaded);
            Assert.Single(loaded.Windows);
            Assert.Equal("code", loaded.Windows[0].ProcessName);
            Assert.Equal("1", loaded.Windows[0].Workspace);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadingAMissingFileIsNotAnError()
    {
        Assert.Null(SessionStore.Load(
            Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void ACorruptSessionIsIgnoredRatherThanFailingStartup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shubbak-corrupt-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, "{ this is not json");

            // The user simply gets the default placement, rather than no window
            // manager at all.
            Assert.Null(SessionStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFutureVersionIsIgnored()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shubbak-version-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """{"version":999,"saved_at":"2026-01-01T00:00:00+00:00","windows":[]}""");

            Assert.Null(SessionStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SavingIsAtomic()
    {
        // Written to a temporary file and moved into place, so a crash midway cannot
        // leave a truncated session that fails to parse on the next start.
        (WindowManager wm, _, _) = Setup();
        wm.ManageWindow(Window("code", "Chrome_WidgetWin_1", "main.cs"));

        string path = Path.Combine(Path.GetTempPath(), $"shubbak-atomic-{Guid.NewGuid():N}.json");

        try
        {
            SessionStore.Save(wm.Root, path);
            SessionStore.Save(wm.Root, path);

            Assert.False(File.Exists(path + ".tmp"), "the temporary file was left behind");
            Assert.NotNull(SessionStore.Load(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }
}
