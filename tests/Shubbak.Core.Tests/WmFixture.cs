using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;

namespace Shubbak.Core.Tests;

/// <summary>
/// Helpers for driving a <see cref="WindowManager"/> in tests.
/// </summary>
internal static class WmFixture
{
    /// <summary>
    /// A window manager with the given monitors and declared workspaces.
    /// </summary>
    /// <param name="options">Behaviour tuning; defaults are used when null.</param>
    /// <param name="monitors">
    /// How many 1920x1080 monitors to attach, laid out left to right.
    /// </param>
    /// <param name="workspaceNames">
    /// Declared (non-transient) workspaces, all placed on the first monitor.
    /// </param>
    public static WindowManager Create(
        WmOptions? options = null,
        int monitors = 1,
        params string[] workspaceNames)
    {
        var wm = new WindowManager(options);

        for (int i = 0; i < monitors; i++)
        {
            wm.AddMonitor(TreeBuilder.Monitor(
                $"\\\\.\\DISPLAY{i + 1}", x: i * 1920, width: 1920, height: 1080));
        }

        if (workspaceNames.Length == 0) workspaceNames = ["1"];

        foreach (string name in workspaceNames)
            wm.AddWorkspace(new WorkspaceNode(name), wm.Root.Monitors[0]);

        wm.ActivateWorkspace(wm.Root.Monitors[0].Workspaces[0]);
        return wm;
    }

    /// <summary>Manages a new window and returns it.</summary>
    public static WindowNode Open(this WindowManager wm, string title)
    {
        WindowNode window = TreeBuilder.Window(title);
        wm.ManageWindow(window);
        return window;
    }

    /// <summary>Runs a layout pass, so rectangle-dependent behaviour is meaningful.</summary>
    public static WindowManager Arrange(this WindowManager wm)
    {
        wm.ComputePlacements();
        return wm;
    }

    /// <summary>The rectangle currently computed for a window.</summary>
    public static Rect RectOf(this WindowManager wm, WindowNode window)
    {
        wm.ComputePlacements();
        return window.Rect;
    }

    public static bool Has<T>(this WmResult result) where T : WmEvent =>
        result.Events.OfType<T>().Any();

    public static T Single<T>(this WmResult result) where T : WmEvent =>
        result.Events.OfType<T>().Single();
}
