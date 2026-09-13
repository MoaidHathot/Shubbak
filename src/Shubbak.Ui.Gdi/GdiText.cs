using System.Runtime.InteropServices;
using Shubbak.Ui.Layout;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Shubbak.Ui.Gdi;

/// <summary>
/// What the two GDI renderers share about text: making a font, and measuring in it.
/// </summary>
/// <remarks>
/// Split out when the composited renderer arrived rather than written twice. The
/// measurement in particular carries a lesson that was learned the hard way, and a
/// second copy of it would be a second place for the lesson to be lost.
/// </remarks>
internal static class GdiText
{
    /// <summary>The flags text is drawn with, so measuring can use the same ones.</summary>
    /// <remarks>
    /// Clipped and single-line: the layout engine has already decided how much room
    /// this text gets, and letting it spill over a neighbour would be worse than
    /// cutting it off.
    /// </remarks>
    public const DRAW_TEXT_FORMAT DrawFlags =
        DRAW_TEXT_FORMAT.DT_SINGLELINE |
        DRAW_TEXT_FORMAT.DT_VCENTER |
        DRAW_TEXT_FORMAT.DT_LEFT |
        DRAW_TEXT_FORMAT.DT_NOPREFIX |
        DRAW_TEXT_FORMAT.DT_END_ELLIPSIS;

    [DllImport("GDI32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern HFONT CreateFontRaw(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        string pszFaceName);

    /// <summary>Creates a font. The caller owns the handle.</summary>
    /// <remarks>
    /// Negative height asks for a character height rather than a cell height, which
    /// is what font sizes elsewhere mean. The SafeHandle-returning overload would
    /// dispose the font at collection time; callers cache these for the life of the
    /// renderer and release them in Dispose.
    /// </remarks>
    public static HFONT CreateFont(FontStyle font, FONT_QUALITY quality)
    {
        string family = string.IsNullOrEmpty(font.Family) ? "Segoe UI" : font.Family;
        int size = (int)Math.Round(font.Size);

        return CreateFontRaw(
            -size, 0, 0, 0,
            font.Bold ? 600 : 400,
            font.Italic ? 1u : 0u, 0, 0,
            (uint)FONT_CHARSET.DEFAULT_CHARSET,
            (uint)FONT_OUTPUT_PRECISION.OUT_TT_PRECIS,
            (uint)FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
            (uint)quality,
            0,
            family);
    }

    /// <summary>The key a font cache uses: what distinguishes one handle from another.</summary>
    public static (string Family, int Size, bool Bold, bool Italic) Key(FontStyle font) =>
        (string.IsNullOrEmpty(font.Family) ? "Segoe UI" : font.Family, (int)Math.Round(font.Size), font.Bold, font.Italic);

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
    /// <param name="dc">A device context to measure in; the font is selected and restored.</param>
    /// <param name="handle">The font, already created for <paramref name="font"/>.</param>
    /// <param name="text">What will be drawn.</param>
    /// <param name="font">The style the handle was made from, for the fallback estimate.</param>
    public static Size Measure(HDC dc, HFONT handle, string text, FontStyle font)
    {
        if (string.IsNullOrEmpty(text)) return Size.Empty;

        HGDIOBJ previous = PInvoke.SelectObject(dc, handle);

        try
        {
            var native = new RECT { left = 0, top = 0, right = 0, bottom = 0 };

            unsafe
            {
                fixed (char* p = text)
                {
                    // Same flags as DrawText, minus the ones that need a real rectangle.
                    int height = PInvoke.DrawText(
                        dc,
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
            if (PInvoke.GetTextExtentPoint32W(dc, text, text.Length, out SIZE size))
                return new Size(size.cx, size.cy);
        }
        finally
        {
            PInvoke.SelectObject(dc, previous);
        }

        // A rough fallback beats returning zero, which would collapse the layout.
        return new Size((int)(text.Length * font.Size * 0.6), (int)(font.Size * 1.4));
    }
}
