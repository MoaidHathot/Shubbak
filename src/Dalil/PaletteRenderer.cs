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
/// So only the visible slice is touched, the model works out which slice that is, and
/// the chrome that never changes is measured once and remembered. Everything added
/// for appearance - the chip, the accent bar, the pills - is a filled rectangle,
/// which costs nothing to measure because there is nothing to measure.
/// </para>
/// </remarks>
internal static class PaletteRenderer
{
    private const int Padding = 10;
    private const int TextInset = 14;
    private const int AccentWidth = 3;
    private const int PillPadding = 7;
    private const int ChipPadding = 9;
    private const int HintBar = 40;

    /// <summary>
    /// Height reserved along the bottom for the mode hints, at a given scale.
    /// </summary>
    /// <remarks>
    /// Every measurement in this file is written at 96 DPI and multiplied on the way
    /// out. Writing them scaled would mean a second set of numbers to keep in step
    /// with the first, and getting one wrong shows up as a single misaligned element
    /// on one machine.
    /// </remarks>
    public static int HintBarHeight(double scale) => (int)Math.Round(HintBar * scale);

    /// <summary>
    /// Widths of text that never changes, measured once.
    /// </summary>
    /// <remarks>
    /// The hint bar names five modes and their prefixes on every repaint, and a
    /// repaint happens on every keystroke. Measured each time that is ten
    /// <c>DT_CALCRECT</c> round trips to draw a caption that has never once been
    /// different. Keyed by text and font size so a move to a display at a different
    /// scale simply misses and re-measures.
    /// </remarks>
    private static readonly Dictionary<(string Text, double Size), Size> s_measured = [];

    private static Size Measure(IRenderer renderer, string text, FontStyle font)
    {
        if (s_measured.TryGetValue((text, font.Size), out Size cached)) return cached;

        Size size = renderer.Measure(text, font);
        s_measured[(text, font.Size)] = size;

        return size;
    }

    public static void Draw(
        IRenderer renderer, PaletteModel model, DalilConfig config, double scale, Rect canvas)
    {
        var metrics = new Metrics(scale);

        var font = new FontStyle(config.FontFamily, config.FontSize, Bold: false, Italic: false);

        var small = new FontStyle(
            config.FontFamily,
            Math.Max(8, config.FontSize - (int)Math.Round(3 * scale)),
            Bold: false,
            Italic: false);

        PaletteTheme theme = PaletteTheme.From(new DalilConfigView(
            config.Background, config.Foreground, config.Match, config.Secondary, config.Border));

        renderer.DrawRectangle(canvas, config.Border, 1, cornerRadius: metrics.Corner);

        int y = canvas.Y + metrics.Padding;
        y = DrawSearchBox(renderer, model, config, theme, metrics, canvas, font, small, y);

        (int first, int count) = model.VisibleWindow(config.VisibleRows);

        if (count == 0)
        {
            DrawEmptyState(renderer, model, config, metrics, canvas, font, small, y);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                DrawRow(renderer, model, config, theme, metrics, canvas, font, small, first + i, y);
                y += config.RowHeight;
            }
        }

