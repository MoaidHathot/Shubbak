namespace Shubbak.Core.Tree;

/// <summary>
/// How a window participates in (or opts out of) tiling.
/// </summary>
public enum WindowState
{
    /// <summary>Participates in the layout tree and is sized by it.</summary>
    Tiling,

    /// <summary>Positioned freely; ignored by layout, but still tracked and focusable.</summary>
    Floating,

    /// <summary>Fills its workspace, covering siblings.</summary>
    Fullscreen,

    /// <summary>Maximised via the native window command.</summary>
    Maximised,

    /// <summary>Minimised to the taskbar; retains its position in the tree.</summary>
    Minimised,
}

/// <summary>
/// A leaf node: one managed top-level window.
/// </summary>
/// <remarks>
/// <para>
/// The native handle is held as an opaque <see cref="long"/> rather than a Win32
/// <c>HWND</c>, because <c>Shubbak.Core</c> must not reference Win32 at all
/// (invariant 5 of docs/adr/0001-language-choice.md). The platform layer maps
/// between the two.
/// </para>
/// <para>
/// <see cref="Tags"/> is present from P1 but unused until P5. The data model has
/// to carry it from the start: retrofitting multi-workspace membership later
/// would disturb focus, close handling, and geometry ownership all at once.
/// </para>
/// </remarks>
public sealed class WindowNode : Node
{
    public WindowNode(long handle, WindowIdentity identity)
    {
        Handle = handle;
        Identity = identity;
    }

    /// <summary>Opaque native window handle (an <c>HWND</c> on Windows).</summary>
    public long Handle { get; }

    /// <summary>Matchable attributes, used by window rules and by the bar.</summary>
    public WindowIdentity Identity { get; set; }

    public WindowState State { get; set; } = WindowState.Tiling;

    /// <summary>True when the layout engine should size this window.</summary>
    public bool IsTiled => State is WindowState.Tiling;

    /// <summary>Keeps the window above others of the same kind.</summary>
    public bool IsAlwaysOnTop { get; set; }

    /// <summary>
    /// Additional workspaces this window appears in beyond its home workspace.
    /// Reserved for P5; always empty in P1.
    /// </summary>
    public IReadOnlySet<string> Tags => _tags;
    private readonly HashSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);

    internal bool AddTag(string tag) => _tags.Add(tag);
    internal bool RemoveTag(string tag) => _tags.Remove(tag);

    /// <summary>
    /// Geometry the window uses when it is not being sized by the layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Serves two related purposes, which are the same value in practice:
    /// the position of the window while <see cref="WindowState.Floating"/>, and the
    /// geometry to restore to when leaving <see cref="WindowState.Fullscreen"/> or
    /// <see cref="WindowState.Maximised"/>.
    /// </para>
    /// <para>
    /// Unlike <see cref="Node.Rect"/> - which is computed output that only the
    /// layout engine writes - this is input, owned by the user and the platform
    /// layer, and so is publicly settable.
    /// </para>
    /// </remarks>
    public Geometry.Rect? FloatingRect { get; set; }

    public override string ToString() =>
        $"Window#{Id}[0x{Handle:X}, {Identity.ProcessName}, \"{Identity.Title}\"]";
}
