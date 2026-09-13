using Shubbak.Core.Geometry;
using Shubbak.Ui.Layout;

namespace Shubbak.Ui.Rendering;

/// <summary>
/// Draws a picture held in memory.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not part of <see cref="IRenderer"/>. That interface is small on
/// purpose - filled rectangles, borders and text - and every member added to it is a
/// member every renderer must implement whether or not it has anything to draw with.
/// A capability instead: the painter draws an <see cref="VisualKind.Image"/> node
/// through this when the renderer offers it, and draws the node's background and
/// border without it when the renderer does not, so a renderer that cannot composite
/// degrades to a gap rather than to a crash.
/// </para>
/// <para>
/// It takes pixels rather than a handle. A handle belongs to whoever made it and
/// means nothing across a pipe; pixels are the form an icon has after crossing one,
/// the form a renderer with its own alpha channel wants anyway, and the reason
/// <c>Shubbak.Ui</c> can stay free of Win32.
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
