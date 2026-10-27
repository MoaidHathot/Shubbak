using System.Buffers;
using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Layouts;

/// <summary>Convenience for callers arranging a complete container, such as previews.</summary>
public static class LayoutInput
{
    public static void Arrange(this ILayout layout, ContainerNode container, Rect area,
        in LayoutOptions options, Span<Rect> destination)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(container);
        int count = container.Count;
        double[]? rented = count > 128 ? ArrayPool<double>.Shared.Rent(count) : null;
        Span<double> weights = rented is null ? stackalloc double[count] : rented.AsSpan(0, count);
        try
        {
            for (int i = 0; i < count; i++) weights[i] = container.Children[i].SizeRatio;
            layout.Arrange(weights, area, in options, destination);
        }
        finally { if (rented is not null) ArrayPool<double>.Shared.Return(rented); }
    }
}
