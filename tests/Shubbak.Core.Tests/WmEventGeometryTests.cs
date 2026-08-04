using System.Reflection;
using Shubbak.Core.Animation;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Which events oblige the daemon to re-arrange the desktop.
/// </summary>
/// <remarks>
/// <para>
/// The daemon marked the layout dirty for every event of every kind, which is
/// correct and expensive. A pending pass re-arranges the whole tree, reads the
/// position of every visible window, shortens the message pump's wait from 250 ms
/// to 7 ms, and raises the system timer resolution to 1 ms - the last of which is a
/// machine-wide setting rather than Shubbak's own.
/// </para>
/// <para>
/// So the cost of a false positive is paid by the whole computer, and the events
/// that produced the most of them were the two that can never move a window: a
/// declined keystroke, and a window retitling itself.
/// </para>
/// </remarks>
public sealed class WmEventGeometryTests
{
    [Fact]
    public void ADeclinedCommandDoesNotMoveAnything()
    {
        // The case that motivated this: holding a focus key against the leftmost
        // window emits one of these per repeat, and each one forced a full pass.
        Assert.False(new CommandRejected("focus", "no window to the left").AffectsGeometry());
    }

    [Fact]
    public void ARetitledWindowDoesNotMoveAnything()
    {
        // The more expensive case, because nothing bounds its rate. A playing video,
        // a terminal showing its working directory, a browser cycling adverts: each
        // retitle held the system at a 1 ms timer for a pass that could not move a
        // single window. The layout engine never reads WindowNode.Identity.
        WindowNode window = TreeBuilder.Window("player");

        Assert.False(new WindowTitleChanged(window, "previous title").AffectsGeometry());
    }

    [Fact]
    public void BindingModeAndConfigAnnouncementsDoNotMoveAnything()
    {
        // Binding mode is routed to the lookup table and the log; the tree is not
        // touched. ConfigReloaded is an announcement to other processes, sent after
        // the reload path has already applied the new options and marked the layout
        // dirty itself.
        Assert.False(new BindingModeChanged("pause").AffectsGeometry());
        Assert.False(new ConfigReloaded("shubbak.kdl").AffectsGeometry());
    }

    [Fact]
    public void FocusMovingIsTreatedAsGeometric()
    {
        // Deliberately, and it is the subtle one. Focus changes no rectangle, but the
        // layout engine passes the focused window through to Placement.Raise, so in a
        // layout whose rectangles overlap - monocle, fullscreen, maximised - focus is
        // what decides which window is actually seen. Excluding it would leave the
        // focused window behind its neighbour in exactly those layouts.
        WindowNode window = TreeBuilder.Window();

        Assert.True(new WindowFocused(window, null).AffectsGeometry());
    }

    [Fact]
    public void EverythingThatChangesTheTreeIsGeometric()
    {
        WindowNode window = TreeBuilder.Window();
        WorkspaceNode workspace = TreeBuilder.Workspace();
        MonitorNode monitor = TreeBuilder.Monitor();
        ContainerNode container = TreeBuilder.Row(window);

        WmEvent[] geometric =
        [
            new WindowManaged(window, workspace),
            new WindowUnmanaged(window.Id, window.Handle, window.Identity),
            new WindowStateChanged(window, WindowState.Tiling, WindowState.Floating),
            new WindowMoved(window, null, workspace),
            new WindowTagsChanged(window, ["2"], IsSticky: false),
            new WorkspaceActivated(workspace, null, monitor),
            new WorkspaceCreated(workspace, monitor),
            new WorkspaceDestroyed(workspace.Id, workspace.Name),
            new WorkspaceMoved(workspace, monitor, monitor),
            new LayoutChanged(container, "grid"),
            new ContainerResized(container),
            new MonitorAdded(monitor),
            new MonitorRemoved(monitor.Id, monitor.DeviceId),
            new MonitorChanged(monitor),
        ];

        foreach (WmEvent wmEvent in geometric)
            Assert.True(wmEvent.AffectsGeometry(), $"{wmEvent.GetType().Name} should be geometric");
    }

    [Fact]
    public void OneGeometricEventCarriesTheWholeBatch()
    {
        // Results arrive as batches, and a batch is re-applied if any part of it
        // demands it. Focusing a window on an inactive workspace activates that
        // workspace and may reject something on the way, all in one result.
        WindowNode window = TreeBuilder.Window();

        IReadOnlyList<WmEvent> batch =
        [
            new CommandRejected("focus", "nothing there"),
            new WindowTitleChanged(window, "old"),
            new ContainerResized(TreeBuilder.Row(window)),
        ];

        Assert.True(batch.AffectGeometry());
    }

