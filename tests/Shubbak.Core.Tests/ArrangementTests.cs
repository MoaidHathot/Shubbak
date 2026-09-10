using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for recording a workspace's tree and putting it back.
/// </summary>
/// <remarks>
/// The shape used throughout: <c>Workspace(splith) [ a | Column [ b / c ] ]</c>, with
/// a at 0.6 and the column at 0.4, the column split 0.3 / 0.7 - the arrangement a
/// demo has after a few minutes of dragging.
/// </remarks>
public sealed class ArrangementTests
{
    private static WindowNode Window(string process, string className, string title, string? path = null)
    {
        return new WindowNode(0, new WindowIdentity
        {
            ProcessName = process,
            ClassName = className,
            Title = title,
            ProcessPath = path,
        });
    }

    /// <summary>Builds the shape above on a fresh window manager.</summary>
    private static (WindowManager Wm, WindowNode A, WindowNode B, WindowNode C) Shape()
    {
        WindowManager wm = WmFixture.Create(workspaceNames: ["1", "2"]);

        WindowNode a = Window("code", "Chrome_WidgetWin_1", "main.cs - Code");
        WindowNode b = Window("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", "pwsh");
        WindowNode c = Window("firefox", "MozillaWindowClass", "docs - Firefox");

        wm.ManageWindow(a);
        wm.ManageWindow(b);
        wm.Split(SplitLayout.Vertical);
        wm.ManageWindow(c);

        WorkspaceNode workspace = wm.FocusedWorkspace!;
        var column = (ContainerNode)workspace.Children[1];

        workspace.SetChildRatio(a, 0.6);
        column.SetChildRatio(b, 0.3);

        return (wm, a, b, c);
    }

    // ---- capture -----------------------------------------------------------

    [Fact]
    public void CaptureRecordsTheShapeTheLayoutsAndTheRatios()
    {
        (WindowManager wm, _, _, _) = Shape();

        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "demo", DateTimeOffset.UnixEpoch)!;

        Assert.Equal("demo", recorded.Name);
        Assert.Equal("1", recorded.Workspace);
        Assert.Equal("splith", recorded.Layout);
        Assert.Equal(3, recorded.WindowCount);
        Assert.Equal(2, recorded.Children.Count);

        ArrangementNode a = recorded.Children[0];
        Assert.False(a.IsContainer);
        Assert.Equal("code", a.ProcessName);
        Assert.Equal("Chrome_WidgetWin_1", a.ClassName);
        Assert.Equal(0.6, a.Ratio, 3);

