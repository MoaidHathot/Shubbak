using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Shubbak.Ui.Rendering;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Shubbak.Ui.Gdi;

/// <summary>
/// An <see cref="IRenderer"/> that composites with real alpha, for a window whose
/// pixels are allowed to be see-through.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GdiRenderer"/> cannot do this, and the reason is worth stating: GDI has
/// no alpha channel. It flattens a translucent colour onto the bar's background, and
/// every fill and every glyph it draws writes a zero into the fourth byte of a 32-bit
/// surface. On an opaque window nobody looks at that byte. On a window the compositor
/// has been told to respect it on, a zero alpha is a hole - the text would be a
/// window onto the desktop, in the shape of the letters.
/// </para>
/// <para>
/// So this renderer owns its pixels. The back buffer is a 32-bit top-down DIB in
/// premultiplied BGRA, which is what the compositor reads. Fills and outlines are
/// written directly, source-over, with anti-aliased corners - GDI's <c>RoundRect</c>
/// was never anti-aliased, so rounded pills look better here than they did. Text
/// still comes from GDI, because GDI is the thing that knows how to shape and hint a
/// font: each run is drawn white on black into a scratch surface, which turns GDI's
/// output into a coverage mask, and the mask is then used to blend the wanted colour -
/// alpha and all - into the frame. Grayscale anti-aliasing rather than ClearType:
/// ClearType's colour fringes assume an opaque background of known colour, which a
/// translucent bar does not have.
/// </para>
/// <para>
/// An opaque background comes out with every alpha at 255, so the same renderer
/// serves a solid bar and a translucent one; nothing has to switch paths when a
/// profile does. The frame is presented with one <c>BitBlt</c>, exactly as before,
/// because a blit between two 32-bit surfaces carries the fourth byte across
/// untouched.
/// </para>
/// <para>
/// Handles are cached by description, because creating a font per draw call is the
/// classic way to make GDI slow, and a bar redraws its whole tree whenever any value
/// changes. The measuring device context is separate because layout runs before
/// <see cref="BeginFrame"/>.
/// </para>
/// </remarks>
public sealed unsafe class CompositedGdiRenderer : IRenderer, IImageRenderer
{
    private readonly HWND _window;
    private readonly HDC _measureDc;
    private readonly Dictionary<(string Family, int Size, bool Bold, bool Italic), HFONT> _fonts = [];

    /// <summary>The frame being built, premultiplied BGRA.</summary>
    private Surface _frame;

    /// <summary>Where GDI draws text, white on black, to be read back as coverage.</summary>
    private Surface _glyphs;

    private HDC _windowDc;
    private Rect _bounds;
    private bool _disposed;

    public CompositedGdiRenderer(nint windowHandle)
    {
        _window = new HWND(windowHandle);
        _measureDc = PInvoke.CreateCompatibleDC(HDC.Null);
    }

    // ---- measurement -------------------------------------------------------

    /// <inheritdoc cref="GdiText.Measure"/>
    public Size Measure(string text, FontStyle font)
    {
        if (string.IsNullOrEmpty(text)) return Size.Empty;

        return GdiText.Measure(_measureDc, GetFont(font), text, font);
    }

    // ---- frame -------------------------------------------------------------

    public void BeginFrame(Rect bounds, Colour background)
    {
        _bounds = bounds;

        // The surfaces first, the window's DC after. CreateDIBSection can fail - out of
        // GDI handles, a window mid-destruction - and a DC taken before the throw was a
        // DC never released, once per attempted frame, until the process hit the handle
        // limit for real.
        EnsureSurfaces(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));

        _windowDc = PInvoke.GetDC(_window);

