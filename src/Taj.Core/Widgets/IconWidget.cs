using System.Text.Json;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Rendering;
using Shubbak.Ipc;
using Shubbak.Ui.Layout;

namespace Taj.Core.Widgets;

/// <summary>
/// A picture from a source: the focused window's icon, as the window manager sends
/// it.
/// </summary>
/// <remarks>
/// <para>
/// The bar never inspects windows, and this does not start: the icon comes over the
/// pipe like the title does, as the JSON of a <see cref="WindowIcon"/> in a source
/// value, and the widget only decodes it. Any source that publishes that shape would
/// draw - a script that reads an icon file and prints one would, though nobody has
/// needed to.
/// </para>
/// <para>
/// Decoded once per distinct value and kept. The tree is rebuilt on every clock tick,
/// and the value is a few kilobytes of base64 that has not changed since the last
/// tick; decoding it each time would be measurable for no gain. The comparison is by
/// reference and then by content, so the common case costs a pointer compare.
/// </para>
/// <para>
/// Hidden when the value is empty, exactly as a text widget with nothing to say is:
/// a window without an icon leaves no gap, and neither does a desktop with no window
/// focused.
/// </para>
/// </remarks>
public sealed class IconWidget : IWidget
{
    private string? _lastValue;
    private ImageBitmap? _lastImage;

    /// <param name="id">The widget's id, for styling and hit testing.</param>
    /// <param name="source">The source whose value is the icon.</param>
    /// <param name="size">The square the icon is drawn in, in pixels.</param>
    /// <param name="style">Background and corners, for a pill behind the icon.</param>
    /// <param name="box">Padding around the picture, inside the pill.</param>
    public IconWidget(string id, string source, int size, VisualStyle style, BoxStyle box = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        Id = id;
        Source = source;
        Size = size;
        Style = style;
        Box = box;
        Dependencies = [source];
    }

    public string Id { get; }

    /// <summary>The source read: <c>window.icon</c> unless config says otherwise.</summary>
    public string Source { get; }

    /// <summary>The side of the square the picture is drawn in.</summary>
    public int Size { get; }

    public IReadOnlyList<string> Dependencies { get; }

    public VisualStyle Style { get; set; }

    public BoxStyle Box { get; set; }

    /// <summary>Command performed when clicked, if any; the same as a text widget's.</summary>
    public string? OnClick { get; set; }

    /// <summary>Colours while the pointer is over it, when it is clickable and they were written.</summary>
    public VisualStyle? HoverStyle { get; set; }

    public VisualNode Build(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        string? value = values.GetValueOrDefault(Source);
        ImageBitmap? image = Decode(value);

        return new VisualNode
        {
            Id = Id,
            Kind = VisualKind.Image,
            Image = image,
            Style = Style,
            Box = Box with
            {
                Width = Size + Box.Padding.Horizontal,
                Height = Size + Box.Padding.Vertical,
            },
            Visible = image is not null,
            OnClick = OnClick,
            HoverStyle = OnClick is { Length: > 0 } ? Hovered() : null,
        };
    }

    /// <summary>
    /// The picture in a value, or null when there is none - decoded once per value.
    /// </summary>
    private ImageBitmap? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        if (ReferenceEquals(value, _lastValue) || string.Equals(value, _lastValue, StringComparison.Ordinal))
            return _lastImage;

        ImageBitmap? image = Parse(value);

        _lastValue = value;
        _lastImage = image;

        return image;
    }

    /// <summary>A <see cref="WindowIcon"/>'s JSON as a bitmap, or null if it is not one.</summary>
    public static ImageBitmap? Parse(string json)
    {
        try
        {
            WindowIcon? icon = JsonSerializer.Deserialize(json, IpcJsonContext.Default.WindowIcon);
            if (icon?.Decode() is not { } bytes) return null;

            return ImageBitmap.FromBgra(icon.Width, icon.Height, bytes);
        }
        catch (JsonException ex)
        {
            Log.Warn(LogCategory.Ipc, $"malformed icon payload: {ex.Message}");
            return null;
        }
    }

    /// <summary>The same hover a text widget without a background gets: the faint pill.</summary>
    private VisualStyle Hovered() => Style with
    {
        Background = HoverStyle is { } written && !written.Background.IsTransparent
            ? written.Background
            : Style.Background.IsTransparent
                ? new Colour(0xFF, 0xFF, 0xFF, 0x1A)
                : Style.Background.Lerp(Colour.White, 0.2),
        CornerRadius = Math.Max(Style.CornerRadius, 4),
    };
}
