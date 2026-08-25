using Dalil.Core;
using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Shubbak.Ui.Rendering;

namespace Dalil;

/// <summary>
/// Draws the palette.
/// </summary>
/// <remarks>
/// <para>
/// Immediate mode, and deliberately not through <c>FlexLayout</c>. The palette is a
/// fixed-height vertical list, so its layout is arithmetic - and the flex engine
/// measures every node it is given, where each measurement is a round trip into GDI.
/// Building a tree for two hundred windows to draw twelve of them would be the most
/// expensive thing the process does, on every keystroke.
/// </para>
/// <para>
/// So only the visible slice is touched, and the model works out which slice that is.
/// </para>
/// </remarks>
internal static class PaletteRenderer
{
    private const int Padding = 8;
    private const int TextInset = 12;

    public static void Draw(IRenderer renderer, PaletteModel model, DalilConfig config, Rect canvas)
    {
        var font = new FontStyle(config.FontFamily, config.FontSize, Bold: false, Italic: false);
        var small = new FontStyle(config.FontFamily, config.FontSize - 2, Bold: false, Italic: false);

        renderer.DrawRectangle(canvas, config.Border, 1, cornerRadius: 8);

        int y = canvas.Y + Padding;
        y = DrawSearchBox(renderer, model, config, canvas, font, y);

        (int first, int count) = model.VisibleWindow(config.VisibleRows);

        if (count == 0)
        {
            renderer.DrawText(
                model.Rows.Count == 0 && model.Term.Length > 0 ? "no matches" : "nothing to show",
                new Rect(canvas.X + TextInset, y + Padding, canvas.Width - (TextInset * 2), config.RowHeight),
                config.Secondary,
                small);

            return;
        }

        for (int i = 0; i < count; i++)
        {
            int index = first + i;
            DrawRow(renderer, model, config, canvas, font, small, index, y);
            y += config.RowHeight;
        }

        DrawScrollHint(renderer, model, config, canvas, small, first, count);
    }

    private static int DrawSearchBox(
        IRenderer renderer, PaletteModel model, DalilConfig config, Rect canvas, FontStyle font, int y)
    {
        var box = new Rect(canvas.X + Padding, y, canvas.Width - (Padding * 2), config.RowHeight);

        // The mode is shown as a word rather than left as the punctuation that
        // selected it. A bare ">" tells someone who has just pressed Tab nothing at
        // all about what they are now searching.
        string label = model.Mode switch
        {
            PaletteMode.Commands => "commands",
            PaletteMode.Workspaces => "workspaces",
            PaletteMode.Layouts => "layouts",
            _ => "windows",
        };

        renderer.DrawText(
            label,
            new Rect(box.X + 4, box.Y, 100, box.Height),
            config.Match,
            font);

        string typed = model.Term;

        renderer.DrawText(
            // A block for a caret. Drawing a real one means a timer, a blink and a
            // focus rule, for a field that is never not focused.
            typed.Length == 0 ? "\u2588" : typed + "\u2588",
            new Rect(box.X + 104, box.Y, box.Width - 108, box.Height),
            config.Foreground,
            font);

        renderer.FillRectangle(
            new Rect(canvas.X + Padding, box.Bottom, canvas.Width - (Padding * 2), 1),
            config.Border);

        return box.Bottom + 1;
    }

    private static void DrawRow(
        IRenderer renderer,
        PaletteModel model,
        DalilConfig config,
        Rect canvas,
        FontStyle font,
        FontStyle small,
        int index,
        int y)
    {
        PaletteRow row = model.Rows[index];
        bool selected = index == model.SelectedIndex;

        var bounds = new Rect(canvas.X + Padding, y, canvas.Width - (Padding * 2), config.RowHeight);

        if (selected) renderer.FillRectangle(bounds, config.SelectionBackground, cornerRadius: 4);

        int textY = bounds.Y + ((config.RowHeight - config.FontSize - 4) / 2);
        int x = bounds.X + TextInset;

        x = DrawHighlighted(renderer, config, font, row, x, textY, bounds.Right - 8);

        if (row.Entry.Secondary.Length > 0 && x < bounds.Right - 80)
        {
            renderer.DrawText(
                "  " + row.Entry.Secondary,
                new Rect(x, textY, bounds.Right - x - 8, config.FontSize + 6),
                config.Secondary,
                small);
        }

        DrawBadges(renderer, config, small, row, bounds, textY);
    }

