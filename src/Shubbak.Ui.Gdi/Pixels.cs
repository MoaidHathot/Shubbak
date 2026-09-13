using Shubbak.Ui.Layout;

namespace Shubbak.Ui.Gdi;

/// <summary>
/// Pixel arithmetic the two renderers share: resampling a bitmap to the size it is
/// drawn at.
/// </summary>
/// <remarks>
/// Each destination pixel takes the area-weighted average of the source pixels it
/// covers, which is the right filter for the case that actually occurs - a 32-pixel
/// icon drawn at 18, 20 or 27 - where nearest-neighbour drops rows and columns and
/// bilinear blurs. It degrades to bilinear-ish when enlarging, which nothing here has a
/// reason to do. Premultiplied all the way through, so an icon's transparent fringe
/// averages towards nothing rather than towards black.
/// </remarks>
internal static class Pixels
{
    /// <summary>The bitmap's pixels at another size, premultiplied <c>0xAARRGGBB</c>, top-down.</summary>
    public static uint[] Scale(ImageBitmap image, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        ReadOnlySpan<uint> source = image.Pixels;

        if (width == image.Width && height == image.Height) return source.ToArray();

        var result = new uint[width * height];

        double scaleX = (double)image.Width / width;
        double scaleY = (double)image.Height / height;

        for (int y = 0; y < height; y++)
        {
            double sy0 = y * scaleY;
            double sy1 = sy0 + scaleY;

            for (int x = 0; x < width; x++)
            {
                double sx0 = x * scaleX;
                result[(y * width) + x] = Average(source, image.Width, image.Height, sx0, sy0, sx0 + scaleX, sy1);
            }
        }

        return result;
    }

    /// <summary>The area-weighted mean of the source over a box in source coordinates.</summary>
    private static uint Average(ReadOnlySpan<uint> source, int width, int height, double x0, double y0, double x1, double y1)
    {
        double b = 0, g = 0, r = 0, a = 0, total = 0;

        int firstY = Math.Max(0, (int)Math.Floor(y0));
        int lastY = Math.Min(height - 1, (int)Math.Ceiling(y1) - 1);
        int firstX = Math.Max(0, (int)Math.Floor(x0));
        int lastX = Math.Min(width - 1, (int)Math.Ceiling(x1) - 1);

        for (int sy = firstY; sy <= lastY; sy++)
        {
            double weightY = Math.Min(sy + 1, y1) - Math.Max(sy, y0);
            if (weightY <= 0) continue;

            for (int sx = firstX; sx <= lastX; sx++)
            {
                double weightX = Math.Min(sx + 1, x1) - Math.Max(sx, x0);
                if (weightX <= 0) continue;

                double weight = weightX * weightY;
                uint p = source[(sy * width) + sx];

                b += (p & 0xFF) * weight;
                g += ((p >> 8) & 0xFF) * weight;
                r += ((p >> 16) & 0xFF) * weight;
                a += (p >> 24) * weight;
                total += weight;
            }
        }

        if (total <= 0) return 0;

        return Pack(
            (int)Math.Round(r / total),
            (int)Math.Round(g / total),
            (int)Math.Round(b / total),
            (int)Math.Round(a / total));
    }

    /// <summary>Four channels as one premultiplied <c>0xAARRGGBB</c> pixel, each clamped.</summary>
    public static uint Pack(int r, int g, int b, int a) =>
        ((uint)Math.Clamp(a, 0, 255) << 24) |
        ((uint)Math.Clamp(r, 0, 255) << 16) |
        ((uint)Math.Clamp(g, 0, 255) << 8) |
        (uint)Math.Clamp(b, 0, 255);
}
