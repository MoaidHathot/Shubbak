using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Telling two windows of the same application apart when a session is restored.
/// </summary>
/// <remarks>
/// <para>
/// Reported from use, twice over. Two browser windows on two workspaces swapped
/// places after the machine resumed from sleep. Two chat windows - one showing a
/// conversation, one showing a meeting - ended up on the same workspace and would not
/// stay apart.
/// </para>
/// <para>
/// Both are the same fault. Process and class are identical between two windows of one
/// application by definition, so the only thing separating them was a hash of the
/// title - and a title is what changes while Shubbak is not running. A browser's is
/// whatever page it is showing; a chat application's is whichever conversation is
/// open. With every distinguishing field stale, each window matched the first
/// unclaimed entry for its application, and which one got there first was enumeration
/// order.
/// </para>
/// </remarks>
public sealed class SessionIdentityTests
{
    private static WindowNode Window(long handle, string process, string className, string title) =>
        new(handle, new WindowIdentity
        {
            ProcessName = process,
            ClassName = className,
            Title = title,
        });

    private static WindowManager Setup() =>
        WmFixture.Create(workspaceNames: ["1", "2"]);

    [Fact]
    public void TwoWindowsOfOneApplicationKeepTheirWorkspacesWhenBothTitlesChange()
    {
        // The reported bug, stated as a test. Two browser windows, each on its own
        // workspace, each showing a different page by the time Shubbak starts again.
        WindowManager wm = Setup();

        wm.ManageWindow(Window(0x1111, "firefox", "MozillaWindowClass", "News - Firefox"));
        wm.FocusWorkspace("2");
        wm.ManageWindow(Window(0x2222, "firefox", "MozillaWindowClass", "Mail - Firefox"));

        Session session = SessionStore.Capture(wm.Root);
        HashSet<int> claimed = [];

        // Enumerated in the opposite order to the one they were saved in, which is the
        // part no code controls and the part that decided the outcome.
        RememberedWindow? second = SessionStore.Match(
            session,
            new WindowIdentity
            {
                ProcessName = "firefox",
                ClassName = "MozillaWindowClass",
                Title = "Something else entirely",
            },
            handle: 0x2222,
            claimed);

        RememberedWindow? first = SessionStore.Match(
            session,
            new WindowIdentity
            {
                ProcessName = "firefox",
                ClassName = "MozillaWindowClass",
                Title = "A third unrelated page",
            },
            handle: 0x1111,
            claimed);

        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.Equal("1", first.Workspace);
        Assert.Equal("2", second.Workspace);
    }

    [Fact]
    public void AHandleBeatsAMatchingTitleOnAnotherWindow()
    {
        // Both signals present and disagreeing. The handle is an identity; the title
        // is a guess that happens to be right about a different window - which is
        // exactly the situation when a chat window is showing the conversation that
        // the other one was showing when the session was saved.
        WindowManager wm = Setup();

        wm.ManageWindow(Window(0x1111, "ms-teams", "TeamsWebView", "Chat"));
        wm.FocusWorkspace("2");
        wm.ManageWindow(Window(0x2222, "ms-teams", "TeamsWebView", "Team sync"));

        Session session = SessionStore.Capture(wm.Root);

        RememberedWindow? matched = SessionStore.Match(
            session,
            new WindowIdentity
            {
                ProcessName = "ms-teams",
                ClassName = "TeamsWebView",

                // The title the *other* window had.
                Title = "Chat",
            },
            handle: 0x2222,
            []);

        Assert.NotNull(matched);
        Assert.Equal("2", matched.Workspace);
    }

    [Fact]
    public void AHandleIsNotTrustedAcrossADifferentApplication()
    {
        // Windows reuses handle values. Requiring process and class to agree first
        // confines a stale match to windows that were already indistinguishable.
        WindowManager wm = Setup();

        wm.ManageWindow(Window(0x1111, "firefox", "MozillaWindowClass", "News"));

        Session session = SessionStore.Capture(wm.Root);

        RememberedWindow? matched = SessionStore.Match(
            session,
            new WindowIdentity { ProcessName = "notepad", ClassName = "Notepad", Title = "News" },
            handle: 0x1111,
            []);

        Assert.Null(matched);
    }

    [Fact]
    public void ASessionWrittenBeforeHandlesWereRecordedStillRestores()
    {
        // Zero means "not recorded", not "handle zero". A session file written by an
        // earlier build must not start matching everything to its first entry.
        var session = new Session(
            // The version Capture writes; a mismatched one is rejected on load.
            SessionStore.Capture(Setup().Root).Version,
            DateTimeOffset.UtcNow,
            [
                new RememberedWindow("firefox", "MozillaWindowClass", 0, "2", [], false, "Tiling"),
            ]);

        RememberedWindow? matched = SessionStore.Match(
            session,
            new WindowIdentity
            {
                ProcessName = "firefox",
                ClassName = "MozillaWindowClass",
                Title = "anything",
            },
            handle: 0x9999,
            []);

        Assert.NotNull(matched);
        Assert.Equal("2", matched.Workspace);
    }

    [Fact]
    public void AnApplicationThatRestartedFallsBackToTheOlderSignals()
    {
        // Handles do not survive the application restarting, and nothing could make
        // them: at that point the two windows are genuinely indistinguishable. What
        // must not happen is that the restore stops working altogether.
        WindowManager wm = Setup();

        wm.ManageWindow(Window(0x1111, "firefox", "MozillaWindowClass", "Docs"));

        Session session = SessionStore.Capture(wm.Root);

        RememberedWindow? matched = SessionStore.Match(
            session,
            new WindowIdentity
            {
                ProcessName = "firefox",
                ClassName = "MozillaWindowClass",
                Title = "Docs",
            },

            // A new handle, because the application was restarted.
            handle: 0x7777,
            []);

        Assert.NotNull(matched);
        Assert.Equal("1", matched.Workspace);
    }
}