    /// <summary>
    /// Draws the row's title, colouring the characters that matched.
    /// </summary>
    /// <remarks>
    /// One draw call per run rather than per character: a run is a maximal stretch of
    /// characters that are all matched or all not, so the common cases - a prefix, a
    /// contiguous substring - cost two or three calls rather than one per letter.
    /// <para>
    /// This is why the matcher returns positions at all. Highlighting is the only
    /// feedback that explains <em>why</em> a row is in the list, which matters most
    /// for the abbreviations that look like coincidences.
    /// </para>
    /// </remarks>
    private static int DrawHighlighted(
        IRenderer renderer, DalilConfig config, FontStyle font, PaletteRow row, int x, int y, int limit)
    {
        string text = row.Entry.Primary;
        IReadOnlyList<int> positions = row.Positions;

        if (positions.Count == 0)
        {
            Size whole = renderer.Measure(text, font);
            renderer.DrawText(text, new Rect(x, y, Math.Max(0, limit - x), whole.Height + 4), config.Foreground, font);
            return Math.Min(x + whole.Width, limit);
        }

        int at = 0;
        int next = 0;

        while (at < text.Length && x < limit)
        {
            bool matched = next < positions.Count && positions[next] == at;

            int run = 0;
            while (at + run < text.Length &&
                   (next + run < positions.Count && positions[next + run] == at + run) == matched)
            {
                run++;
                if (!matched && next < positions.Count && positions[next] == at + run) break;
            }

            string piece = text.Substring(at, run);
            Size size = renderer.Measure(piece, font);

            renderer.DrawText(
                piece,
                new Rect(x, y, Math.Max(0, limit - x), size.Height + 4),
                matched ? config.Match : config.Foreground,
                font);

            x += size.Width;
            at += run;
            if (matched) next += run;
        }

        return Math.Min(x, limit);
    }

    private static void DrawBadges(
        IRenderer renderer, DalilConfig config, FontStyle small, PaletteRow row, Rect bounds, int y)
    {
        if (row.Entry.Badges.Count == 0) return;

        // Right to left, so the first badge stays nearest the title however many
        // there are and the row does not reflow as state changes.
        int right = bounds.Right - TextInset;

        for (int i = row.Entry.Badges.Count - 1; i >= 0; i--)
        {
            string badge = row.Entry.Badges[i];
            Size size = renderer.Measure(badge, small);

            int x = right - size.Width;
            if (x < bounds.X + 120) return;

            renderer.DrawText(badge, new Rect(x, y, size.Width + 2, size.Height + 4), config.Secondary, small);
            right = x - 10;
        }
    }

    /// <summary>Says how much of the list is not being shown.</summary>
    /// <remarks>
    /// A count rather than a scrollbar. The palette is driven by the keyboard, so a
    /// bar that cannot be dragged would be decoration; "12 of 213" is the fact the
    /// user actually wants, which is whether the thing they are looking for might be
    /// further down.
    /// </remarks>
    private static void DrawScrollHint(
        IRenderer renderer, PaletteModel model, DalilConfig config, Rect canvas, FontStyle small,
        int first, int count)
    {
        if (model.Rows.Count <= count) return;

        string hint = $"{first + 1}-{first + count} of {model.Rows.Count}";
        Size size = renderer.Measure(hint, small);

        renderer.DrawText(
            hint,
            new Rect(canvas.Right - size.Width - TextInset, canvas.Bottom - size.Height - 6,
                     size.Width + 2, size.Height + 4),
            config.Secondary,
            small);
    }
}
