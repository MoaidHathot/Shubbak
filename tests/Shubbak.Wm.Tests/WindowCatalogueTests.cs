using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.Wm.Tests;

/// <summary>
/// Describing the whole desktop, not just the part Shubbak arranges.
/// </summary>
/// <remarks>
/// <para>
/// The tree contains only managed windows, which makes it exactly the wrong place to
/// look for one that has gone missing. This is the query that answers "where did that
/// window go", so the cases that matter are the ones the tree cannot express.
/// </para>
/// <para>
/// Only the join is tested here. Discovery reads the live desktop, which cannot be
/// asserted against on a machine whose window list is whatever happens to be open.
/// </para>
/// </remarks>
public sealed class WindowCatalogueTests
{
    private static WindowCatalogue.Discovered Seen(
        nint handle,
        string title = "a window",
        ManageDecision decision = default,
        string concealment = "none") =>
        new(handle, title, "TestClass", "test", 4321, false, decision, concealment);

    private static WindowManager WithOneWorkspace()
    {
        var wm = new WindowManager();

        var bounds = new Rect(0, 0, 1920, 1080);
        var monitor = new MonitorNode("\\\\.\\DISPLAY1", bounds, bounds, 96);
        wm.AddMonitor(monitor);
        wm.AddWorkspace(new WorkspaceNode("1"), monitor);
        wm.ActivateWorkspace(monitor.Workspaces[0]);

        return wm;
    }

    [Fact]
    public void AnUnmanagedWindowCarriesTheReasonItWasPassedOver()
    {
        WindowManager wm = WithOneWorkspace();
        var registry = new WindowRegistry();

        IReadOnlyList<WindowCandidate> described = WindowCatalogue.Join(
            [Seen(0x100, decision: ManageDecision.No(ExclusionReason.Elevated))], wm, registry);

        WindowCandidate only = Assert.Single(described);

        Assert.False(only.Managed);
        Assert.NotNull(only.ExclusionReason);

        // The reason is the whole value of the query. "Not managed" alone leaves the
        // user exactly where they started; "runs at a higher integrity level" tells
        // them what to do about it.
        Assert.Contains("integrity level", only.ExclusionReason, StringComparison.Ordinal);
        Assert.Null(only.Workspace);
        Assert.Null(only.State);
    }

    [Fact]
    public void BeingManagedOverrulesTheFilter()
    {
        WindowManager wm = WithOneWorkspace();
        var registry = new WindowRegistry();

        var node = new WindowNode(0x200, new WindowIdentity
        {
            Title = "managed", ProcessName = "test", ClassName = "TestClass",
        });

        wm.ManageWindow(node);
        registry.Adopt(0x200, node);

        // The filter's verdict on a window Shubbak has concealed itself. Every window
        // on an inactive workspace reads exactly like this, because cloaking is how
        // they are concealed - so trusting the filter here would tell the user that
        // every window not currently on screen had been excluded.
        IReadOnlyList<WindowCandidate> described = WindowCatalogue.Join(
            [Seen(0x200, decision: ManageDecision.No(ExclusionReason.CloakedByShell), concealment: "cloaked")],
            wm,
            registry);

        WindowCandidate only = Assert.Single(described);

        Assert.True(only.Managed);
        Assert.Null(only.ExclusionReason);
        Assert.Equal("1", only.Workspace);
        Assert.Equal("cloaked", only.Concealment);
    }

    [Fact]
    public void ConcealmentIsReportedAsAMechanism()
    {
        WindowManager wm = WithOneWorkspace();
        var registry = new WindowRegistry();

        IReadOnlyList<WindowCandidate> described = WindowCatalogue.Join(
            [
                Seen(0x1, concealment: "none"),
                Seen(0x2, concealment: "cloaked"),
                Seen(0x3, concealment: "hidden"),
                Seen(0x4, concealment: "minimised"),
            ],
            wm,
            registry);

        // Three ways of being off screen that are not interchangeable. Minimised is
        // the user's own doing and the taskbar shows it; cloaked is recoverable and
        // usually Shubbak's doing; hidden is what a crashed window manager leaves
        // behind and is the one the user cannot undo without help.
        Assert.Equal(["none", "cloaked", "hidden", "minimised"], described.Select(w => w.Concealment));
    }

    [Fact]
    public void TheFocusedWindowIsMarkedAndCarriesItsRecency()
    {
        WindowManager wm = WithOneWorkspace();
        var registry = new WindowRegistry();

        var first = new WindowNode(0x300, new WindowIdentity
        {
            Title = "first", ProcessName = "test", ClassName = "TestClass",
        });

        var second = new WindowNode(0x301, new WindowIdentity
        {
            Title = "second", ProcessName = "test", ClassName = "TestClass",
        });

        wm.ManageWindow(first);
        wm.ManageWindow(second);
        registry.Adopt(0x300, first);
        registry.Adopt(0x301, second);

        wm.FocusWindow(first);
        wm.FocusWindow(second);

        IReadOnlyList<WindowCandidate> described =
            WindowCatalogue.Join([Seen(0x300), Seen(0x301)], wm, registry);

        WindowCandidate a = described.Single(w => w.Handle == 0x300);
        WindowCandidate b = described.Single(w => w.Handle == 0x301);

        Assert.False(a.Focused);
        Assert.True(b.Focused);

        // Recency is what lets a client put the likely answer first. Without it the
        // only orderings available are the z-order, which a concealed window is not
        // meaningfully in, and the title, which nobody remembers.
        Assert.True(b.FocusSequence > a.FocusSequence);
    }

    [Fact]
    public void AWindowKnownToTheRegistryButNotOnAWorkspaceIsStillDescribed()
    {
        WindowManager wm = WithOneWorkspace();
        var registry = new WindowRegistry();

        // Adopted by the registry but never inserted into the tree. Reachable during
        // adoption, and the join must not assume the two are always in step - a null
        // dereference here would take the whole query down.
        var orphan = new WindowNode(0x400, new WindowIdentity
        {
            Title = "orphan", ProcessName = "test", ClassName = "TestClass",
        });

        registry.Adopt(0x400, orphan);

        WindowCandidate only = Assert.Single(WindowCatalogue.Join([Seen(0x400)], wm, registry));

        Assert.True(only.Managed);
        Assert.Null(only.Workspace);
        Assert.False(only.WorkspaceDisplayed);
    }
}
