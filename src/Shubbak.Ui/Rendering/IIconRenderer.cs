using Shubbak.Core.Geometry;

namespace Shubbak.Ui.Rendering;

/// <summary>
/// Draws an icon that already exists somewhere else.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not part of <see cref="IRenderer"/>. That interface is small on
/// purpose - filled rectangles, borders and text - and every member added to it is a
/// member every renderer must implement whether or not it has anything to draw with.
/// The bar has no use for this and should not have to grow a method to say so.
/// </para>
/// <para>
/// A capability instead: a host that wants icons asks whether its renderer is one of
/// these, and draws without them when it is not. That keeps the seam honest - the
/// palette degrades to what it looked like before rather than failing - and keeps a
/// Win32 handle out of the interface that is supposed to make the drawing technology
/// replaceable.
/// </para>
/// <para>
/// The handle is <see cref="nint"/> rather than a typed one for the same reason:
/// <c>Shubbak.Ui</c> contains no Win32 and is not about to start.
/// </para>
/// </remarks>
public interface IIconRenderer
{
    /// <summary>
    /// Draws an icon, scaled to fit the rectangle.
    /// </summary>
    /// <remarks>
    /// The icon is not owned by the renderer and must not be destroyed by it. What is
    /// passed here is generally a class icon, which belongs to the application that
    /// registered the class and outlives anything this process does with it.
    /// </remarks>
    /// <param name="icon">A handle to the icon, or zero to draw nothing.</param>
    /// <param name="rect">Where to draw it. Square rectangles look best.</param>
    void DrawIcon(nint icon, Rect rect);
}