        ArrangementNode column = recorded.Children[1];
        Assert.True(column.IsContainer);
        Assert.Equal("splitv", column.Layout);
        Assert.Equal(0.4, column.Ratio, 3);
        Assert.Equal(["WindowsTerminal", "firefox"], column.Children!.Select(n => n.ProcessName));
        Assert.Equal(0.3, column.Children![0].Ratio, 3);
        Assert.Equal(0.7, column.Children![1].Ratio, 3);
    }

    [Fact]
    public void TitlesAreHashedNeverStoredAndThePathIsKept()
    {
        WindowManager wm = WmFixture.Create();
        wm.ManageWindow(Window("firefox", "MozillaWindowClass", "Confidential Q3 - Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe"));
        wm.ManageWindow(Window("code", "Chrome_WidgetWin_1", "secret.cs"));

        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "x", DateTimeOffset.UnixEpoch)!;
        string json = System.Text.Json.JsonSerializer.Serialize(
            new ArrangementFile(1, [recorded]), ArrangementJsonContext.Default.ArrangementFile);

        Assert.DoesNotContain("Confidential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("firefox.exe", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Arrangements.HashTitle("Confidential Q3 - Firefox"), recorded.Children[0].TitleHash);
    }

    [Fact]
    public void TheTitleHashIsTheSameInEveryProcess()
    {
        // string.GetHashCode is seeded per process; a hash written by one window manager
        // and read by the next has to be the same number, or it never breaks a tie.
        Assert.Equal(Arrangements.HashTitle("pwsh"), Arrangements.HashTitle("PWSH"));
        Assert.Equal(1306565163, Arrangements.HashTitle("pwsh"));
        Assert.Equal(0, Arrangements.HashTitle(null));
        Assert.Equal(0, Arrangements.HashTitle(""));
    }

    [Fact]
    public void OnlyWhatTilesIsRecordedAndAContainerLeftWithOneChildBecomesTheChild()
    {
        (WindowManager wm, _, WindowNode b, _) = Shape();

        // Float the terminal: the column is left with the browser alone.
        wm.FocusWindow(b);
        wm.ToggleFloating();

        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "demo", DateTimeOffset.UnixEpoch)!;

        Assert.Equal(2, recorded.WindowCount);
        Assert.Equal("code", recorded.Children[0].ProcessName);

        // The child, at the column's share, exactly as the tree would flatten it.
        ArrangementNode second = recorded.Children[1];
        Assert.False(second.IsContainer);
        Assert.Equal("firefox", second.ProcessName);
    }

    [Fact]
    public void NothingTilingMeansNothingToRecord()
    {
        WindowManager wm = WmFixture.Create();
        Assert.Null(Arrangements.Capture(wm.FocusedWorkspace!, "empty", DateTimeOffset.UnixEpoch));

        wm.Open("a");
        wm.ToggleFloating();
        Assert.Null(Arrangements.Capture(wm.FocusedWorkspace!, "floating", DateTimeOffset.UnixEpoch));
    }

    // ---- restore -----------------------------------------------------------

    [Fact]
    public void RestorePutsDraggedWindowsBackIntoTheRecordedShape()
    {
        (WindowManager wm, WindowNode a, WindowNode b, WindowNode c) = Shape();
        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "demo", DateTimeOffset.UnixEpoch)!;

        // A few minutes of dragging: everything flat, equal, the other way round.
        wm.FocusWindow(a);
        wm.MoveDirection(Direction.Right);
        wm.MoveDirection(Direction.Right);
        wm.FocusWindow(c);
        wm.MoveDirection(Direction.Left);
        wm.EqualiseSiblings();
        wm.SetLayout(SplitLayout.Vertical);

        WmResult result = wm.RestoreArrangement(recorded);

        Assert.True(result.Succeeded);
        ArrangementRestored account = result.Single<ArrangementRestored>();
        Assert.Equal((3, 0, 0), (account.Placed, account.Missing, account.Kept));
        Assert.True(result.Has<LayoutChanged>());

        WorkspaceNode workspace = wm.FocusedWorkspace!;
        Assert.Same(SplitLayout.Horizontal, workspace.Layout);
        Assert.Equal(2, workspace.Children.Count);
        Assert.Same(a, workspace.Children[0]);
        Assert.Equal(0.6, a.SizeRatio, 3);

        var column = Assert.IsType<ContainerNode>(workspace.Children[1]);
        Assert.Same(SplitLayout.Vertical, column.Layout);
        Assert.Equal(0.4, column.SizeRatio, 3);
        Assert.Equal([b, c], column.Children);
        Assert.Equal(0.3, b.SizeRatio, 3);
        Assert.Equal(0.7, c.SizeRatio, 3);
    }

    [Fact]
    public void WindowsAreMatchedByProcessAndClassNotByHandle()
    {
        (WindowManager wm, WindowNode a, WindowNode b, WindowNode c) = Shape();
        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "demo", DateTimeOffset.UnixEpoch)!;

        // The browser was closed and reopened: a new node, same process and class.
        wm.UnmanageWindow(c);
        WindowNode reopened = Window("firefox", "MozillaWindowClass", "something else entirely");
        wm.ManageWindow(reopened);

        WmResult result = wm.RestoreArrangement(recorded);

        Assert.True(result.Succeeded);
        var column = Assert.IsType<ContainerNode>(wm.FocusedWorkspace!.Children[1]);
        Assert.Equal([b, reopened], column.Children);
        _ = a;
    }

    [Fact]
    public void OfTwoWindowsOfOneProgramTheOneThatWasThereIsPreferred()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode left = Window("firefox", "MozillaWindowClass", "left");
        WindowNode right = Window("firefox", "MozillaWindowClass", "right");
        wm.ManageWindow(left);
        wm.ManageWindow(right);
        wm.FocusedWorkspace!.SetChildRatio(left, 0.7);

        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "two", DateTimeOffset.UnixEpoch)!;

        // Swapped by hand.
        wm.FocusWindow(left);
        wm.MoveDirection(Direction.Right);
        Assert.Equal([right, left], wm.FocusedWorkspace!.Children);

        wm.RestoreArrangement(recorded);

        // The title hash tells them apart, so left is on the left again with its share.
        Assert.Equal([left, right], wm.FocusedWorkspace!.Children);
        Assert.Equal(0.7, left.SizeRatio, 3);
    }

    [Fact]
    public void ARecordedWindowThatIsNotOpenIsLeftOutAndItsShareGoesToItsSiblings()
    {
        (WindowManager wm, WindowNode a, _, WindowNode c) = Shape();
        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "demo", DateTimeOffset.UnixEpoch)!;

        // The terminal is gone. The column had [b / c]; with b gone it is c alone,
        // which is not a container at all.
        wm.UnmanageWindow(wm.FocusedWorkspace!.Children[1].Children[0] as WindowNode ?? throw new InvalidOperationException());

        WmResult result = wm.RestoreArrangement(recorded);

        ArrangementRestored account = result.Single<ArrangementRestored>();
        Assert.Equal((2, 1, 0), (account.Placed, account.Missing, account.Kept));

        WorkspaceNode workspace = wm.FocusedWorkspace!;
        Assert.Equal([a, c], workspace.Children);
        Assert.Equal(0.6, a.SizeRatio, 3);
        Assert.Equal(0.4, c.SizeRatio, 3);
    }

    [Fact]
    public void AWindowTheArrangementDoesNotKnowStaysAtTheEndWithAFairShare()
    {
        (WindowManager wm, WindowNode a, WindowNode b, WindowNode c) = Shape();
        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "demo", DateTimeOffset.UnixEpoch)!;

        WindowNode stranger = Window("notepad", "Notepad", "notes");
        wm.ManageWindow(stranger);

        WmResult result = wm.RestoreArrangement(recorded);

        ArrangementRestored account = result.Single<ArrangementRestored>();
        Assert.Equal((3, 0, 1), (account.Placed, account.Missing, account.Kept));

        WorkspaceNode workspace = wm.FocusedWorkspace!;
        Assert.Equal(3, workspace.Children.Count);
        Assert.Same(a, workspace.Children[0]);
        Assert.IsType<ContainerNode>(workspace.Children[1]);
        Assert.Same(stranger, workspace.Children[2]);

        // Two recorded children and one stranger: the recorded pair keep their 0.6 / 0.4
        // proportion inside two thirds, and the stranger has the third it would have
        // had as one more child.
        Assert.Equal(0.4, a.SizeRatio, 3);
        Assert.Equal(0.2667, workspace.Children[1].SizeRatio, 3);
        Assert.Equal(0.3333, stranger.SizeRatio, 3);
        Assert.Equal(0.3, b.SizeRatio, 3);
        Assert.Equal(0.7, c.SizeRatio, 3);
    }

    [Fact]
    public void AFloatingWindowIsNeitherMovedNorCounted()
    {
        (WindowManager wm, WindowNode a, WindowNode b, WindowNode c) = Shape();
        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "demo", DateTimeOffset.UnixEpoch)!;

        WindowNode note = Window("notepad", "Notepad", "sticky note");
        wm.ManageWindow(note);
        wm.FocusWindow(note);
        wm.ToggleFloating();

        WmResult result = wm.RestoreArrangement(recorded);

        ArrangementRestored account = result.Single<ArrangementRestored>();
        Assert.Equal((3, 0, 1), (account.Placed, account.Missing, account.Kept));
        Assert.Equal(WindowState.Floating, note.State);
        Assert.Contains(note, wm.FocusedWorkspace!.DescendantWindows());
        _ = (a, b, c);
    }

    [Fact]
    public void RestoringWhereNothingRecordedIsOpenIsRefusedNotQuietlyDone()
    {
        (WindowManager wm, _, _, _) = Shape();
        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "demo", DateTimeOffset.UnixEpoch)!;

        // Another workspace, with a window of its own that the arrangement knows nothing about.
        wm.ActivateWorkspace(wm.Root.FindWorkspace("2")!);
        wm.Open("unrelated");

        WmResult result = wm.RestoreArrangement(recorded);

        Assert.False(result.Succeeded);
        CommandRejected rejected = result.Single<CommandRejected>();
        Assert.Contains("demo", rejected.Reason, StringComparison.Ordinal);
        Assert.Contains("\"2\"", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoringOnAnEmptyWorkspaceIsRefused()
    {
        (WindowManager wm, _, _, _) = Shape();
        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "demo", DateTimeOffset.UnixEpoch)!;

        wm.ActivateWorkspace(wm.Root.FindWorkspace("2")!);

        WmResult result = wm.RestoreArrangement(recorded);

        Assert.False(result.Succeeded);
        Assert.Contains("nothing to arrange", result.Single<CommandRejected>().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoringWhatIsAlreadyInPlaceChangesNothingButStillReports()
    {
        (WindowManager wm, WindowNode a, WindowNode b, WindowNode c) = Shape();
        Arrangement recorded = Arrangements.Capture(wm.FocusedWorkspace!, "demo", DateTimeOffset.UnixEpoch)!;

        WmResult result = wm.RestoreArrangement(recorded);

        Assert.True(result.Succeeded);
        WorkspaceNode workspace = wm.FocusedWorkspace!;
        Assert.Same(a, workspace.Children[0]);
        Assert.Equal([b, c], ((ContainerNode)workspace.Children[1]).Children);
        Assert.Equal(0.6, a.SizeRatio, 3);
    }

    [Fact]
    public void AnUnknownLayoutNameFallsBackRatherThanFailing()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode a = wm.Open("a");
        WindowNode b = wm.Open("b");

        // A file from a future version naming a layout this one has never heard of.
        Arrangement recorded = new("future", "1", "hexagonal", [
            new ArrangementNode(0.5, ProcessName: "a", ClassName: "aClass"),
            new ArrangementNode(0.5, ProcessName: "b", ClassName: "bClass"),
        ], DateTimeOffset.UnixEpoch);

        WmResult result = wm.RestoreArrangement(recorded);

        Assert.True(result.Succeeded);
        Assert.Equal([a, b], wm.FocusedWorkspace!.Children);
    }

    // ---- the file ----------------------------------------------------------

    [Fact]
    public void TheStoreRoundTripsAndReplacesByNameWithoutCase()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shubbak-arrangements-{Guid.NewGuid():N}.json");

        try
        {
            (WindowManager wm, _, _, _) = Shape();
            Arrangement first = Arrangements.Capture(wm.FocusedWorkspace!, "Demo", DateTimeOffset.UnixEpoch)!;

            var store = new ArrangementStore(path);
            store.Put(first);
            store.Put(Arrangements.Capture(wm.FocusedWorkspace!, "other", DateTimeOffset.UnixEpoch)!);
            Assert.True(store.Save());

            var again = new ArrangementStore(path);
            again.Load();

            Assert.Equal(["Demo", "other"], again.All.Select(a => a.Name));
            Assert.NotNull(again.Find("demo"));
            Assert.Equal(3, again.Find("DEMO")!.WindowCount);
            Assert.Equal("splitv", again.Find("demo")!.Children[1].Layout);
            Assert.Equal(0.3, again.Find("demo")!.Children[1].Children![0].Ratio, 3);

            // Replaced in place, so the order somebody saved things in is kept.
            again.Put(first with { Name = "DEMO", Workspace = "9" });
            Assert.Equal(["DEMO", "other"], again.All.Select(a => a.Name));
            Assert.Equal("9", again.Find("demo")!.Workspace);

            Assert.True(again.Remove("Other"));
            Assert.False(again.Remove("Other"));
            Assert.Single(again.All);

            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ACorruptOrForeignFileIsEmptyRatherThanFatal()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shubbak-arrangements-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, "{ this is not json");
            var store = new ArrangementStore(path);
            store.Load();
            Assert.Empty(store.All);

            File.WriteAllText(path, """{"version": 99, "arrangements": []}""");
            store.Load();
            Assert.Empty(store.All);

            var missing = new ArrangementStore(path + ".missing");
            missing.Load();
            Assert.Empty(missing.All);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheDefaultFileSitsBesideTheSessionFile()
    {
        Assert.Equal(Path.GetDirectoryName(SessionStore.DefaultPath), Path.GetDirectoryName(ArrangementStore.DefaultPath));
        Assert.EndsWith("arrangements.json", ArrangementStore.DefaultPath, StringComparison.Ordinal);
    }
}
