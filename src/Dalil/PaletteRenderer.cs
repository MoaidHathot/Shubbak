using Dalil.Core;
using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Shubbak.Ui.Rendering;

namespace Dalil;

/// <summary>What the palette needs drawn, beyond the model itself.</summary>
/// <param name="OverlayTitle">The list opened from a row, when one is showing.</param>
/// <param name="Icons">
/// How to find a window's application icon, or null to draw none. A function rather
/// than a cache reference so the renderer stays testable and knows nothing about Win32.
/// </param>
/// <param name="IconRenderer">
/// The renderer's icon capability, when it has one. Null degrades to the layout that
/// existed before icons did, rather than to a crash or a gap.
/// </param>
internal readonly record struct PaletteChrome(
    string? OverlayTitle = null,
    Func<long, nint>? Icons = null,
    IIconRenderer? IconRenderer = null);

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
    /// <summary>
    /// Widths of text that never changes, measured once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hint bar names the modes and their prefixes on every repaint, and a repaint
    /// happens on every keystroke. Measured each time that is a dozen
    /// <c>DT_CALCRECT</c> round trips to draw a caption that has never once been
    /// different. Keyed by text and font size so a move to a display at a different
    /// scale simply misses and re-measures.
    /// </para>
    /// <para>
    /// Bounded, which it was not. Badges carry workspace names and the breadcrumb chip
    /// carries a truncated window title, so arbitrary text was reaching a dictionary
    /// that only ever grew - a slow leak in a process that is resident for the length
    /// of a login session. Cleared wholesale when it gets large, because the entries
    /// that matter are the fixed chrome and those are re-measured on the very next
    /// frame.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<(string Text, double Size), Size> s_measured = [];

    /// <summary>How many measurements to remember before starting again.</summary>
    private const int MeasureCacheCapacity = 512;

    /// <summary>Trims a title to fit a chip without a mid-word cut.</summary>
    private static string Shorten(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)] + "\u2026";

    private static Size Measure(IRenderer renderer, string text, FontStyle font)
    {
        if (s_measured.TryGetValue((text, font.Size), out Size cached)) return cached;

        Size size = renderer.Measure(text, font);

        if (s_measured.Count >= MeasureCacheCapacity) s_measured.Clear();

        s_measured[(text, font.Size)] = size;

        return size;
    }

    public static void Draw(
        IRenderer renderer, PaletteModel model, DalilConfig config, PaletteLayout layout,
        PaletteChrome chrome = default)
    {
        Rect canvas = layout.Canvas;

        var font = new FontStyle(config.FontFamily, config.FontSize, Bold: false, Italic: false);

        var small = new FontStyle(
            config.FontFamily,
            Math.Max(8, config.FontSize - (int)Math.Round(3 * layout.Scale)),
            Bold: false,
            Italic: false);

        PaletteTheme theme = PaletteTheme.From(new DalilConfigView(
            config.Background, config.Foreground, config.Match, config.Secondary,
            config.Border, config.Danger));

        renderer.DrawRectangle(canvas, config.Border, 1, cornerRadius: layout.Corner);

        DrawSearchBox(
            renderer, model, config, theme, layout, canvas, font, small,
            layout.SearchBox.Y, chrome.OverlayTitle);

        (int first, int count) = model.VisibleWindow(layout.VisibleRows);

        if (count == 0)
        {
            DrawEmptyState(renderer, model, config, layout, canvas, font, small, layout.ListTop);
        }
        else
        {
            // Positions come from the layout rather than from a running total, so the
            // rows drawn are exactly the rows the mouse will hit.
            for (int slot = 0; slot < count; slot++)
            {
                DrawRow(
                    renderer, model, config, theme, layout, canvas, font, small,
                    first + slot, slot, chrome);
            }
        }

        DrawHintBar(renderer, model, config, theme, layout, canvas, small, first, count, chrome.OverlayTitle);
    }

    private static void DrawSearchBox(
        IRenderer renderer, PaletteModel model, DalilConfig config, PaletteTheme theme,
        PaletteLayout layout, Rect canvas, FontStyle font, FontStyle small, int y, string? actionsFor)
    {
        var box = new Rect(canvas.X + layout.Padding, y, canvas.Width - (layout.Padding * 2), config.RowHeight);

        // The mode is shown as a word rather than left as the punctuation that
        // selected it. A bare ">" tells someone who has just pressed Tab nothing at
        // all about what they are now searching.
        //
        // As a chip rather than as loose text, so the eye reads it as a label on the
        // field instead of as the first word of what was typed.
        // In the action list the chip names the window being acted on, not the mode.
        // The mode has not changed and saying "windows" over a list of verbs would be
        // actively misleading about what Enter is going to do.
        string label = actionsFor is { Length: > 0 } subject
            ? Shorten(Leaf(subject), 28)
            : PaletteModel.NameOf(model.Mode);

        Size labelSize = Measure(renderer, label, small);

        int chipHeight = labelSize.Height + layout[8];
        var chip = new Rect(
            box.X + layout[6],
            box.Y + ((box.Height - chipHeight) / 2),
            labelSize.Width + (layout.ChipPadding * 2),
            chipHeight);

        renderer.FillRectangle(chip, theme.Chip, cornerRadius: chipHeight / 2);

        renderer.DrawText(
            label,
            new Rect(chip.X + layout.ChipPadding, chip.Y + layout[4], labelSize.Width + 2, labelSize.Height + 2),
            theme.ChipText,
            small);

        int x = chip.Right + layout[12];

        // A prompt, so the field looks like something you type into even when it is
        // empty. A chevron rather than a magnifier: it is in every UI font on the
        // machine, where a glyph from a symbol range may or may not be.
        Size promptSize = Measure(renderer, "\u203A", font);

        renderer.DrawText(
            "\u203A",
            new Rect(x, TextTop(box, config), promptSize.Width + 2, promptSize.Height + 2),
            theme.Prompt,
            font);

        x += promptSize.Width + layout[10];

        // The right end of the field, taken before the typed text is placed so the
        // text stops short of it rather than running underneath.
        int typedRight = DrawStatus(renderer, model, theme, layout, small, box);

        DrawTyped(renderer, model, config, theme, font, x, TextTop(box, config), typedRight - layout[4], config);

        renderer.FillRectangle(
            new Rect(canvas.X + layout.Padding, box.Bottom + layout[3], canvas.Width - (layout.Padding * 2), 1),
            theme.Rule);
    }

    /// <summary>
    /// Draws what was typed, with the caret where it actually is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caret is a block drawn between two runs of text rather than a real one.
    /// Drawing a real caret means a timer, a blink and a focus rule, for a field that
    /// is never not focused - and a timer is the one thing that would stop a closed
    /// palette costing nothing at all.
    /// </para>
    /// <para>
    /// Scrolled so the caret is always visible. The field used to draw the whole term
    /// into a fixed rectangle and let GDI clip it, which was invisible while the
    /// palette was a filter over short queries and became the obvious problem the
    /// moment commands mode grew arguments: <c>resize --width +5%</c> is longer than
    /// the box on a narrow window, and what ran off the right-hand end was exactly
    /// what was being typed.
    /// </para>
    /// </remarks>
    private static void DrawTyped(
        IRenderer renderer, PaletteModel model, DalilConfig config, PaletteTheme theme,
        FontStyle font, int x, int y, int right, DalilConfig metrics)
    {
        const string Caret = "\u258F";

        string typed = model.Term;
        int available = Math.Max(0, right - x);
        int height = metrics.FontSize + 8;

        if (typed.Length == 0)
        {
            renderer.DrawText(Caret, new Rect(x, y, available, height), theme.Prompt, font);
            return;
        }

        int caret = Math.Clamp(model.TermCaret, 0, typed.Length);

        string before = typed[..caret];
        string after = typed[caret..];

        // Measured rather than counted: Segoe UI is proportional, so a character count
        // would put the caret somewhere near the right place and only for text made
        // entirely of the same letter.
        int beforeWidth = before.Length == 0 ? 0 : renderer.Measure(before, font).Width;
        int caretWidth = Measure(renderer, Caret, font).Width;
        int wholeWidth = beforeWidth + caretWidth +
            (after.Length == 0 ? 0 : renderer.Measure(after, font).Width);

        // Only ever scrolled far enough to bring the caret back into view, and never
        // past the start. Keeping the text pinned left whenever it fits means the
        // common case - a short query - never moves under the reader.
        int shift = 0;

        if (wholeWidth > available)
        {
            int caretRight = beforeWidth + caretWidth;

            if (caretRight > available) shift = caretRight - available;
        }

        int at = x - shift;

        if (before.Length > 0)
        {
            renderer.DrawText(
                before, new Rect(at, y, beforeWidth + shift + 2, height), config.Foreground, font);
        }

        at += beforeWidth;

        renderer.DrawText(Caret, new Rect(at, y, caretWidth + 2, height), theme.Prompt, font);
        at += caretWidth;

        if (after.Length > 0)
            renderer.DrawText(after, new Rect(at, y, Math.Max(0, right - at), height), config.Foreground, font);
    }

    /// <summary>
    /// Says so when the window manager is not behaving normally.
    /// </summary>
    /// <remarks>
    /// Paused tiling, a swallowing binding mode and a suspended manager all look
    /// exactly like a crash: windows stop being arranged, or the keyboard stops
    /// responding. A window manager that cannot be reached at all looks like a slow
    /// one. The palette is where somebody goes to find out what is wrong, which makes
    /// it the last place that should stay silent about the four causes it already
    /// knows.
    /// <para>
    /// In the search box rather than the hint bar, because the hint bar is a list of
    /// things that are always true and this is a thing that is usually not - and
    /// because the eye is already at the box, reading what it typed.
    /// </para>
    /// </remarks>
    /// <returns>The x the typed text must stop at.</returns>
    private static int DrawStatus(
        IRenderer renderer, PaletteModel model, PaletteTheme theme,
        PaletteLayout layout, FontStyle small, Rect box)
    {
        WmStatus status = model.Status;

        // Most alarming first. A palette that cannot reach the window manager is
        // showing a list that may be minutes old, which matters more than anything the
        // window manager might have been doing when it was last heard from.
        (string? label, bool alarming) = !status.Connected
            ? ("offline", true)
            : status.Suspended
                ? ("suspended", true)
                : status.Paused
                    ? ("paused", false)
                    : status.BindingMode is { Length: > 0 } mode ? (mode, false) : (null, false);

        if (label is null) return box.Right;

        Size size = Measure(renderer, label, small);

        int height = size.Height + layout[8];
        int width = size.Width + (layout.ChipPadding * 2);
        int x = box.Right - width - layout[6];

        // Never at the cost of the field. A search box you cannot see what you typed
        // into is worse than an unexplained pause.
        if (x < box.X + layout[160]) return box.Right;

        var pill = new Rect(x, box.Y + ((box.Height - height) / 2), width, height);

        // The accent, not the pill grey the badges use. This is the one thing on the
        // window that is meant to be noticed without being looked for.
        renderer.FillRectangle(pill, alarming ? theme.DangerPill : theme.Accent, cornerRadius: height / 2);

        renderer.DrawText(
            label,
            new Rect(pill.X + layout.ChipPadding, pill.Y + layout[4], size.Width + 2, size.Height + 2),
            alarming ? theme.Danger : theme.ChipText,
            small);

        return x - layout[6];
    }

    private static void DrawRow(
        IRenderer renderer,
        PaletteModel model,
        DalilConfig config,
        PaletteTheme theme,
        PaletteLayout layout,
        Rect canvas,
        FontStyle font,
        FontStyle small,
        int index,
        int slot,
        PaletteChrome chrome)
    {
        PaletteRow row = model.Rows[index];
        PaletteEntry entry = row.Entry;

        bool selected = index == model.SelectedIndex;
        bool marked = model.IsMarked(entry);

        Rect bounds = layout.RowBounds(slot);

        if (selected)
        {
            renderer.FillRectangle(bounds, config.SelectionBackground, cornerRadius: layout[6]);

            // A bar down the left edge as well as a fill. The fill alone is easy to
            // lose against a dark background at a glance, and this is a list read at
            // a glance by definition - the accent is what the eye finds before it has
            // read anything.
            renderer.FillRectangle(
                new Rect(bounds.X + layout[2], bounds.Y + layout[5], layout.AccentWidth, bounds.Height - layout[10]),
                theme.Accent,
                cornerRadius: layout.AccentWidth);
        }

        // A marked row keeps its stripe whether or not it is selected, which is the
        // whole point: the reason to mark six windows is to then move the selection
        // off them and still know which six they were.
        if (marked && !selected)
        {
            renderer.FillRectangle(
                new Rect(bounds.X + layout[2], bounds.Y + layout[5], layout.MarkWidth, bounds.Height - layout[10]),
                theme.Mark,
                cornerRadius: layout.MarkWidth);
        }

        if (layout.IconSize > 0 &&
            chrome.IconRenderer is { } icons &&
            chrome.Icons is { } lookup &&
            entry.IconHandle is { } handle &&
            lookup(handle) is var icon && icon != 0)
        {
            icons.DrawIcon(icon, layout.IconBounds(slot));
        }

        Colour title = entry.Destructive
            ? theme.Danger
            : entry.Unavailable ? theme.Muted : config.Foreground;

        Colour secondary = entry.Unavailable ? theme.Muted : config.Secondary;

        int textY = TextTop(bounds, config);
        int right = DrawBadges(renderer, theme, layout, small, row, bounds, marked);
        int x = bounds.X + layout.RowTextInset;

        x = DrawHighlighted(
            renderer, config, font, entry.Primary, row.Positions, title, x, textY, right - layout[12]);

        if (entry.Secondary.Length > 0 && x < right - layout[60])
        {
            _ = DrawHighlighted(
                renderer, config, small, entry.Secondary, row.SecondaryPositions ?? [],
                secondary, x + layout[10], textY + layout[1], right - layout[12]);
        }
    }

    /// <summary>Where text sits so it is optically centred in a row.</summary>
    private static int TextTop(Rect bounds, DalilConfig config) =>
        bounds.Y + ((bounds.Height - config.FontSize - (config.FontSize / 3)) / 2);

    /// <summary>
    /// Draws text, colouring the characters that matched.
    /// </summary>
    /// <remarks>
    /// One draw call per run rather than per character: a run is a maximal stretch of
    /// characters that are all matched or all not, so the common cases - a prefix, a
    /// contiguous substring - cost two or three calls rather than one per letter.
    /// <para>
    /// This is why the matcher returns positions at all. Highlighting is the only
    /// feedback that explains <em>why</em> a row is in the list, which matters most
    /// for the abbreviations that look like coincidences - and, now, for the rows
    /// matched on their application rather than their title, which used to appear with
    /// nothing underlined anywhere and no way to tell why they had.
    /// </para>
    /// </remarks>
    private static int DrawHighlighted(
        IRenderer renderer, DalilConfig config, FontStyle font, string text,
        IReadOnlyList<int> positions, Colour colour, int x, int y, int limit)
    {
        if (text.Length == 0 || x >= limit) return x;

        if (positions.Count == 0)
        {
            Size whole = renderer.Measure(text, font);
            renderer.DrawText(text, new Rect(x, y, Math.Max(0, limit - x), whole.Height + 4), colour, font);

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
                matched ? config.Match : colour,
                font);

            x += size.Width;
            at += run;
            if (matched) next += run;
        }

        return Math.Min(x, limit);
    }

    /// <summary>
    /// Draws the row's state markers, most important first.
    /// </summary>
    /// <returns>Where the title must stop.</returns>
    /// <remarks>
    /// <para>
    /// Drawn before the title, not after, so the title knows where to be clipped. A
    /// long title used to run under the badges and be overpainted by them, which read
    /// as a rendering fault rather than as a title that did not fit.
    /// </para>
    /// <para>
    /// The first badge is drawn at the right edge and the rest fill in leftwards, so
    /// the one that matters most is pinned to a fixed column and the ones that get
    /// dropped when a row runs out of room are the ones at the end of the list. It
    /// used to be the other way about: badges were drawn from the end of the list
    /// backwards and the loop gave up when it ran out of width, so a window that was
    /// unmanaged, minimised, floating, sticky and tagged onto three workspaces would
    /// silently omit "unmanaged" and "minimised" - the only two that explained why it
    /// was not where it had been left.
    /// </para>
    /// </remarks>
    private static int DrawBadges(
        IRenderer renderer, PaletteTheme theme, PaletteLayout layout, FontStyle small,
        PaletteRow row, Rect bounds, bool marked)
    {
        int right = bounds.Right - layout.TextInset;

        // A marked row says so in words as well as with its stripe. The stripe is
        // three pixels wide and this is a list somebody is about to act on the
        // contents of, so it is worth the room.
        if (marked)
            right = DrawBadge(renderer, theme, layout, small, "marked", right, bounds, theme.Mark);

        for (int i = 0; i < row.Entry.Badges.Count; i++)
        {
            int next = DrawBadge(
                renderer, theme, layout, small, row.Entry.Badges[i], right, bounds, theme.Pill);

            // Out of room. Everything after this is less important than what has
            // already been drawn, so stopping here loses the least.
            if (next == right) break;

            right = next;
        }

        return right;
    }

    /// <summary>Draws one badge, if it fits, and says where the next one goes.</summary>
    private static int DrawBadge(
        IRenderer renderer, PaletteTheme theme, PaletteLayout layout, FontStyle small,
        string badge, int right, Rect bounds, Colour fill)
    {
        Size size = Measure(renderer, badge, small);

        int width = size.Width + (layout.PillPadding * 2);
        int height = size.Height + layout[6];
        int x = right - width;

        // Never at the cost of the title. A row that is all state and no name is not
        // identifiable, which is the one thing a row has to be.
        if (x < bounds.X + layout[160]) return right;

        var pill = new Rect(x, bounds.Y + ((bounds.Height - height) / 2), width, height);

        renderer.FillRectangle(pill, fill, cornerRadius: height / 2);

        renderer.DrawText(
            badge,
            new Rect(pill.X + layout.PillPadding, pill.Y + layout[3], size.Width + 2, size.Height + 2),
            fill.Equals(theme.Mark) ? theme.ChipText : theme.PillText,
            small);

        return x - layout[6];
    }

    /// <summary>Says why the list is empty, and what to do about it.</summary>
    /// <remarks>
    /// A window manager that cannot be reached says so. It used to suggest that the
    /// window manager "may still be starting up", which is true for about two seconds
    /// after a login and misleading for ever afterwards - a daemon that has crashed and
    /// one that is merely slow produced identical, confidently wrong, advice.
    /// </remarks>
    private static void DrawEmptyState(
        IRenderer renderer, PaletteModel model, DalilConfig config, PaletteLayout layout,
        Rect canvas, FontStyle font, FontStyle small, int y)
    {
        bool searched = model.Term.Length > 0;
        bool offline = !model.Status.Connected;

        string headline = offline
            ? "Can't reach the window manager"
            : searched ? "No matches" : "Nothing to show";

        string hint = offline
            ? "Is shubbak-wm running? `shubbak status` will say."
            : searched
                ? "Backspace to widen the search, or Tab to look somewhere else"
                : "The window manager may still be starting up";

        int x = canvas.X + layout.TextInset + layout[4];

        renderer.DrawText(
            headline,
            new Rect(x, y + layout[14], canvas.Width - (layout.TextInset * 2), config.FontSize + layout[8]),
            offline ? config.Danger : config.Foreground,
            font);

        renderer.DrawText(
            hint,
            new Rect(x, y + layout[14] + config.FontSize + layout[8],
                     canvas.Width - (layout.TextInset * 2), config.FontSize + layout[6]),
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
        PaletteLayout layout, Rect canvas, FontStyle small, int first, int count, string? actionsFor)
    {
        int y = canvas.Bottom - layout.HintBar;

        // The rule sits a little below the last row rather than flush against it, so
        // the bar reads as a separate strip instead of as a thirteenth result.
        renderer.FillRectangle(
            new Rect(canvas.X + layout.Padding, y + layout[5], canvas.Width - (layout.Padding * 2), 1),
            theme.Rule);

        // Well clear of the bottom edge. The window has rounded corners, so anything
        // drawn in the last few pixels is clipped by the compositor rather than by
        // anything in this file - which looks like a font problem and is not one.
        int textY = y + layout[13];
        int x = canvas.X + layout.TextInset;

        // The position is measured and placed first, so the hints know where they have
        // to stop. Drawn last and the two collided: enough modes to fill the bar and
        // "actions" was printed straight through "1-12 of 17", which is the sort of
        // thing that looks like a font problem and is really an arithmetic one.
        int limit = canvas.Right - layout.TextInset;

        // What is marked outranks where we are in the list. A count of marks is a
        // thing the user is holding in their head and needs confirmed; a scroll
        // position is a thing they can see.
        string? tail = model.MarkedCount > 0
            ? $"{model.MarkedCount} marked"
            : model.Rows.Count > count
                // A count rather than a scrollbar. The palette is driven by the
                // keyboard, so a bar that cannot be dragged would be decoration;
                // whether the thing being looked for might be further down is the fact
                // actually wanted.
                ? $"{first + 1}\u2013{first + count} of {model.Rows.Count}"
                : null;

        if (tail is not null)
        {
            Size size = renderer.Measure(tail, small);

            renderer.DrawText(
                tail,
                new Rect(canvas.Right - size.Width - layout.TextInset, textY, size.Width + 2, size.Height + 4),
                model.MarkedCount > 0 ? config.Match : config.Secondary,
                small);

            limit = canvas.Right - size.Width - layout.TextInset - layout[10];
        }

        // In the action list the modes are not what matters and Escape is, because a
        // list of verbs with no visible way back is the one place somebody will press
        // it hoping to undo and expect the whole palette to vanish.
        //
        // What Enter does is read off the selected row rather than assumed. Every
        // overlay used to advertise "do it", including a report whose rows all do
        // nothing at all - so the one list where Enter was inert was also the one
        // insisting it was not.
        if (actionsFor is { Length: > 0 })
        {
            if (VerbFor(model.Selected?.Entry) is { Length: > 0 } verb)
                x = DrawHint(renderer, config, theme, layout, small, "\u21B5", verb, x, textY, limit, active: true);

            if (model.Selected?.Entry.Expands is { Length: > 0 })
                x = DrawHint(renderer, config, theme, layout, small, "\u2303C", "copy", x, textY, limit, active: false);

            _ = DrawHint(renderer, config, theme, layout, small, "Esc", "back", x, textY, limit, active: false);
            return;
        }

        // Every prefix is shown, always. A hint that does not fit is not drawn at all,
        // so the last mode in the list used to fall off the end in silence - which is
        // how adding one made another disappear, with nothing to suggest it had.
        //
        // Rather than budgeting for a particular width, the bar is tried at each level
        // of detail until one fits. Nothing here needs to know how wide Segoe UI is at
        // this scale, and a wider window or a shorter mode list simply gets a fuller
        // bar without anybody adjusting a number.
        bool hasActions = model.Selected?.Entry.HasActions == true || model.MarkedCount > 0;
        HintStyle style = StyleThatFits(renderer, model, layout, small, x, limit, hasActions);

        // Tab first, because it is the one that needs no memory at all: a user who
        // reads nothing else can still reach every mode by pressing it. Its label is
        // the first thing to go, though - "Tab" beside a row of prefixes is legible
        // without being told that prefixes are modes, and the word costs as much room
        // as a mode name.
        x = DrawHint(
            renderer, config, theme, layout, small,
            "Tab", style.TabLabel ? "modes" : string.Empty, x, textY, limit, active: false);

        if (hasActions && style.Actions)
            x = DrawHint(renderer, config, theme, layout, small, "\u2303\u21B5", "actions", x, textY, limit, active: false);

        // In jump order, which is the order the digits number and the order the help
        // screen lists. The bar used to walk the enum, so the caps on screen and the
        // keys that reach them agreed only by coincidence.
        foreach (PaletteMode mode in PaletteModel.JumpOrder)
        {
            char prefix = model.Prefixes.PrefixFor(mode);
            if (prefix == '\0') continue;

            x = DrawHint(
                renderer, config, theme, layout, small,
                prefix.ToString(), style.ModeNames ? PaletteModel.NameOf(mode) : string.Empty,
                x, textY, limit, active: model.Mode == mode);
        }
    }

    /// <summary>How much of the hint bar is spelled out rather than left as a key cap.</summary>
    private readonly record struct HintStyle(bool TabLabel, bool Actions, bool ModeNames);

    /// <summary>
    /// The bar from fullest to plainest, in the order things are given up.
    /// </summary>
    /// <remarks>
    /// The mode names go last, and all together. They are what the bar is for - a
    /// prefix nobody can read is a shortcut nobody has - and a bar naming three modes
    /// and showing four bare caps would read as the names belonging to the wrong caps.
    /// Before them go the word "modes", which explains a key that explains itself, and
    /// then the advertisement for Ctrl+Enter, which is also written under <c>?</c>.
    /// </remarks>
    private static readonly HintStyle[] s_hintStyles =
    [
        new(TabLabel: true, Actions: true, ModeNames: true),
        new(TabLabel: false, Actions: true, ModeNames: true),
        new(TabLabel: false, Actions: false, ModeNames: true),
        new(TabLabel: false, Actions: false, ModeNames: false),
    ];

    /// <summary>The fullest bar that fits, or the plainest when none does.</summary>
    private static HintStyle StyleThatFits(
        IRenderer renderer, PaletteModel model, PaletteLayout layout, FontStyle small,
        int x, int limit, bool hasActions)
    {
        foreach (HintStyle style in s_hintStyles)
        {
            if (x + HintsWidth(renderer, model, layout, small, style, hasActions) <= limit)
                return style;
        }

        return s_hintStyles[^1];
    }

    /// <summary>How much room a whole bar of hints needs.</summary>
    private static int HintsWidth(
        IRenderer renderer, PaletteModel model, PaletteLayout layout, FontStyle small,
        HintStyle style, bool hasActions)
    {
        int width = HintWidth(renderer, layout, small, "Tab", style.TabLabel ? "modes" : string.Empty);

        if (hasActions && style.Actions)
            width += HintWidth(renderer, layout, small, "\u2303\u21B5", "actions");

        foreach (PaletteMode mode in PaletteModel.JumpOrder)
        {
            char prefix = model.Prefixes.PrefixFor(mode);
            if (prefix == '\0') continue;

            width += HintWidth(
                renderer, layout, small,
                prefix.ToString(),
                style.ModeNames ? PaletteModel.NameOf(mode) : string.Empty);
        }

        // Every hint includes the gap that would follow it, and nothing follows the
        // last one. Left in, the bar would be judged a whole gap wider than it draws
        // and would give up its names slightly before it had to.
        return width - layout[16];
    }

    /// <summary>How much room a hint needs, cap and label together.</summary>
    private static int HintWidth(
        IRenderer renderer, PaletteLayout layout, FontStyle small, string key, string label)
    {
        int cap = Measure(renderer, key, small).Width + (layout[6] * 2);

        return label.Length == 0
            ? cap + layout[16]
            : cap + layout[5] + Measure(renderer, label, small).Width + layout[16];
    }

    /// <summary>
    /// What Enter would do to the selected row, in one word, or nothing.
    /// </summary>
    /// <remarks>
    /// Read from the row rather than from the kind of list it is in, because the two
    /// disagree: a report and an action list are both overlays, and Enter is inert in
    /// one and consequential in the other. Returning empty for a row that does nothing
    /// leaves the hint out altogether, which is the honest answer - a key cap that
    /// promises an outcome it cannot deliver is worse than no key cap.
    /// </remarks>
    private static string VerbFor(PaletteEntry? entry) => entry switch
    {
        null => string.Empty,
        { SwitchesTo: not null } => "go",
        { Explains: not null } => "inspect",
        { Expands.Length: > 0 } => "read it",
        { HasActions: true, Command.Length: 0 } => "open",
        { Destructive: true, Command.Length: > 0 } => "ask first",
        { Command.Length: > 0 } => "do it",
        _ => string.Empty,
    };

    /// <summary>The last part of a breadcrumb, which is the part that is about to act.</summary>
    /// <remarks>
    /// A chip is a fixed and rather small amount of room, and a breadcrumb three levels
    /// deep spends all of it on the levels already left behind - so the one name that
    /// says what Enter is about to do was the one being ellipsised away.
    /// </remarks>
    private static string Leaf(string breadcrumb)
    {
        int cut = breadcrumb.LastIndexOf('\u203A');

        return cut >= 0 && cut < breadcrumb.Length - 1
            ? breadcrumb[(cut + 1)..].Trim()
            : breadcrumb;
    }

    /// <summary>
    /// Draws one key cap and its label, if it fits, and says where the next would go.
    /// </summary>
    /// <remarks>
    /// Nothing is drawn past <c>limit</c>. A hint printed beyond it would land on top
    /// of whatever is already there, which is worse than a hint that is simply
    /// absent - overlapping text is unreadable for both of them at once, and the modes
    /// are reachable with Tab regardless.
    /// <para>
    /// Measured whole before anything is drawn, so a hint is never half-printed with
    /// its label clipped away. A lone key cap explains nothing.
    /// </para>
    /// </remarks>
    private static int DrawHint(
        IRenderer renderer, DalilConfig config, PaletteTheme theme, PaletteLayout layout, FontStyle small,
        string key, string label, int x, int y, int limit, bool active)
    {
        Size keySize = Measure(renderer, key, small);

        int width = keySize.Width + (layout[6] * 2);
        int height = keySize.Height + layout[4];

        // Measured whole before anything is drawn, so a hint is never half-printed
        // with its label clipped off - a lone key cap explains nothing.
        //
        // Unless it is deliberately alone: a mode reduced to its cap because the bar
        // ran out of room is still worth showing, and is the only way every prefix
        // stays visible on a narrow window.
        Size labelSize = label.Length == 0 ? default : Measure(renderer, label, small);

        int end = label.Length == 0
            ? x + width
            : x + width + layout[5] + labelSize.Width;

        if (end > limit) return x;

        var cap = new Rect(x, y - layout[1], width, height);

        // The prefix is drawn as a key cap. It is the part worth remembering, and a
        // shape around it is what makes it read as something to press rather than as
        // punctuation in a sentence.
        renderer.FillRectangle(cap, active ? theme.Chip : theme.Pill, cornerRadius: layout[4]);

        renderer.DrawText(
            key,
            new Rect(cap.X + layout[6], cap.Y + layout[2], keySize.Width + 2, keySize.Height + 2),
            active ? theme.ChipText : config.Secondary,
            small);

        if (label.Length == 0) return cap.Right + layout[16];

        x = cap.Right + layout[5];

        renderer.DrawText(
            label, new Rect(x, y, labelSize.Width + 2, labelSize.Height + 4),
            active ? config.Foreground : config.Secondary, small);

        return x + labelSize.Width + layout[16];
    }
}