        // A translucent clear is a translucent bar; a transparent one leaves the
        // compositor's backdrop, or the desktop, showing wherever nothing is drawn.
        new Span<uint>(_frame.Bits, _frame.Width * _frame.Height).Fill(Premultiply(background, 1.0));
    }

    public void EndFrame()
    {
        if (!_frame.Dc.IsNull && !_windowDc.IsNull)
        {
            // One blit, so the frame appears whole rather than being assembled on
            // screen in front of the user. SRCCOPY between two 32-bit surfaces moves
            // the alpha byte along with the colour, which is the whole point.
            PInvoke.BitBlt(
                _windowDc, 0, 0, _frame.Width, _frame.Height,
                _frame.Dc, 0, 0, ROP_CODE.SRCCOPY);
        }

        if (!_windowDc.IsNull)
        {
            _ = PInvoke.ReleaseDC(_window, _windowDc);
            _windowDc = HDC.Null;
        }
    }

    // ---- primitives --------------------------------------------------------

    public void FillRectangle(Rect rect, Colour colour, int cornerRadius = 0)
    {
        if (colour.IsTransparent || rect.IsEmpty || _frame.Bits is null) return;

        Rect local = ToLocal(rect);
        Rect clip = local.Intersect(SurfaceRect);
        if (clip.IsEmpty) return;

        int radius = ClampRadius(local, cornerRadius);

        for (int y = clip.Top; y < clip.Bottom; y++)
        {
            uint* row = _frame.Bits + (y * _frame.Width);

            for (int x = clip.Left; x < clip.Right; x++)
            {
                double coverage = radius > 0 ? RoundedCoverage(local, radius, x, y) : 1.0;
                if (coverage > 0) Blend(ref row[x], colour, coverage);
            }
        }
    }

    public void DrawRectangle(Rect rect, Colour colour, int thickness, int cornerRadius = 0)
    {
        if (colour.IsTransparent || thickness <= 0 || rect.IsEmpty || _frame.Bits is null) return;

        Rect local = ToLocal(rect);
        Rect clip = local.Intersect(SurfaceRect);
        if (clip.IsEmpty) return;

        int radius = ClampRadius(local, cornerRadius);

        // The stroke is the outer shape less the inner one. Subtracting coverages
        // rather than drawing two shapes keeps the inside edge anti-aliased too.
        Rect inner = local.Deflate(thickness);
        int innerRadius = Math.Max(0, radius - thickness);

        for (int y = clip.Top; y < clip.Bottom; y++)
        {
            uint* row = _frame.Bits + (y * _frame.Width);

            for (int x = clip.Left; x < clip.Right; x++)
            {
                double outer = radius > 0 ? RoundedCoverage(local, radius, x, y) : 1.0;
                if (outer <= 0) continue;

                double hole = inner.IsEmpty || !inner.Contains(x, y)
                    ? 0
                    : innerRadius > 0 ? RoundedCoverage(inner, innerRadius, x, y) : 1.0;

                double coverage = outer - hole;
                if (coverage > 0) Blend(ref row[x], colour, coverage);
            }
        }
    }

    public void DrawText(string text, Rect rect, Colour colour, FontStyle font)
    {
        if (string.IsNullOrEmpty(text) || rect.IsEmpty || colour.IsTransparent || _glyphs.Bits is null) return;

        Rect local = ToLocal(rect);
        Rect clip = local.Intersect(SurfaceRect);
        if (clip.IsEmpty) return;

        // Only the rows the text can land on are cleared; GDI clips to the rectangle
        // it is given, so nothing outside them changes.
        for (int y = clip.Top; y < clip.Bottom; y++)
            new Span<uint>(_glyphs.Bits + (y * _glyphs.Width) + clip.Left, clip.Width).Clear();

        HGDIOBJ previousFont = PInvoke.SelectObject(_glyphs.Dc, GetFont(font));

        try
        {
            // The full local rectangle rather than the clipped one, so vertical
            // centring and the ellipsis see the room the layout actually gave.
            var native = new RECT
            {
                left = local.Left,
                top = local.Top,
                right = local.Right,
                bottom = local.Bottom,
            };

            fixed (char* p = text)
            {
                PInvoke.DrawText(_glyphs.Dc, p, text.Length, ref native, GdiText.DrawFlags);
            }
        }
        finally
        {
            PInvoke.SelectObject(_glyphs.Dc, previousFont);
        }

        // GDI batches drawing calls per thread. Reading the bits back before the
        // batch is flushed reads whatever was there before.
        PInvoke.GdiFlush();

        for (int y = clip.Top; y < clip.Bottom; y++)
        {
            uint* mask = _glyphs.Bits + (y * _glyphs.Width);
            uint* row = _frame.Bits + (y * _frame.Width);

            for (int x = clip.Left; x < clip.Right; x++)
            {
                // White on black, grayscale-smoothed: any channel is the coverage.
                int coverage = (int)(mask[x] & 0xFF);
                if (coverage == 0) continue;

                Blend(ref row[x], colour, coverage / 255.0);
            }
        }
    }

    // ---- images ------------------------------------------------------------

    /// <summary>
    /// Draws a bitmap scaled to the rectangle, blended over the frame.
    /// </summary>
    /// <remarks>
    /// Resampled by <see cref="Pixels.Scale"/> - area-averaged, so a 32-pixel icon
    /// drawn at 20 keeps its edges - and then composited pixel by pixel, premultiplied
    /// throughout. A pixel copy when the sizes already match, which is the case a bar
    /// meets on every repaint once its icon is the size it asked for.
    /// </remarks>
    public void DrawImage(ImageBitmap image, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (rect.IsEmpty || _frame.Bits is null) return;

        Rect local = ToLocal(rect);
        Rect clip = local.Intersect(SurfaceRect);
        if (clip.IsEmpty) return;

        uint[] scaled = Pixels.Scale(image, local.Width, local.Height);

        for (int y = clip.Top; y < clip.Bottom; y++)
        {
            uint* row = _frame.Bits + (y * _frame.Width);
            int sourceRow = (y - local.Top) * local.Width;

            for (int x = clip.Left; x < clip.Right; x++)
                BlendPremultiplied(ref row[x], scaled[sourceRow + (x - local.Left)]);
        }
    }

    /// <summary>Source-over of one premultiplied pixel onto another.</summary>
    private static void BlendPremultiplied(ref uint destination, uint source)
    {
        int alpha = (int)(source >> 24);
        if (alpha <= 0) return;

        if (alpha >= 255)
        {
            destination = source;
            return;
        }

        int inverse = 255 - alpha;
        uint d = destination;

        int b = (int)(source & 0xFF) + Scale((int)(d & 0xFF), inverse);
        int g = (int)((source >> 8) & 0xFF) + Scale((int)((d >> 8) & 0xFF), inverse);
        int r = (int)((source >> 16) & 0xFF) + Scale((int)((d >> 16) & 0xFF), inverse);
        int a = alpha + Scale((int)(d >> 24), inverse);

        destination = Pack(r, g, b, a);
    }

    // ---- compositing -------------------------------------------------------

    /// <summary>Source-over, in premultiplied 8-bit arithmetic.</summary>
    private static void Blend(ref uint destination, Colour colour, double coverage)
    {
        int alpha = (int)Math.Round(colour.A * Math.Clamp(coverage, 0, 1));
        if (alpha <= 0) return;

        if (alpha >= 255)
        {
            destination = Pack(colour.R, colour.G, colour.B, 255);
            return;
        }

        int inverse = 255 - alpha;
        uint d = destination;

        int b = Scale(colour.B, alpha) + Scale((int)(d & 0xFF), inverse);
        int g = Scale(colour.G, alpha) + Scale((int)((d >> 8) & 0xFF), inverse);
        int r = Scale(colour.R, alpha) + Scale((int)((d >> 16) & 0xFF), inverse);
        int a = alpha + Scale((int)(d >> 24), inverse);

        destination = Pack(r, g, b, a);
    }

    /// <summary><c>value * factor / 255</c>, rounded.</summary>
    private static int Scale(int value, int factor) => ((value * factor) + 127) / 255;

    private static uint Pack(int r, int g, int b, int a) =>
        ((uint)Math.Min(a, 255) << 24) |
        ((uint)Math.Min(r, 255) << 16) |
        ((uint)Math.Min(g, 255) << 8) |
        (uint)Math.Min(b, 255);

    /// <summary>A colour as one premultiplied pixel, scaled by an extra opacity.</summary>
    private static uint Premultiply(Colour colour, double opacity)
    {
        int alpha = (int)Math.Round(colour.A * Math.Clamp(opacity, 0, 1));
        if (alpha <= 0) return 0;

        return Pack(Scale(colour.R, alpha), Scale(colour.G, alpha), Scale(colour.B, alpha), alpha);
    }

    /// <summary>
    /// How much of a pixel lies inside a rounded rectangle, 0 to 1.
    /// </summary>
    /// <remarks>
    /// Exact along the straight edges - the rectangle is pixel-aligned - and a
    /// one-pixel ramp on the distance to the corner circle elsewhere, which is the
    /// usual approximation and indistinguishable from the real area at bar sizes.
    /// </remarks>
    private static double RoundedCoverage(Rect rect, int radius, int x, int y)
    {
        double px = x + 0.5;
        double py = y + 0.5;

        // The centre of whichever corner circle this pixel is nearest, or nothing
        // if the pixel is in the straight part of the shape.
        double cx = px < rect.Left + radius ? rect.Left + radius
            : px > rect.Right - radius ? rect.Right - radius
            : px;

        double cy = py < rect.Top + radius ? rect.Top + radius
            : py > rect.Bottom - radius ? rect.Bottom - radius
            : py;

        if (cx == px || cy == py) return 1.0;

        double distance = Math.Sqrt(((px - cx) * (px - cx)) + ((py - cy) * (py - cy)));

        return Math.Clamp(radius + 0.5 - distance, 0, 1);
    }

    /// <summary>A radius no larger than the shape can carry.</summary>
    private static int ClampRadius(Rect rect, int radius) =>
        Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);

    private Rect SurfaceRect => new(0, 0, _frame.Width, _frame.Height);

    private Rect ToLocal(Rect rect) => rect.Translate(-_bounds.X, -_bounds.Y);

    // ---- resources ---------------------------------------------------------

    /// <summary>A 32-bit DIB with a device context of its own, and its pixels.</summary>
    private struct Surface
    {
        public HDC Dc;
        public HBITMAP Bitmap;
        public HGDIOBJ Previous;
        public uint* Bits;
        public int Width;
        public int Height;
    }

    private void EnsureSurfaces(int width, int height)
    {
        // Both surfaces, or neither: the check is on the frame alone, so a glyph
        // surface that failed after the frame succeeded would never be tried again and
        // every text call would return early - a bar with backgrounds and no words, at
        // that size, for good.
        if (_frame.Bits is not null && _glyphs.Bits is not null && _frame.Width == width && _frame.Height == height) return;

        Release(ref _frame);
        Release(ref _glyphs);

        _frame = CreateSurface(width, height);

        try
        {
            _glyphs = CreateSurface(width, height);
        }
        catch
        {
            Release(ref _frame);
            throw;
        }

        // Set once: text is always drawn white on black, and the mode is a property
        // of the device context rather than of the call.
        PInvoke.SetBkMode(_glyphs.Dc, BACKGROUND_MODE.TRANSPARENT);
        PInvoke.SetTextColor(_glyphs.Dc, new COLORREF(0x00FFFFFF));
    }

    /// <summary>
    /// A top-down 32-bit DIB section. Top-down so that row <c>y</c> is at offset
    /// <c>y * width</c>, the way every loop here reads it.
    /// </summary>
    private static Surface CreateSurface(int width, int height)
    {
        var info = new BITMAPINFO();
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;

        void* bits = null;
        HBITMAP bitmap = PInvoke.CreateDIBSection(HDC.Null, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);

        if (bitmap.IsNull || bits is null)
            throw new InvalidOperationException("CreateDIBSection failed");

        HDC dc = PInvoke.CreateCompatibleDC(HDC.Null);
        HGDIOBJ previous = PInvoke.SelectObject(dc, bitmap);

        return new Surface
        {
            Dc = dc,
            Bitmap = bitmap,
            Previous = previous,
            Bits = (uint*)bits,
            Width = width,
            Height = height,
        };
    }

    private static void Release(ref Surface surface)
    {
        if (!surface.Dc.IsNull)
        {
            if (!surface.Previous.IsNull) PInvoke.SelectObject(surface.Dc, surface.Previous);
            PInvoke.DeleteDC(surface.Dc);
        }

        if (!surface.Bitmap.IsNull) PInvoke.DeleteObject(surface.Bitmap);

        surface = default;
    }

    private HFONT GetFont(FontStyle font)
    {
        (string Family, int Size, bool Bold, bool Italic) key = GdiText.Key(font);
        if (_fonts.TryGetValue(key, out HFONT cached)) return cached;

        // Grayscale smoothing: the coverage read back has to be one number per
        // pixel, and ClearType's per-channel fringes are wrong over anything that is
        // not an opaque, known background. The metrics are the same either way, so
        // measuring in this font agrees with drawing in it.
        HFONT handle = GdiText.CreateFont(font, FONT_QUALITY.ANTIALIASED_QUALITY);

        _fonts[key] = handle;
        return handle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Release(ref _frame);
        Release(ref _glyphs);

        foreach (HFONT font in _fonts.Values) PInvoke.DeleteObject(font);
        _fonts.Clear();

        if (!_measureDc.IsNull) PInvoke.DeleteDC(_measureDc);
    }
}
