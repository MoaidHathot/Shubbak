using System.Runtime.InteropServices;
using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Shubbak.Ui.Rendering;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Shubbak.Ui.Gdi;

/// <summary>
/// A GDI implementation of <see cref="IRenderer"/>.
/// </summary>
/// <remarks>
/// <para>
/// GDI rather than Direct2D. A bar draws filled rectangles, borders and text; GDI
/// does all three with no COM interop, no device-lost handling and no swap chain,
/// and the whole renderer fits in one file. Direct2D would give better text
/// rendering and cheap effects - and the <see cref="IRenderer"/> seam exists so
/// it can replace this without touching the sources, widgets or layout engine.
/// </para>
/// <para>
/// Drawing goes to an off-screen bitmap which is blitted in one operation at the end
/// of the frame. Painting straight to the window device context produces visible
/// tearing on every redraw, which on a bar that updates once a second is extremely
/// noticeable.
/// </para>
/// <para>
/// Handles are cached by description, because creating a font or a brush per draw
/// call is the classic way to make GDI slow, and a bar redraws its whole tree every
/// time any value changes.
/// </para>
/// </remarks>
public sealed class GdiRenderer : IRenderer, IImageRenderer
{
    private readonly HWND _window;

    private readonly Dictionary<uint, HBRUSH> _brushes = [];
    private readonly Dictionary<(string Family, int Size, bool Bold, bool Italic), HFONT> _fonts = [];

    private HDC _windowDc;
    private HDC _memoryDc;
    private HBITMAP _bitmap;
    private HGDIOBJ _previousBitmap;

    private Rect _bounds;
    private Colour _backdrop = new(0x1E, 0x1E, 0x2E);
    private int _bufferWidth;
    private int _bufferHeight;
    private bool _disposed;

    /// <summary>Device context used for measuring outside a frame.</summary>
    private readonly HDC _measureDc;

    public GdiRenderer(nint windowHandle)
    {
        _window = new HWND(windowHandle);

        // Measurement happens during layout, which runs before BeginFrame, so it
        // needs a device context of its own.
        _measureDc = PInvoke.CreateCompatibleDC(HDC.Null);
    }

    // ---- measurement -------------------------------------------------------

    /// <summary>How much room a string needs, in the font it will be drawn in.</summary>
    /// <remarks>
    /// The measuring itself, and the reason it is done with <c>DT_CALCRECT</c>, lives in
    /// <see cref="GdiText.Measure"/> so the composited renderer measures identically.
    /// </remarks>
    public Size Measure(string text, FontStyle font)
    {
        if (string.IsNullOrEmpty(text)) return Size.Empty;

        return GdiText.Measure(_measureDc, GetFont(font), text, font);
    }

    // ---- frame -------------------------------------------------------------

    public void BeginFrame(Rect bounds, Colour background)
    {
        _bounds = bounds;
        _backdrop = background;
        _windowDc = PInvoke.GetDC(_window);

        EnsureBuffer(bounds.Width, bounds.Height);

        var full = new RECT { left = 0, top = 0, right = bounds.Width, bottom = bounds.Height };
        _ = FillRectRaw(_memoryDc, in full, GetBrush(background));

        PInvoke.SetBkMode(_memoryDc, BACKGROUND_MODE.TRANSPARENT);
    }

    public void EndFrame()
    {
        if (!_memoryDc.IsNull && !_windowDc.IsNull)
        {
            // One blit, so the frame appears whole rather than being assembled on
            // screen in front of the user.
            PInvoke.BitBlt(
                _windowDc, 0, 0, _bounds.Width, _bounds.Height,
                _memoryDc, 0, 0, ROP_CODE.SRCCOPY);
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
        if (colour.IsTransparent || rect.IsEmpty) return;

        Rect local = ToLocal(rect);

        if (cornerRadius <= 0)
        {
            var native = new RECT
            {
                left = local.Left,
                top = local.Top,
                right = local.Right,
                bottom = local.Bottom,
            };

            _ = FillRectRaw(_memoryDc, in native, GetBrush(colour));
            return;
        }

        HBRUSH brush = GetBrush(colour);
        HGDIOBJ previousBrush = PInvoke.SelectObject(_memoryDc, brush);

        // A null pen leaves the rounded rectangle unoutlined; a border is drawn
        // separately when one is asked for.
        HGDIOBJ nullPen = PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.NULL_PEN);
        HGDIOBJ previousPen = PInvoke.SelectObject(_memoryDc, nullPen);

        try
        {
            int diameter = cornerRadius * 2;

            PInvoke.RoundRect(
                _memoryDc, local.Left, local.Top, local.Right + 1, local.Bottom + 1,
                diameter, diameter);
        }
        finally
        {
            PInvoke.SelectObject(_memoryDc, previousPen);
            PInvoke.SelectObject(_memoryDc, previousBrush);
        }
    }

