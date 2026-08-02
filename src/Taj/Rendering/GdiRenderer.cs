using System.Runtime.InteropServices;
using Shubbak.Core.Geometry;
using Taj.Core.Layout;
using Taj.Core.Rendering;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Taj.Rendering;

/// <summary>
/// A GDI implementation of <see cref="ITajRenderer"/>.
/// </summary>
/// <remarks>
/// <para>
/// GDI rather than Direct2D. A bar draws filled rectangles, borders and text; GDI
/// does all three with no COM interop, no device-lost handling and no swap chain,
/// and the whole renderer fits in one file. Direct2D would give better text
/// rendering and cheap effects - and the <see cref="ITajRenderer"/> seam exists so
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
public sealed class GdiRenderer : ITajRenderer
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
    /// <para>
    /// Measured with <c>DT_CALCRECT</c> rather than <c>GetTextExtentPoint32</c>, because
    /// only the former agrees with what is actually drawn. <c>GetTextExtentPoint32</c>
    /// consults the selected font alone, while <c>DrawText</c> quietly borrows a glyph
    /// from another font when the selected one has none.
    /// </para>
    /// <para>
    /// So a character the font lacks was measured at the width of the missing-glyph box
    /// and then drawn several pixels wider - and since the text is drawn with
    /// <c>DT_END_ELLIPSIS</c> into the width that was measured, the glyph was cut off.
    /// Six of the eleven layout icons have no glyph in Segoe UI Variable Text, and none
    /// of them do in Segoe UI, so the layout indicator was a clipped smear rather than
    /// a symbol. Any template holding an unusual character had the same fault.
    /// </para>
    /// </remarks>
    public Size Measure(string text, FontStyle font)
    {
        if (string.IsNullOrEmpty(text)) return Size.Empty;

        HFONT handle = GetFont(font);
        HGDIOBJ previous = PInvoke.SelectObject(_measureDc, handle);

        try
        {
            var native = new RECT { left = 0, top = 0, right = 0, bottom = 0 };

            unsafe
            {
                fixed (char* p = text)
                {
                    // Same flags as DrawText, minus the ones that need a real rectangle.
                    int height = PInvoke.DrawText(
                        _measureDc,
                        p,
                        text.Length,
                        ref native,
                        DRAW_TEXT_FORMAT.DT_CALCRECT |
                        DRAW_TEXT_FORMAT.DT_SINGLELINE |
                        DRAW_TEXT_FORMAT.DT_LEFT |
                        DRAW_TEXT_FORMAT.DT_NOPREFIX);

                    if (height != 0)
                        return new Size(native.right - native.left, native.bottom - native.top);
                }
            }

            // Still worth asking: DT_CALCRECT fails on some device contexts where the
            // simpler call succeeds, and a slightly narrow answer beats none.
            if (PInvoke.GetTextExtentPoint32W(_measureDc, text, text.Length, out SIZE size))
                return new Size(size.cx, size.cy);
        }
        finally
        {
            PInvoke.SelectObject(_measureDc, previous);
        }

        // A rough fallback beats returning zero, which would collapse the layout.
        return new Size((int)(text.Length * font.Size * 0.6), (int)(font.Size * 1.4));
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
                    PInvoke.DrawText(
                        _memoryDc,
                        p,
                        text.Length,
                        ref native,
                        DRAW_TEXT_FORMAT.DT_SINGLELINE |
                        DRAW_TEXT_FORMAT.DT_VCENTER |
                        DRAW_TEXT_FORMAT.DT_LEFT |
                        DRAW_TEXT_FORMAT.DT_NOPREFIX |
                        DRAW_TEXT_FORMAT.DT_END_ELLIPSIS);
                }
            }
        }
        finally
        {
            PInvoke.SelectObject(_memoryDc, previousFont);
        }
    }

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

    [DllImport("GDI32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern HFONT CreateFontRaw(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        string pszFaceName);

    [DllImport("USER32.dll", EntryPoint = "FillRect", ExactSpelling = true)]
    private static extern int FillRectRaw(HDC hDC, in RECT lprc, HBRUSH hbr);

    private HFONT GetFont(FontStyle font)
    {
        string family = string.IsNullOrEmpty(font.Family) ? "Segoe UI" : font.Family;
        int size = (int)Math.Round(font.Size);

        var key = (family, size, font.Bold, font.Italic);
        if (_fonts.TryGetValue(key, out HFONT cached)) return cached;

        // Negative height asks for a character height rather than a cell height,
        // which is what font sizes elsewhere mean.
        // The SafeHandle-returning overload would dispose the font at collection
        // time; these are cached for the process lifetime and released in Dispose.
        HFONT handle = CreateFontRaw(
            -size, 0, 0, 0,
            font.Bold ? 600 : 400,
            font.Italic ? 1u : 0u, 0, 0,
            (uint)FONT_CHARSET.DEFAULT_CHARSET,
            (uint)FONT_OUTPUT_PRECISION.OUT_TT_PRECIS,
            (uint)FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
            (uint)FONT_QUALITY.CLEARTYPE_QUALITY,
            0,
            family);

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
        // Alpha is resolved by blending against the bar''s own background rather than
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

        foreach (HBRUSH brush in _brushes.Values) PInvoke.DeleteObject(brush);
        _brushes.Clear();

        foreach (HFONT font in _fonts.Values) PInvoke.DeleteObject(font);
        _fonts.Clear();

        if (!_measureDc.IsNull) PInvoke.DeleteDC(_measureDc);
    }
}