        DrawHintBar(renderer, model, config, theme, metrics, canvas, small, first, count);
    }

    /// <summary>Spacing for one scale factor, worked out once per frame.</summary>
    private readonly record struct Metrics
    {
        public Metrics(double scale)
        {
            Scale = scale;
            Padding = At(PaletteRenderer.Padding, scale);
            TextInset = At(PaletteRenderer.TextInset, scale);
            AccentWidth = Math.Max(2, At(PaletteRenderer.AccentWidth, scale));
            PillPadding = At(PaletteRenderer.PillPadding, scale);
            ChipPadding = At(PaletteRenderer.ChipPadding, scale);
            HintBar = At(PaletteRenderer.HintBar, scale);
            Corner = At(10, scale);
        }

        public double Scale { get; }

        public int Padding { get; }

        public int TextInset { get; }

        public int AccentWidth { get; }

        public int PillPadding { get; }

        public int ChipPadding { get; }

        public int HintBar { get; }

        public int Corner { get; }

        /// <summary>Scales a measurement written at 96 DPI.</summary>
        public int this[int value] => At(value, Scale);

        private static int At(int value, double scale) => (int)Math.Round(value * scale);
    }

    private static int DrawSearchBox(
        IRenderer renderer, PaletteModel model, DalilConfig config, PaletteTheme theme,
        Metrics metrics, Rect canvas, FontStyle font, FontStyle small, int y)
    {
        var box = new Rect(canvas.X + metrics.Padding, y, canvas.Width - (metrics.Padding * 2), config.RowHeight);

        // The mode is shown as a word rather than left as the punctuation that
        // selected it. A bare ">" tells someone who has just pressed Tab nothing at
        // all about what they are now searching.
        //
        // As a chip rather than as loose text, so the eye reads it as a label on the
        // field instead of as the first word of what was typed.
        string label = PaletteModel.NameOf(model.Mode);
        Size labelSize = Measure(renderer, label, small);

        int chipHeight = labelSize.Height + metrics[8];
        var chip = new Rect(
            box.X + metrics[6],
            box.Y + ((box.Height - chipHeight) / 2),
            labelSize.Width + (metrics.ChipPadding * 2),
            chipHeight);

        renderer.FillRectangle(chip, theme.Chip, cornerRadius: chipHeight / 2);

        renderer.DrawText(
            label,
            new Rect(chip.X + metrics.ChipPadding, chip.Y + metrics[4], labelSize.Width + 2, labelSize.Height + 2),
            theme.ChipText,
            small);

        int x = chip.Right + metrics[12];

        // A prompt, so the field looks like something you type into even when it is
        // empty. A chevron rather than a magnifier: it is in every UI font on the
        // machine, where a glyph from a symbol range may or may not be.
        Size promptSize = Measure(renderer, "\u203A", font);

        renderer.DrawText(
            "\u203A",
            new Rect(x, TextTop(box, config), promptSize.Width + 2, promptSize.Height + 2),
            theme.Prompt,
            font);

        x += promptSize.Width + metrics[10];

        string typed = model.Term;

        renderer.DrawText(
            // A block for a caret. Drawing a real one means a timer, a blink and a
            // focus rule, for a field that is never not focused - and a timer is the
            // one thing that would stop a closed palette costing nothing.
            typed.Length == 0 ? "\u258F" : typed + "\u258F",
            new Rect(x, TextTop(box, config), box.Right - x - metrics[4], config.FontSize + metrics[8]),
            typed.Length == 0 ? theme.Prompt : config.Foreground,
            font);

        renderer.FillRectangle(
            new Rect(canvas.X + metrics.Padding, box.Bottom + metrics[3], canvas.Width - (metrics.Padding * 2), 1),
            theme.Rule);

        return box.Bottom + metrics[8];
    }

    private static void DrawRow(
        IRenderer renderer,
        PaletteModel model,
        DalilConfig config,
        PaletteTheme theme,
        Metrics metrics,
        Rect canvas,
        FontStyle font,
        FontStyle small,
        int index,
        int y)
    {
        PaletteRow row = model.Rows[index];
        bool selected = index == model.SelectedIndex;

        var bounds = new Rect(canvas.X + metrics.Padding, y, canvas.Width - (metrics.Padding * 2), config.RowHeight);

        if (selected)
        {
            renderer.FillRectangle(bounds, config.SelectionBackground, cornerRadius: metrics[6]);

            // A bar down the left edge as well as a fill. The fill alone is easy to
            // lose against a dark background at a glance, and this is a list read at
            // a glance by definition - the accent is what the eye finds before it has
            // read anything.
            renderer.FillRectangle(
                new Rect(bounds.X + metrics[2], bounds.Y + metrics[5], metrics.AccentWidth, bounds.Height - metrics[10]),
                theme.Accent,
                cornerRadius: metrics.AccentWidth);
        }

        int textY = TextTop(bounds, config);
        int right = DrawBadges(renderer, config, theme, metrics, small, row, bounds, textY);
        int x = bounds.X + metrics.TextInset;

        x = DrawHighlighted(renderer, config, font, row, x, textY, right - metrics[12]);

        if (row.Entry.Secondary.Length > 0 && x < right - metrics[60])
        {
            renderer.DrawText(
                row.Entry.Secondary,
                new Rect(x + metrics[10], textY + metrics[1], right - x - metrics[22], config.FontSize + metrics[6]),
                config.Secondary,
                small);
        }
    }

    /// <summary>Where text sits so it is optically centred in a row.</summary>
    private static int TextTop(Rect bounds, DalilConfig config) =>
        bounds.Y + ((bounds.Height - config.FontSize - (config.FontSize / 3)) / 2);

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

    /// <summary>
    /// Draws the row's state markers, right to left.
    /// </summary>
    /// <returns>Where the title must stop.</returns>
    /// <remarks>
    /// Drawn before the title, not after, so the title knows where to be clipped. A
    /// long title used to run under the badges and be overpainted by them, which read
    /// as a rendering fault rather than as a title that did not fit.
    /// <para>
    /// Right to left so the first badge stays nearest the title however many there
    /// are, and the row does not reflow as state changes.
    /// </para>
    /// </remarks>
    private static int DrawBadges(
        IRenderer renderer, DalilConfig config, PaletteTheme theme, Metrics metrics, FontStyle small,
        PaletteRow row, Rect bounds, int textY)
    {
        int right = bounds.Right - metrics.TextInset;
        if (row.Entry.Badges.Count == 0) return right;

        for (int i = row.Entry.Badges.Count - 1; i >= 0; i--)
        {
            string badge = row.Entry.Badges[i];
            Size size = Measure(renderer, badge, small);

            int width = size.Width + (metrics.PillPadding * 2);
            int height = size.Height + metrics[6];
            int x = right - width;

            // Never at the cost of the title. A row that is all state and no name is
            // not identifiable, which is the one thing a row has to be.
            if (x < bounds.X + metrics[160]) return right;

            var pill = new Rect(x, bounds.Y + ((bounds.Height - height) / 2), width, height);

            renderer.FillRectangle(pill, theme.Pill, cornerRadius: height / 2);

            renderer.DrawText(
                badge,
                new Rect(pill.X + metrics.PillPadding, pill.Y + metrics[3], size.Width + 2, size.Height + 2),
                theme.PillText,
                small);

            right = x - metrics[6];
        }

        return right;
    }

    /// <summary>Says why the list is empty, and what to do about it.</summary>
    private static void DrawEmptyState(
        IRenderer renderer, PaletteModel model, DalilConfig config, Metrics metrics,
        Rect canvas, FontStyle font, FontStyle small, int y)
    {
        bool searched = model.Term.Length > 0;

        string headline = searched ? "No matches" : "Nothing to show";

        string hint = searched
            ? "Backspace to widen the search, or Tab to look somewhere else"
            : "The window manager may still be starting up";

        int x = canvas.X + metrics.TextInset + metrics[4];

        renderer.DrawText(
            headline,
            new Rect(x, y + metrics[14], canvas.Width - (metrics.TextInset * 2), config.FontSize + metrics[8]),
            config.Foreground,
            font);

        renderer.DrawText(
            hint,
            new Rect(x, y + metrics[14] + config.FontSize + metrics[8],
                     canvas.Width - (metrics.TextInset * 2), config.FontSize + metrics[6]),
            config.Secondary,
            small);
    }

    /// <summary>
    /// The permanent hint bar: every mode, its prefix, and how far down the list we are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer to prefixes being fast and unfindable. Nobody guesses that <c>~</c>
    /// means layouts, and a shortcut nobody discovers is a shortcut nobody has. So all
    /// of them are on screen all of the time, the current one is highlighted so the
    /// mapping is learned by use rather than by reading, and <c>?</c> is listed
    /// alongside the rest as the way to the full key reference.
    /// </para>
    /// <para>
    /// Every measurement here is cached, so the whole bar costs a handful of filled
    /// rectangles and no round trips at all after the first frame.
    /// </para>
    /// </remarks>
    private static void DrawHintBar(
        IRenderer renderer, PaletteModel model, DalilConfig config, PaletteTheme theme,
        Metrics metrics, Rect canvas, FontStyle small, int first, int count)
    {
        int y = canvas.Bottom - metrics.HintBar;

        // The rule sits a little below the last row rather than flush against it, so
        // the bar reads as a separate strip instead of as a thirteenth result.
        renderer.FillRectangle(
            new Rect(canvas.X + metrics.Padding, y + metrics[5], canvas.Width - (metrics.Padding * 2), 1),
            theme.Rule);

        // Well clear of the bottom edge. The window has rounded corners, so anything
        // drawn in the last few pixels is clipped by the compositor rather than by
        // anything in this file - which looks like a font problem and is not one.
        int textY = y + metrics[13];
        int x = canvas.X + metrics.TextInset;

        // Tab first, because it is the one that needs no memory at all: a user who
        // reads nothing else can still reach every mode by pressing it.
        x = DrawHint(renderer, config, theme, metrics, small, "Tab", "modes", x, textY, active: false);

        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
        {
            char prefix = PaletteModel.PrefixFor(mode);
            if (prefix == '\0') continue;

            x = DrawHint(
                renderer, config, theme, metrics, small,
                prefix.ToString(), PaletteModel.NameOf(mode),
                x, textY, active: model.Mode == mode);
        }

        if (model.Rows.Count <= count) return;

        // A count rather than a scrollbar. The palette is driven by the keyboard, so a
        // bar that cannot be dragged would be decoration; whether the thing being
        // looked for might be further down is the fact actually wanted.
        string position = $"{first + 1}\u2013{first + count} of {model.Rows.Count}";
        Size size = renderer.Measure(position, small);

        renderer.DrawText(
            position,
            new Rect(canvas.Right - size.Width - metrics.TextInset, textY, size.Width + 2, size.Height + 4),
            config.Secondary,
            small);
    }

    private static int DrawHint(
        IRenderer renderer, DalilConfig config, PaletteTheme theme, Metrics metrics, FontStyle small,
        string key, string label, int x, int y, bool active)
    {
        Size keySize = Measure(renderer, key, small);

        int width = keySize.Width + (metrics[6] * 2);
        int height = keySize.Height + metrics[4];

        var cap = new Rect(x, y - metrics[1], width, height);

        // The prefix is drawn as a key cap. It is the part worth remembering, and a
        // shape around it is what makes it read as something to press rather than as
        // punctuation in a sentence.
        renderer.FillRectangle(cap, active ? theme.Chip : theme.Pill, cornerRadius: metrics[4]);

        renderer.DrawText(
            key,
            new Rect(cap.X + metrics[6], cap.Y + metrics[2], keySize.Width + 2, keySize.Height + 2),
            active ? theme.ChipText : config.Secondary,
            small);

        x = cap.Right + metrics[5];

        Size labelSize = Measure(renderer, label, small);

        renderer.DrawText(
            label, new Rect(x, y, labelSize.Width + 2, labelSize.Height + 4),
            active ? config.Foreground : config.Secondary, small);

        return x + labelSize.Width + metrics[16];
    }
}