    [Fact]
    public void ABatchOfNothingButNoiseIsNotGeometric()
    {
        WindowNode window = TreeBuilder.Window();

        IReadOnlyList<WmEvent> batch =
        [
            new CommandRejected("focus", "nothing there"),
            new WindowTitleChanged(window, "old"),
        ];

        Assert.False(batch.AffectGeometry());
        Assert.False(Array.Empty<WmEvent>().AffectGeometry());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AWorkspaceSwitchOutranksALayoutChangeInEitherOrder(bool switchFirst)
    {
        // Activating a workspace commonly brings a different layout with it, so both
        // arrive in one batch. The switch is what the user is watching.
        WindowNode window = TreeBuilder.Window();
        var activated = new WorkspaceActivated(TreeBuilder.Workspace(), null, TreeBuilder.Monitor());
        var relaid = new LayoutChanged(TreeBuilder.Row(window), "grid");

        IReadOnlyList<WmEvent> batch = switchFirst ? [activated, relaid] : [relaid, activated];

        Assert.Equal(AnimationKind.WorkspaceSwitch, batch.LayoutAnimationKind());
    }

    [Fact]
    public void EachKindComesFromTheEventThatCausedIt()
    {
        WindowNode window = TreeBuilder.Window();
        ContainerNode container = TreeBuilder.Row(window);

        Assert.Equal(
            AnimationKind.WorkspaceSwitch,
            new WorkspaceActivated(TreeBuilder.Workspace(), null, TreeBuilder.Monitor()).LayoutAnimationKind());

        Assert.Equal(AnimationKind.LayoutChange, new LayoutChanged(container, "grid").LayoutAnimationKind());
        Assert.Equal(AnimationKind.LayoutChange, new ContainerResized(container).LayoutAnimationKind());
    }

    [Fact]
    public void AnOrdinaryMoveCarriesNoOpinionAboutTheMotion()
    {
        // Null means "nothing to say", and the caller keeps the window-move profile.
        // Every event answering with a kind would make the two tunable profiles fire
        // for changes that are not layout changes or workspace switches at all.
        WindowNode window = TreeBuilder.Window();

        Assert.Null(new WindowMoved(window, null, TreeBuilder.Workspace()).LayoutAnimationKind());
        Assert.Null(new WindowFocused(window, null).LayoutAnimationKind());
        Assert.Null(new CommandRejected("focus", "nothing there").LayoutAnimationKind());
        Assert.Null(Array.Empty<WmEvent>().LayoutAnimationKind());
    }

    [Fact]
    public void EveryDeclaredEventHasBeenConsidered()
    {
        // The guard that makes the default safe. AffectsGeometry is written as a list
        // of exclusions, so an event added later is geometric without anyone having
        // thought about it - which is the harmless direction, but only if somebody
        // eventually thinks about it. This is what makes them.
        //
        // Adding a WmEvent therefore fails here until it is named below and covered
        // by one of the tests above.
        //
        // Reflection is the only way to ask "what subtypes exist"; C# has no closed
        // hierarchy and so no compile-time exhaustiveness over one. The trim analyzer
        // objects on principle, and is wrong here specifically: this assembly is a
        // test host that is never trimmed, never published, and never AOT compiled.
#pragma warning disable IL2026
        string[] declared =
        [
            .. typeof(WmEvent).Assembly
                .GetTypes()
                .Where(t => t.IsSealed && !t.IsAbstract && t.IsSubclassOf(typeof(WmEvent)))
                .Select(t => t.Name)
                .OrderBy(name => name, StringComparer.Ordinal),
        ];
#pragma warning restore IL2026

        string[] considered =
        [
            .. new[]
            {
                nameof(BindingModeChanged),
                nameof(CommandRejected),
                nameof(ConfigReloaded),
                nameof(ContainerResized),
                nameof(LayoutChanged),
                nameof(MonitorAdded),
                nameof(MonitorChanged),
                nameof(MonitorRemoved),
                nameof(WindowFocused),
                nameof(WindowManaged),
                nameof(WindowMoved),
                nameof(WindowStateChanged),
                nameof(WindowTagsChanged),
                nameof(WindowTitleChanged),
                nameof(WindowUnmanaged),
                nameof(WorkspaceActivated),
                nameof(WorkspaceCreated),
                nameof(WorkspaceDestroyed),
                nameof(WorkspaceMoved),
            }.OrderBy(name => name, StringComparer.Ordinal),
        ];

        Assert.Equal(considered, declared);
    }
}