    public void DrawRectangle(Rect rect, Colour colour, int thickness, int cornerRadius = 0)
    {
        if (colour.IsTransparent || thickness <= 0 || rect.IsEmpty) return;

        Rect local = ToLocal(rect);

        HPEN pen = PInvoke.CreatePen(PEN_STYLE.PS_SOLID, thickness, ToColorRef(colour));
        HGDIOBJ previousPen = PInvoke.SelectObject(_memoryDc, pen);

        HGDIOBJ hollow = PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.HOLLOW_BRUSH);
        HGDIOBJ previousBrush = PInvoke.SelectObject(_memoryDc, hollow);

        try
        {
            if (cornerRadius > 0)
            {
                int diameter = cornerRadius * 2;
                PInvoke.RoundRect(
                    _memoryDc, local.Left, local.Top, local.Right, local.Bottom, diameter, diameter);
            }
            else
            {
                PInvoke.Rectangle(_memoryDc, local.Left, local.Top, local.Right, local.Bottom);
            }
        }
        finally
        {
            PInvoke.SelectObject(_memoryDc, previousBrush);
            PInvoke.SelectObject(_memoryDc, previousPen);
            PInvoke.DeleteObject(pen);
        }
    }

    public void DrawText(string text, Rect rect, Colour colour, FontStyle font)
    {
        if (string.IsNullOrEmpty(text) || rect.IsEmpty) return;

        Rect local = ToLocal(rect);

        HFONT handle = GetFont(font);
        HGDIOBJ previousFont = PInvoke.SelectObject(_memoryDc, handle);

        PInvoke.SetTextColor(_memoryDc, ToColorRef(colour));

        try
        {
            var native = new RECT
            {
                left = local.Left,
                top = local.Top,
                right = local.Right,
                bottom = local.Bottom,
            };

            unsafe
            {
                fixed (char* p = text)
                {
                    // Clipped and single-line: the layout engine has already decided
                    // how much room this text gets, and letting it spill over a
                    // neighbour would be worse than cutting it off.
                    PInvoke.DrawText(_memoryDc, p, text.Length, ref native, GdiText.DrawFlags);
                }
            }
        }
        finally
        {
            PInvoke.SelectObject(_memoryDc, previousFont);
        }
    }

    // ---- images ------------------------------------------------------------

    /// <summary>
    /// Draws a bitmap into the back buffer, scaled to the rectangle and blended by its
    /// alpha.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The back buffer is an opaque device bitmap with no pixels to reach, so the
    /// blend is GDI's: <c>AlphaBlend</c> with <c>AC_SRC_ALPHA</c>, which expects exactly
    /// the premultiplied pixels an <see cref="ImageBitmap"/> holds. It is asked to
    /// stretch nothing - it does not filter, and a 32-pixel icon squeezed to 18 by it
    /// loses rows - so the picture is resampled first, by the same area-averaging the
    /// composited renderer uses, into a scratch DIB of the size it is drawn at.
    /// </para>
    /// <para>
    /// The scratch surface is kept between calls and remade only when the size changes:
    /// a list of rows draws its icons all at one size, and creating and destroying a
    /// section per icon per frame would be the most GDI work in the frame.
    /// </para>
    /// </remarks>
    public unsafe void DrawImage(ImageBitmap image, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (rect.IsEmpty || _memoryDc.IsNull) return;

        Rect local = ToLocal(rect);

        if (!EnsureScratch(local.Width, local.Height)) return;

        uint[] scaled = Pixels.Scale(image, local.Width, local.Height);
        scaled.AsSpan().CopyTo(new Span<uint>(_scratchBits, scaled.Length));

        // The batch has to reach the section before it is read as a source.
        PInvoke.GdiFlush();

        var blend = new BLENDFUNCTION
        {
            BlendOp = (byte)PInvoke.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
        };

        _ = PInvoke.AlphaBlend(
            _memoryDc, local.Left, local.Top, local.Width, local.Height,
            _scratchDc, 0, 0, local.Width, local.Height,
            blend);
    }

    /// <summary>A 32-bit DIB of the given size for images to be blended from.</summary>
    private unsafe bool EnsureScratch(int width, int height)
    {
        if (!_scratchBitmap.IsNull && _scratchWidth == width && _scratchHeight == height) return true;

        ReleaseScratch();

        var info = new BITMAPINFO();
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height;   // top-down, like the pixels it receives
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;

        void* bits = null;
        HBITMAP bitmap = PInvoke.CreateDIBSection(HDC.Null, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);

        if (bitmap.IsNull || bits is null) return false;

        _scratchDc = PInvoke.CreateCompatibleDC(HDC.Null);
        _scratchPrevious = PInvoke.SelectObject(_scratchDc, bitmap);
        _scratchBitmap = bitmap;
        _scratchBits = (uint*)bits;
        _scratchWidth = width;
        _scratchHeight = height;

        return true;
    }

    private void ReleaseScratch()
    {
        if (!_scratchDc.IsNull)
        {
            if (!_scratchPrevious.IsNull) PInvoke.SelectObject(_scratchDc, _scratchPrevious);
            PInvoke.DeleteDC(_scratchDc);
            _scratchDc = HDC.Null;
        }

        if (!_scratchBitmap.IsNull)
        {
            PInvoke.DeleteObject(_scratchBitmap);
            _scratchBitmap = HBITMAP.Null;
        }

        _scratchPrevious = HGDIOBJ.Null;
        unsafe { _scratchBits = null; }
        _scratchWidth = _scratchHeight = 0;
    }

    private HDC _scratchDc;
    private HBITMAP _scratchBitmap;
    private HGDIOBJ _scratchPrevious;
    private unsafe uint* _scratchBits;
    private int _scratchWidth;
    private int _scratchHeight;

    // ---- resources ---------------------------------------------------------

    private void EnsureBuffer(int width, int height)
    {
        if (!_bitmap.IsNull && _bufferWidth == width && _bufferHeight == height) return;

        ReleaseBuffer();

        _memoryDc = PInvoke.CreateCompatibleDC(_windowDc);
        _bitmap = PInvoke.CreateCompatibleBitmap(_windowDc, width, height);
        _previousBitmap = PInvoke.SelectObject(_memoryDc, _bitmap);

        _bufferWidth = width;
        _bufferHeight = height;
    }

    private void ReleaseBuffer()
    {
        if (!_memoryDc.IsNull)
        {
            if (!_previousBitmap.IsNull) PInvoke.SelectObject(_memoryDc, _previousBitmap);
            PInvoke.DeleteDC(_memoryDc);
            _memoryDc = HDC.Null;
        }

        if (!_bitmap.IsNull)
        {
            PInvoke.DeleteObject(_bitmap);
            _bitmap = HBITMAP.Null;
        }

        _previousBitmap = HGDIOBJ.Null;
    }

    /// <summary>
    /// A cached solid brush.
    /// </summary>
    /// <remarks>
    /// Creating one per draw call is the classic way to make GDI slow, and a bar
    /// redraws its whole tree whenever any value changes.
    /// </remarks>
    private HBRUSH GetBrush(Colour colour)
    {
        uint key = ToColorRef(colour).Value;

        if (_brushes.TryGetValue(key, out HBRUSH cached)) return cached;

        HBRUSH brush = PInvoke.CreateSolidBrush(new COLORREF(key));
        _brushes[key] = brush;

        return brush;
    }

    [DllImport("USER32.dll", EntryPoint = "FillRect", ExactSpelling = true)]
    private static extern int FillRectRaw(HDC hDC, in RECT lprc, HBRUSH hbr);

    private HFONT GetFont(FontStyle font)
    {
        (string Family, int Size, bool Bold, bool Italic) key = GdiText.Key(font);
        if (_fonts.TryGetValue(key, out HFONT cached)) return cached;

        // ClearType, because this renderer only ever draws onto an opaque surface,
        // which is the one place ClearType's colour fringes are correct.
        HFONT handle = GdiText.CreateFont(font, FONT_QUALITY.CLEARTYPE_QUALITY);

        _fonts[key] = handle;
        return handle;
    }

    /// <summary>
    /// Converts to GDI's byte order.
    /// </summary>
    /// <remarks>
    /// COLORREF is 0x00BBGGRR - blue first - whereas config, CSS and every designer
    /// write red first. Getting this backwards produces a bar that looks almost
    /// right, which is the hardest kind of wrong to notice.
    /// </remarks>
    private COLORREF ToColorRef(Colour colour)
    {
        // Alpha is resolved by blending against the bar's own background rather than
        // being discarded. GDI has no compositing, so a half-transparent colour used
        // to render fully opaque - which made `empty-colour` and the dimmed inactive
        // workspaces indistinguishable from the active ones despite the config
        // plainly asking for a difference.
        Colour flat = colour.A >= 255 ? colour : Blend(colour, _backdrop);

        return new((uint)(flat.R | (flat.G << 8) | (flat.B << 16)));
    }

    /// <summary>Flattens a translucent colour onto an opaque one.</summary>
    private static Colour Blend(Colour source, Colour backdrop)
    {
        int alpha = source.A;
        int inverse = 255 - alpha;

        return new Colour(
            (byte)(((source.R * alpha) + (backdrop.R * inverse)) / 255),
            (byte)(((source.G * alpha) + (backdrop.G * inverse)) / 255),
            (byte)(((source.B * alpha) + (backdrop.B * inverse)) / 255));
    }

    private Rect ToLocal(Rect rect) => rect.Translate(-_bounds.X, -_bounds.Y);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseBuffer();
        ReleaseScratch();

        foreach (HBRUSH brush in _brushes.Values) PInvoke.DeleteObject(brush);
        _brushes.Clear();

        foreach (HFONT font in _fonts.Values) PInvoke.DeleteObject(font);
        _fonts.Clear();

        if (!_measureDc.IsNull) PInvoke.DeleteDC(_measureDc);
    }
}
