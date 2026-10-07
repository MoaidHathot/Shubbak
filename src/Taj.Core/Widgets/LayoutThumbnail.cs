using Shubbak.Core.Geometry;
using Shubbak.Core.Layouts;
using Shubbak.Core.Tree;
using Shubbak.Ui.Layout;

namespace Taj.Core.Widgets;

/// <summary>
/// A layout drawn small: where its panes fall in a box of a given size, as the
/// window manager would place that many windows.
/// </summary>
/// <remarks>
/// <para>
/// Computed by the layout engine itself rather than described by hand. The bar
/// already links the assembly the layouts live in, and a picture that reproduces the
/// arithmetic in miniature would have to be kept in step with it - the grid's rule
/// that a short last row is stretched, the spiral's choice of corner, a master count
/// that is not one. Arranging a container of placeholder windows asks the same code
/// the desktop is arranged by, so the thumbnail is right by construction and a
/// layout added to the window manager is drawn by the bar the day it exists.
/// </para>
/// <para>
/// Arranged at the thumbnail's own pixel size, not at a large virtual one. The
/// layouts distribute space with cumulative rounding so that tiles meet exactly with
/// the gap and nothing else between them, and at sixteen pixels that care is the
/// whole difference between a crisp diagram and a smeared one; mapping a
/// thousand-pixel arrangement down would round a second time and lose it. The result
/// is returned in the unit square so the painter can scale it to the display.
/// </para>
/// <para>
/// Nothing here touches a window. The placeholders are nodes with a handle of one,
/// two, three and a blank identity; they exist for the length of the call and the
/// bar learns the layout's name from the window manager as it always did.
/// </para>
/// </remarks>
public static class LayoutThumbnail
{
    /// <summary>The most panes a thumbnail draws; past this the picture is noise at bar size.</summary>
    public const int MaxPanes = 9;

    /// <summary>A placeholder window's identity. The layouts never read it.</summary>
    private static readonly WindowIdentity Placeholder = new()
    {
        ProcessName = "taj",
        ClassName = "LayoutThumbnail",
        Title = string.Empty,
    };

    /// <summary>
    /// The panes of a layout with so many windows, as fractions of a box of the given
    /// size, or null when the window manager has no layout of that name.
    /// </summary>
    /// <param name="layout">The layout's name, or one of its aliases, as the window manager spells it.</param>
    /// <param name="panes">How many windows to draw, clamped to 1..<see cref="MaxPanes"/>.</param>
    /// <param name="width">The box's width in pixels.</param>
    /// <param name="height">The box's height in pixels.</param>
    /// <param name="gap">The space between panes in pixels.</param>
    /// <remarks>
    /// A layout whose panes overlap - monocle - comes back as one pane filling the
    /// box: drawing every window's full rectangle on top of the last would compound
    /// a translucent colour and say nothing a single one does not.
    /// </remarks>
    public static UnitRect[]? Panes(string? layout, int panes, int width, int height, int gap)
    {
        if (string.IsNullOrEmpty(layout) || !LayoutRegistry.TryResolve(layout, out ILayout? engine)) return null;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        panes = Math.Clamp(panes, 1, MaxPanes);
        gap = Math.Max(0, gap);

        if (engine.Overlaps) return [new UnitRect(0, 0, 1, 1)];

        var container = new ContainerNode(engine);
        for (int i = 0; i < panes; i++) container.Add(new WindowNode(i + 1, Placeholder));

        Span<Rect> rects = stackalloc Rect[panes];

        // No floor on a tile's size: the engine's default of 24 pixels is larger than
        // the whole box, and a pane too thin to see is the truthful picture of a
        // workspace divided that finely.
        engine.Arrange(container, new Rect(0, 0, width, height), new LayoutOptions(InnerGap: gap, MinimumTileExtent: 0), rects);

        var result = new UnitRect[panes];

        for (int i = 0; i < panes; i++)
        {
            Rect rect = rects[i];

            result[i] = new UnitRect(
                rect.X / (double)width,
                rect.Y / (double)height,
                rect.Width / (double)width,
                rect.Height / (double)height);
        }

        return result;
    }

    /// <summary>Whether the window manager has a layout of this name.</summary>
    public static bool Knows(string? layout) =>
        !string.IsNullOrEmpty(layout) && LayoutRegistry.TryResolve(layout, out _);
}
