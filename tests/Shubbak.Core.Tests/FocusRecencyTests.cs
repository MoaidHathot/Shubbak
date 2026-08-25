using Shubbak.Core.Commands;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Ordering windows by how recently they were focused, and reaching one by handle.
/// </summary>
/// <remarks>
/// <para>
/// The tree records the focused window and, per workspace, the one to return to. It
/// could not order windows against one another at all, which is what "go back to what
/// I was just using" needs - and what anything offering a list of windows needs, since
/// most recently used is the only ordering that puts the likely answer first.
/// </para>
/// <para>
/// A counter rather than a clock, so that two focus changes inside one system timer
/// tick still compare correctly. On a 15.6 ms clock that is most of a quick pair.
/// </para>
/// </remarks>
public sealed class FocusRecencyTests
{
    [Fact]
    public void AWindowThatHasNeverBeenFocusedHasNoSequence()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode window = TreeBuilder.Window("never focused");

        Assert.Equal(0, window.FocusSequence);
        _ = wm;
    }

    [Fact]
    public void FocusingStampsAnIncreasingSequence()
    {
        WindowManager wm = WmFixture.Create();

        WindowNode first = wm.Open("first");
        WindowNode second = wm.Open("second");

        wm.FocusWindow(first);
        wm.FocusWindow(second);

        Assert.True(
            second.FocusSequence > first.FocusSequence,
            "the window focused later must sort as more recent");
    }

    [Fact]
    public void RefocusingTheSameWindowDoesNotAdvanceTheCounter()
    {
        WindowManager wm = WmFixture.Create();

        WindowNode window = wm.Open("only");
        wm.FocusWindow(window);
        long stamped = window.FocusSequence;

        wm.FocusWindow(window);

        // SetFocus returns early when nothing changed. If it did not, every command
        // that reasserts focus would reorder the list without the focus having moved.
        Assert.Equal(stamped, window.FocusSequence);
    }

    [Fact]
    public void GoingBackReturnsToThePreviousWindow()
    {
        WindowManager wm = WmFixture.Create();

        WindowNode first = wm.Open("first");
        WindowNode second = wm.Open("second");

        wm.FocusWindow(first);
        wm.FocusWindow(second);

        Assert.True(wm.FocusRecentWindow().Succeeded);
        Assert.Same(first, wm.FocusedWindow);
    }

    [Fact]
    public void GoingBackTwiceReturnsToWhereYouStarted()
    {
        WindowManager wm = WmFixture.Create();

        WindowNode first = wm.Open("first");
        WindowNode second = wm.Open("second");

        wm.FocusWindow(first);
        wm.FocusWindow(second);

        wm.FocusRecentWindow();
        wm.FocusRecentWindow();

        // The behaviour Alt+Tab is actually wanted for. Leaving a window makes it the
        // most recent one, so the pair swaps rather than walking backwards through
        // history.
        Assert.Same(second, wm.FocusedWindow);
    }

    [Fact]
    public void GoingBackCrossesWorkspaces()
    {
        WindowManager wm = WmFixture.Create(monitors: 1, workspaceNames: ["1", "2"]);

        WindowNode onFirst = wm.Open("on workspace one");
        wm.FocusWindow(onFirst);

        wm.FocusWorkspace("2");
        WindowNode onSecond = wm.Open("on workspace two");
        wm.FocusWindow(onSecond);

        Assert.True(wm.FocusRecentWindow().Succeeded);

        // The point of searching globally. A window is easiest to lose when it is not
        // where you are looking, so a search confined to the current workspace would
        // exclude exactly the cases worth having this for.
        Assert.Same(onFirst, wm.FocusedWindow);
        Assert.True(onFirst.IsOnADisplayedWorkspace);
    }

    [Fact]
    public void GoingBackSkipsMinimisedWindows()
    {
        WindowManager wm = WmFixture.Create();

        WindowNode kept = wm.Open("kept");
        WindowNode putAway = wm.Open("put away");
        WindowNode current = wm.Open("current");

        wm.FocusWindow(kept);
        wm.FocusWindow(putAway);
        wm.FocusWindow(current);

        wm.SetWindowState(putAway, WindowState.Minimised);

        wm.FocusRecentWindow();

        // "Take me back to what I was using" must not mean "undo the fact that I
        // deliberately put that away", and focus on a minimised window is focus on
        // something invisible.
        Assert.Same(kept, wm.FocusedWindow);
    }

    [Fact]
    public void GoingBackWithNowhereToGoIsRefusedRatherThanSilent()
    {
        WindowManager wm = WmFixture.Create();
        WindowNode only = wm.Open("only");
        wm.FocusWindow(only);

        WmResult result = wm.FocusRecentWindow();

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Events.OfType<CommandRejected>(),
            e => e.Command == "focus-recent-window");
    }

    [Fact]
    public void AWindowCanBeFocusedByItsHandle()
    {
        WindowManager wm = WmFixture.Create();

        WindowNode first = wm.Open("first");
        WindowNode second = wm.Open("second");
        wm.FocusWindow(first);

        Assert.True(wm.FocusWindowByHandle(second.Handle).Succeeded);
        Assert.Same(second, wm.FocusedWindow);
    }

    [Fact]
    public void FocusingAMinimisedWindowByHandleRestoresItFirst()
    {
        WindowManager wm = WmFixture.Create();

        WindowNode target = wm.Open("target");
        WindowNode other = wm.Open("other");

        wm.SetWindowState(target, WindowState.Minimised);
        wm.FocusWindow(other);

        WmResult result = wm.FocusWindowByHandle(target.Handle);

        Assert.True(result.Succeeded);
        Assert.Same(target, wm.FocusedWindow);
        Assert.NotEqual(WindowState.Minimised, target.State);

        // Both halves must be announced together. The restore is applied to the tree
        // by a helper that does not complete the operation, so an implementation that
        // called the public entry point instead would change the state and tell
        // nobody - the bar and the layout pass would both still believe it minimised.
        Assert.True(result.Has<WindowStateChanged>());
        Assert.True(result.Has<WindowFocused>());
    }

    [Fact]
    public void FocusingAnUnknownHandleIsRefused()
    {
        WindowManager wm = WmFixture.Create();

        WmResult result = wm.FocusWindowByHandle(0x1234);

        // Refused rather than thrown, and refused rather than ignored: the host reads
        // this as "not in the tree" and decides whether to go and reveal it.
        Assert.False(result.Succeeded);
    }
}
