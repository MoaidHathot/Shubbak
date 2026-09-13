using Shubbak.Core.Geometry;
using Shubbak.Ui.Layout;

namespace Shubbak.Ui.Rendering;

/// <summary>
/// Draws a picture held in memory.
/// </summary>
/// <remarks>
/// <para>
/// A capability rather than a member of <see cref="IRenderer"/>, for the reason
/// <see cref="IIconRenderer"/> gives: the core interface is small on purpose, and a
/// renderer that cannot composite should not have to grow a method to say so. The
/// painter draws an <see cref="VisualKind.Image"/> node through this when the
/// renderer offers it, and draws the node's background and border without it when
/// the renderer does not.
/// </para>
/// <para>
/// Where <see cref="IIconRenderer"/> takes a handle that belongs to somebody else,
/// this takes pixels that belong to nobody - the form an icon has after crossing the
/// pipe, and the form a renderer with its own alpha channel wants anyway.
/// </para>
/// </remarks>
public interface IImageRenderer
{
    /// <summary>
    /// Draws a bitmap scaled to fit the rectangle, blending its alpha over whatever is
    /// already there.
    /// </summary>
    /// <param name="image">The picture.</param>
    /// <param name="rect">Where to draw it. The aspect ratio is not preserved; callers size the box.</param>
    void DrawImage(ImageBitmap image, Rect rect);
}
