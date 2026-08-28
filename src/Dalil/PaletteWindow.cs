using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalil.Core;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ipc;
using Shubbak.Native;
using Shubbak.Ui.Gdi;
using Shubbak.Ui.Layout;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dalil;

/// <summary>
/// The palette window: a search box and a list, driven entirely by the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// Created hidden at startup and never destroyed until the process ends. Opening is
/// therefore a ShowWindow and a repaint rather than a window creation, which is what
/// keeps it under the threshold where a user notices having waited.
/// </para>
/// <para>
/// <c>WS_EX_TOOLWINDOW</c> keeps it out of Alt+Tab and out of Shubbak's tree - the
/// filter rejects tool windows - while still allowing it to take the keyboard, which
/// <c>WS_EX_NOACTIVATE</c> would not. The bar uses NOACTIVATE precisely because it
/// must never steal focus; this window exists to.
/// </para>
/// </remarks>
public sealed class PaletteWindow : IDisposable
{
    private const string WindowClass = "DalilPaletteWindow";

    /// <summary>Posted by the IPC reader thread to wake the message loop.</summary>
    internal const uint WakeMessage = PInvoke.WM_APP + 1;

    private static readonly Dictionary<nint, PaletteWindow> s_windows = [];
    private static bool s_classRegistered;

    private readonly PaletteModel _model = new();
    private DalilConfig _config;

    /// <summary>
    /// The configuration with the monitor's scaling applied.
    /// </summary>
    /// <remarks>
    /// Everything drawn uses this; <see cref="_config"/> keeps what the user actually
    /// wrote. Without it the palette is laid out in raw pixels, which on a 3840x2160
    /// display at 150% renders a 720-pixel window a third smaller than intended and a
    /// 15-point font at ten. It looked like a design decision rather than a bug, which
    /// is how it survived being looked at.
    /// </remarks>
    private DalilConfig _scaled;

    /// <summary>The scale factor of the monitor the palette is on: 1.0 at 96 DPI.</summary>
    private double _scale = 1.0;

    private HWND _handle;
    private GdiRenderer? _renderer;
    private Rect _bounds;
    private string _query = string.Empty;
    private bool _open;
    private bool _disposed;

    /// <summary>
    /// True while the palette is deliberately giving focus away.
    /// </summary>
    /// <remarks>
    /// Choosing a row activates another window, which takes the foreground and sends
    /// this one <c>WM_ACTIVATE</c> with <c>WA_INACTIVE</c> - the same message as the
    /// user clicking elsewhere. Without this flag the close-on-blur handler runs in
    /// the middle of acting on the selection and races it.
    /// </remarks>
    private bool _closing;

    /// <summary>One level of list opened from a row.</summary>
    /// <param name="Title">What the search box calls it.</param>
    /// <param name="SavedQuery">What was typed before it took over the box.</param>
    /// <param name="Entries">The rows it shows.</param>
    /// <param name="Whole">
    /// The single value this frame is showing broken across its rows, when it is one.
    /// Set only for an expanded row: copying there has to yield the text as it was
    /// written, not as it happened to be broken to fit this window's width.
    /// </param>
    /// <remarks>
    /// The rows are held here rather than reached back through the row the frame was
    /// opened from. Most frames are a row's own children, but not all: an explanation
    /// is fetched from the window manager and belongs to no row, and going back to it
    /// has to work the same way as going back to anything else.
    /// </remarks>
    private readonly record struct Overlay(
        string Title,
        string SavedQuery,
        IReadOnlyList<PaletteEntry> Entries,
        string? Whole = null);

    /// <summary>Lists opened from a row, innermost last.</summary>
    private readonly Stack<Overlay> _overlays = new();

    /// <summary>Where the pointer was last seen, so a resting cursor is ignored.</summary>
    private (int X, int Y) _lastMouse = (int.MinValue, int.MinValue);

    private bool _trackingMouse;

    /// <summary>Raised with a command string when a row is chosen.</summary>
    public event Action<string>? CommandRequested;

    /// <summary>
    /// Raised when the palette starts showing a different kind of thing.
    /// </summary>
    /// <remarks>
    /// Fired for every route into a mode, not only for Tab. Typing <c>&gt;</c> changes
    /// mode as surely as pressing Tab does, and so does backspacing over it - and a
    /// mode change that did not refill the list would leave the user searching windows
    /// while the box said "commands".
    /// </remarks>
    public event Action<PaletteMode>? ModeChanged;

    /// <summary>
    /// Raised with a window handle and a title when a row asks for an explanation.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CommandRequested"/> because the answer comes back.
    /// Everything else the palette does is told to the window manager and forgotten;
    /// this has to be asked, waited for, and shown, so the palette stays open and the
    /// host calls <see cref="ShowReport"/> when the report arrives.
    /// </remarks>
    public event Action<long, string>? ExplainRequested;

    /// <summary>Raised when the process should stop.</summary>
    public static event Action? RequestShutdown;

    public PaletteWindow(DalilConfig config)
    {
        _config = config;
        _scaled = config;
    }

    /// <summary>Whether the palette is currently on screen.</summary>
    public bool IsOpen => _open;

    /// <summary>Which mode the palette is showing.</summary>
    /// <remarks>
    /// Read by the host when refreshing, so that a window event arriving while the
    /// user is browsing commands does not replace the command list with a window
    /// list underneath them.
    /// </remarks>
    public PaletteMode Mode => _model.Mode;

    public unsafe nint Handle => (nint)_handle.Value;

    /// <summary>Creates the window, hidden.</summary>
    public unsafe bool Create()
    {
        EnsureClassRegistered();

        _handle = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST,
            WindowClass,
            "Dalil",
            WINDOW_STYLE.WS_POPUP,
            0, 0, _config.Width, RequiredHeight(),
            HWND.Null, (SafeHandle?)null, (SafeHandle?)null, null);

        if (_handle.IsNull) return false;

        s_windows[(nint)_handle.Value] = this;
        _renderer = new GdiRenderer((nint)_handle.Value);

        RoundTheCorners();
        return true;
    }

    /// <summary>Supplies rows derived from the query itself.</summary>
    /// <remarks>
    /// Applied only in commands mode; every other mode offers what it was given.
    /// </remarks>
    public void Augment(Func<string, IReadOnlyList<PaletteEntry>> compose) =>
        _model.Augmenter = (mode, term) =>
            mode is PaletteMode.Commands ? compose(term) : [];

    /// <summary>Replaces the rows on offer.</summary>
    /// <remarks>
    /// Ignored while the action list is showing. Window events keep arriving whether
    /// or not the palette is busy, and a refresh landing mid-decision would replace
    /// "close it / float it / bring it here" with the window list underneath the
    /// user's finger - and Enter would then act on whatever had taken that row.
    /// </remarks>
    /// <summary>Records what the window manager last said about itself.</summary>
    public void SetStatus(WmStatus status)
    {
        _model.SetStatus(status);
        if (_open) Repaint();
    }

    public void SetEntries(IEnumerable<PaletteEntry> entries)
    {
        if (_overlays.Count > 0) return;

        _model.SetEntries(entries);
        if (_open) Repaint();
    }

    /// <summary>Applies a reloaded configuration.</summary>
    public void Reconfigure(DalilConfig config)
    {
        _config = config;
        _scaled = config;

        if (_open) Repaint();
    }

    /// <summary>
    /// Shows the palette on the right monitor and takes the keyboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The query is cleared on every open. A palette that remembers what was typed
    /// last time is one that shows a filtered list to someone who has just asked to
    /// see everything.
    /// </para>
    /// <para>
    /// Safe to call on an already-open palette, and deliberately so: pressing the key
    /// again is how a user says "you are not listening to me". Taking the foreground
    /// can fail for reasons this process cannot see or prevent, and when it does the
    /// window is on screen looking perfectly normal while every key goes somewhere
    /// else - so the same gesture that opens it has to be able to repair it.
    /// </para>
    /// <para>
    /// An open palette is not moved. Re-running the placement would make the window
    /// jump between monitors as the foreground changes underneath it, which is a
    /// strange answer to someone asking for the keyboard back.
    /// </para>
    /// </remarks>
    public bool Open(PaletteMode mode = PaletteMode.Windows)
    {
        if (_handle.IsNull) return false;

        bool wasOpen = _open;

        // A fresh open is a fresh question, so anything opened last time goes.
        _overlays.Clear();

        _query = PrefixFor(mode);
        _model.SetQuery(_query);
        _closing = false;

        if (!wasOpen)
        {
            PositionOnTargetMonitor();
            PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_SHOW);
        }

        _open = true;
        Repaint();

        return EnsureForeground();
    }

    /// <summary>
    /// Re-asserts that the palette has the keyboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="Open"/> so the host can try again a moment later.
    /// Taking the foreground fails for transient reasons - a menu still closing, a
    /// drag still finishing, an application still starting - and a second attempt a
    /// few frames later usually succeeds where the first did not.
    /// </para>
    /// <para>
    /// One case is not transient and no amount of retrying will fix it: the window
    /// being left behind belongs to a process at a higher integrity level.
    /// <c>AttachThreadInput</c> across that boundary is refused by UIPI, which is the
    /// same wall that stops the window manager tiling elevated windows. The message
    /// says so rather than leaving the user to conclude the palette is broken.
    /// </para>
    /// </remarks>
    public bool EnsureForeground()
    {
        if (_handle.IsNull || !_open) return false;

        if (Foreground.Take(_handle)) return true;

        Log.Warn(LogCategory.Wm,
            "the palette is on screen but could not take the keyboard. " +
            "Something is holding the foreground: a menu or a drag still finishing, " +
            "or a window belonging to a process running higher than Dalil, which " +
            "Windows will not let it take focus from.");

        return false;
    }

    /// <summary>Whether the palette is showing but not actually receiving keys.</summary>
    /// <remarks>
    /// A palette that failed to activate never became active, so it will never be
    /// told it has been deactivated - which means close-on-blur cannot dismiss it and
    /// it sits there looking normal and answering nothing. The host uses this to
    /// notice and put it away rather than leave a window nobody can reach.
    /// </remarks>
    public unsafe bool IsStranded =>
        _open && !_handle.IsNull && PInvoke.GetForegroundWindow() != _handle;

    /// <summary>Hides the palette.</summary>
    public void Close()
    {
        if (!_open || _handle.IsNull) return;

        // Forgotten on the way out, so the next open starts from the list rather than
        // from whatever was showing when it was dismissed.
        _overlays.Clear();

        _closing = true;
        _open = false;

        PInvoke.ShowWindow(_handle, SHOW_WINDOW_CMD.SW_HIDE);
    }

    // ---- input ---------------------------------------------------------------

    /// <summary>A printable character was typed.</summary>
    private void OnCharacter(char value)
    {
        // Control characters arrive here too - Enter, Escape, Backspace all produce a
        // WM_CHAR - and every one of them is handled as a key rather than as text.
        if (char.IsControl(value)) return;

        // Not simply appended: a prefix typed while there is nothing to search replaces
        // the mode rather than being searched for. See PaletteInput.Typed.
        ApplyQuery(PaletteInput.Typed(_query, value));
    }

    /// <summary>
    /// Replaces the query, announcing a mode change if one fell out of it.
    /// </summary>
    /// <remarks>
    /// The single place the query changes, so no route into a mode can forget to
    /// refill the list. There are four: Tab, typing a prefix, deleting one, and
    /// choosing a row in the help list.
    /// </remarks>
    private void ApplyQuery(string query)
    {
        PaletteMode before = _model.Mode;

        _query = query;
        _model.SetQuery(_query);

        if (_model.Mode != before) ModeChanged?.Invoke(_model.Mode);

        Repaint();
    }

    /// <summary>A key that means something other than a character.</summary>
    /// <returns>Whether the key was handled here.</returns>
    private bool OnKey(VIRTUAL_KEY key)
    {
        bool control = IsDown(VIRTUAL_KEY.VK_CONTROL);
        bool shift = IsDown(VIRTUAL_KEY.VK_SHIFT);

        switch (key)
        {
            case VIRTUAL_KEY.VK_ESCAPE:
                // Backs out of the action list rather than dismissing. Escape means
                // "not that" one level at a time; throwing the whole palette away
                // because somebody changed their mind about an action would mean
                // starting the search again.
                if (_overlays.Count > 0) Pop();
                else Close();

                return true;

            case VIRTUAL_KEY.VK_RETURN when control:
                EnterActions();
                return true;

            case VIRTUAL_KEY.VK_C when control:
                Copy(everything: shift);
                return true;

            case VIRTUAL_KEY.VK_RETURN:
                Choose();
                return true;

            case VIRTUAL_KEY.VK_BACK:
                // Empty and inside a frame, Backspace has nothing to delete, so it
                // goes back instead. Escape already did, but Backspace is what a hand
                // reaches for when the thing on screen arrived by pressing Enter -
                // and doing nothing at all was the least useful of the three options.
                if (_query.Length == 0 && _overlays.Count > 0) Pop();
                else Backspace(wholeWord: control);

                return true;

            case VIRTUAL_KEY.VK_UP:
                Move(-1);
                return true;

            case VIRTUAL_KEY.VK_DOWN:
                Move(1);
                return true;

            case VIRTUAL_KEY.VK_PRIOR:
                Move(-_scaled.VisibleRows);
                return true;

            case VIRTUAL_KEY.VK_NEXT:
                Move(_scaled.VisibleRows);
                return true;

            case VIRTUAL_KEY.VK_HOME when control:
                _model.SelectEdge(last: false);
                Repaint();
                return true;

            case VIRTUAL_KEY.VK_END when control:
                _model.SelectEdge(last: true);
                Repaint();
                return true;

            case VIRTUAL_KEY.VK_TAB:
                CycleMode(forward: !shift);
                return true;

            // The Emacs-style pair, because a palette is a text field and every other
            // text field on the machine honours them.
            case VIRTUAL_KEY.VK_N when control:
            case VIRTUAL_KEY.VK_J when control:
                Move(1);
                return true;

            case VIRTUAL_KEY.VK_P when control:
            case VIRTUAL_KEY.VK_K when control:
                Move(-1);
                return true;

            case VIRTUAL_KEY.VK_U when control:
                ApplyQuery(string.Empty);
                return true;

            default:
                return TryChord(key, control, shift);
        }
    }

    /// <summary>
    /// Acts on a chord, from the main list or from inside a list of actions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where a chord acts is decided by <see cref="PaletteInput.ChordActsHere"/>, which
    /// is where that rule lives and where it is tested. In short: always inside the
    /// action list, because that is the only place the chord is written down; and from
    /// the main list only when the guard is off, which is what the guard is for.
    /// </para>
    /// <para>
    /// The two look in different places for the same thing. From the main list the
    /// chords belong to the selected row's actions; inside the list the rows are those
    /// actions, and each carries its own.
    /// </para>
    /// </remarks>
    private bool TryChord(VIRTUAL_KEY key, bool control, bool shift)
    {
        if (PaletteInput.ChordFor(key, control, shift) is not { } wanted) return false;

        bool inside = _overlays.Count > 0;

        if (!PaletteInput.ChordActsHere(wanted, inside, _config.ActionGuard)) return false;

        if (inside)
        {
            // The frame's own rows rather than the filtered ones. A chord names the
            // action, not whatever happens to have survived what is typed in the box.
            PaletteEntry? row = _overlays.Peek().Entries
                .FirstOrDefault(e => string.Equals(e.Chord, wanted, StringComparison.Ordinal));

            return row is not null && Act(row.Command, row.Explains, row.Primary);
        }

        if (_model.Selected is not { } selected) return false;
        if (selected.Entry.Actions is not { Count: > 0 } actions) return false;

        if (actions.FirstOrDefault(a => a.Chord == wanted) is not { } action) return false;

        return Act(action.Command, action.Explains, selected.Entry.Primary);
    }

    /// <summary>Does what a chord selected, and says whether anything happened.</summary>
    /// <remarks>
    /// Explaining leaves the palette open, exactly as choosing the same row from the
    /// list does: the report is fetched and needs somewhere to arrive. Everything else
    /// closes first, because the command usually raises another window and a palette
    /// still topmost would cover the thing that was just asked for.
    /// </remarks>
    private bool Act(string command, long? explains, string leaf)
    {
        if (explains is { } handle)
        {
            ExplainRequested?.Invoke(handle, Breadcrumb(leaf));
            return true;
        }

        if (command.Length == 0) return false;

        Close();
        CommandRequested?.Invoke(command);

        return true;
    }

    /// <summary>
    /// Opens a list on top of whatever is showing.
    /// </summary>
    /// <remarks>
    /// Pushed rather than assigned, because a row inside a list can have a list of its
    /// own - the actions for a window include a tag picker, and the picker is chosen
    /// from within the actions. A single slot could only ever hold one of the two, and
    /// Escape from the deeper one would have thrown the whole palette away.
    /// </remarks>
    public void Push(string title, IReadOnlyList<PaletteEntry> entries)
    {
        if (entries.Count == 0) return;

        _overlays.Push(new Overlay(title, _query, entries));

        _query = string.Empty;
        _model.SetQuery(_query);
        _model.SetEntries(entries);

        Repaint();
    }

    /// <summary>
    /// Opens one row's whole text, broken across as many rows as it needs.
    /// </summary>
    /// <remarks>
    /// The answer to a report row being one clipped line. Rather than teaching the
    /// palette to wrap - which would mean variable row heights, a measuring layout
    /// pass and a window that resizes underneath the selection - the text becomes
    /// several ordinary rows in an ordinary frame, and Escape leaves it exactly the
    /// way Escape leaves an action list.
    /// </remarks>
    private void Expand(string whole, string title)
    {
        if (_renderer is not { } renderer) return;

        // The width a row's text actually gets, which is what it has to be broken to
        // fit. Measured against the same renderer that will draw it.
        PaletteLayout layout = Layout();
        int width = layout.RowBounds(0).Width - (layout.TextInset * 2);

        IReadOnlyList<PaletteEntry> lines =
            PaletteEntries.ForWrapped(whole, width, text => renderer.Measure(text, RowFont()).Width);

        if (lines.Count == 0) return;

        _overlays.Push(new Overlay(title, _query, lines, whole));

        _query = string.Empty;
        _model.SetQuery(_query);
        _model.SetEntries(lines);

        Repaint();
    }

    /// <summary>
    /// Shows a fetched report as a level of its own.
    /// </summary>
    /// <remarks>
    /// Called from the host once the window manager has answered. It arrives after the
    /// row that asked for it was chosen, which is why the palette does not close on
    /// that choice - there would be nothing left to show it in.
    /// </remarks>
    public void ShowReport(string title, WindowReport report)
    {
        if (!_open) return;

        Push(title, PaletteEntries.ForReport(report));
    }

    /// <summary>Says why a report could not be fetched, where the report would have been.</summary>
    public void ShowReportFailure(string title, string reason)
    {
        if (!_open) return;

        Push(title, PaletteEntries.ForReportFailure(reason));
    }

    /// <summary>Shows what can be done to the selected row.</summary>
    private void EnterActions()
    {
        if (_model.Selected?.Entry is not { Actions.Count: > 0 } entry) return;

        Push(Breadcrumb(entry.Primary), PaletteActions.AsEntries(entry.Actions));
    }

    /// <summary>The title for a list opened from a row, in context.</summary>
    private string Breadcrumb(string leaf) =>
        _overlays.Count == 0 ? leaf : $"{_overlays.Peek().Title} \u203A {leaf}";

    /// <summary>
    /// Copies the selected row, or everything on screen, to the clipboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both, because both are wanted and for different things. One row is a class name
    /// or a path going into a rule you are about to write. Everything is the whole
    /// report going into an issue, which is the single most useful thing to do with an
    /// explanation of why a window will not tile - and until now the only way to get
    /// one out of the palette was to read it off the screen and retype it.
    /// </para>
    /// <para>
    /// What text that produces is decided by <see cref="PaletteInput.CopyText"/>. The
    /// palette stays open either way: copying is not choosing, and closing on it would
    /// take away the list somebody is working through one line at a time.
    /// </para>
    /// </remarks>
    private void Copy(bool everything)
    {
        string? text = PaletteInput.CopyText(
            _model.Selected?.Entry,
            _model.Rows.Select(r => r.Entry),
            _overlays.Count > 0 ? _overlays.Peek().Whole : null,
            everything);

        if (string.IsNullOrEmpty(text)) return;

        _ = Clipboard.SetText(text, Handle);
    }

    /// <summary>
    /// Goes back one level, or closes when there is nowhere left to go.
    /// </summary>
    /// <remarks>
    /// One level at a time. Escape means "not that", and somebody who changes their
    /// mind about which workspace to tag has not changed their mind about the window
    /// they spent the search finding.
    /// </remarks>
    private void Pop()
    {
        if (_overlays.Count == 0) return;

        Overlay frame = _overlays.Pop();

        _query = frame.SavedQuery;

        if (_overlays.Count > 0)
        {
            _model.SetEntries(_overlays.Peek().Entries);
        }
        else
        {
            // Back to the mode's own list, which the host owns and restores. The
            // window never kept a copy to go stale.
            ModeChanged?.Invoke(_model.Mode);
        }

        _model.SetQuery(_query);
        Repaint();
    }

    private void Move(int delta)
    {
        _model.MoveSelection(delta);
        Repaint();
    }

    // ---- mouse -----------------------------------------------------------------

    /// <summary>
    /// Selects the row under the pointer.
    /// </summary>
    /// <remarks>
    /// Only when the pointer has actually moved. Windows sends <c>WM_MOUSEMOVE</c>
    /// whenever the window moves or is shown beneath a resting cursor, and acting on
    /// those would let a pointer that happens to be sitting over the list take the
    /// selection away from the keyboard the moment the palette appears - which is the
    /// one thing it must never do, since it opened to receive typing.
    /// </remarks>
    private void OnMouseMove(int x, int y)
    {
        if (x == _lastMouse.X && y == _lastMouse.Y) return;
        _lastMouse = (x, y);

        TrackMouseLeaving();

        (int first, int count) = _model.VisibleWindow(_scaled.VisibleRows);
        int slot = Layout().SlotAt(x, y);

        if (slot < 0 || slot >= count) return;
        if (_model.SelectedIndex == first + slot) return;

        _model.SelectAt(first + slot);
        Repaint();
    }

    private void OnClick(int x, int y)
    {
        (int first, int count) = _model.VisibleWindow(_scaled.VisibleRows);
        int slot = Layout().SlotAt(x, y);

        // A click on the chrome selects nothing rather than acting on whatever row is
        // nearest, which is what clamping would do.
        if (slot < 0 || slot >= count) return;

        _model.SelectAt(first + slot);
        Choose();
    }

    /// <summary>
    /// Scrolls the list.
    /// </summary>
    /// <remarks>
    /// Moves the selection rather than a separate scroll offset, because there is no
    /// separate scroll offset - the visible window is computed from the selection, so
    /// the two can never disagree.
    /// </remarks>
    private void OnWheel(int delta)
    {
        Move(delta > 0 ? -3 : 3);
    }

    /// <summary>Asks to be told when the pointer leaves, once.</summary>
    private unsafe void TrackMouseLeaving()
    {
        if (_trackingMouse || _handle.IsNull) return;

        var track = new TRACKMOUSEEVENT
        {
            cbSize = (uint)sizeof(TRACKMOUSEEVENT),
            dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
            hwndTrack = _handle,
        };

        _trackingMouse = PInvoke.TrackMouseEvent(ref track);
    }

    /// <summary>
    /// Deletes backwards, by character or by word.
    /// </summary>
    /// <remarks>
    /// A mode prefix is deleted as one thing rather than left behind as a lone
    /// punctuation mark, which would leave the palette in a mode the user thought
    /// they had just backed out of.
    /// </remarks>
    private void Backspace(bool wholeWord)
    {
        if (_query.Length == 0) return;

        if (wholeWord)
        {
            int cut = _query.TrimEnd().LastIndexOf(' ');
            ApplyQuery(cut <= 0 ? string.Empty : _query[..(cut + 1)]);
            return;
        }

        ApplyQuery(_query[..^1]);
    }

    private void CycleMode(bool forward)
    {
        PaletteMode[] modes = Enum.GetValues<PaletteMode>();
        int at = Array.IndexOf(modes, _model.Mode);
        int next = ((at + (forward ? 1 : -1)) % modes.Length + modes.Length) % modes.Length;

        SwitchTo(modes[next]);
    }

    /// <summary>
    /// Acts on the selected row.
    /// </summary>
    /// <remarks>
    /// What a row does is decided by <see cref="PaletteInput.Choose"/>, which is where
    /// that reasoning lives and where it is tested. This is the half that needs a
    /// window: sending, opening and closing.
    /// <para>
    /// Closed before a command is sent, deliberately. The command usually raises
    /// another window, and a palette still on screen and still topmost when that
    /// happens covers the thing the user just asked to see.
    /// </para>
    /// </remarks>
    private void Choose()
    {
        if (_model.Selected is not { } row) return;

        PaletteEntry entry = row.Entry;

        switch (PaletteInput.Choose(entry, _model.Mode, _overlays.Count > 0))
        {
            case PaletteChoice.SwitchMode:
                SwitchTo(entry.SwitchesTo!.Value);
                return;

            case PaletteChoice.Inspect:
                // The report has to be fetched, so the palette stays open and the host
                // pushes it when it arrives.
                ExplainRequested?.Invoke(entry.Explains!.Value, Breadcrumb(entry.Primary));
                return;

            case PaletteChoice.Expand:
                Expand(entry.Expands!, Breadcrumb(
                    entry.Secondary is { Length: > 0 } label ? label : entry.Primary));
                return;

            case PaletteChoice.OpenChildren:
                Push(Breadcrumb(entry.Primary), PaletteActions.AsEntries(entry.Actions!));
                return;

            case PaletteChoice.Complete:
                _query = ">" + entry.Primary + " ";
                _model.SetQuery(_query);
                Repaint();
                return;

            case PaletteChoice.Run:
                Close();
                CommandRequested?.Invoke(entry.Command);
                return;

            default:
                return;
        }
    }

    /// <summary>What the search box calls the list currently showing.</summary>
    private string? OverlayTitle() => _overlays.Count > 0 ? _overlays.Peek().Title : null;

    /// <summary>Changes mode and tells the host to refill the list.</summary>
    private void SwitchTo(PaletteMode mode)
    {
        _model.SetMode(mode);
        _query = _model.Query;

        ModeChanged?.Invoke(mode);
        Repaint();
    }

    // ---- placement -------------------------------------------------------------

    /// <summary>
    /// How tall the palette needs to be.
    /// </summary>
    /// <remarks>
    /// A search row, the results, and a hint bar. The hint bar is not optional: mode
    /// prefixes are punctuation, and punctuation nobody is shown is punctuation nobody
    /// finds.
    /// </remarks>
    private int RequiredHeight()
    {
        // Asked of a layout rather than assembled here, so the height the window is
        // given and the positions drawn inside it come from the same arithmetic.
        var probe = new PaletteLayout(_scaled, _scale, new Rect(0, 0, _scaled.Width, 0));

        return probe.ListTop + (_scaled.RowHeight * _scaled.VisibleRows) + probe.HintBar;
    }

    /// <summary>The layout for the window as it currently stands.</summary>
    private PaletteLayout Layout() =>
        new(_scaled, _scale, new Rect(0, 0, _bounds.Width, _bounds.Height));

    /// <summary>The font a row's own text is drawn in.</summary>
    /// <remarks>
    /// Built the same way the renderer builds it, from the scaled config, so text
    /// broken to fit a row is measured in the face it will actually be drawn in.
    /// </remarks>
    private FontStyle RowFont() =>
        new(_scaled.FontFamily, _scaled.FontSize, Bold: false, Italic: false);

    /// <summary>
    /// Puts the palette on the monitor the user is looking at, at that monitor's scale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A palette that always opens on the primary monitor is one that appears on a
    /// different screen from the window the user was just using, which is a strange
    /// thing to do to somebody who has asked to find something.
    /// </para>
    /// <para>
    /// Positioned, then measured, then sized. The scale factor belongs to the monitor
    /// the window is on, and there is no way to ask which that is until it is there -
    /// so the window is moved first with whatever size it currently has, the DPI is
    /// read from it, and only then is the real size worked out. Two SetWindowPos calls
    /// on a hidden window, which nobody sees and which cost nothing next to the first
    /// paint.
    /// </para>
    /// </remarks>
    private unsafe void PositionOnTargetMonitor()
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        HMONITOR monitor = TargetMonitor();

        if (!PInvoke.GetMonitorInfo(monitor, &info)) return;

        RECT work = info.rcWork;

        // Onto the monitor first, so the DPI read below is that monitor's.
        PInvoke.SetWindowPos(
            _handle, HWND.Null, work.left + 32, work.top + 32, _scaled.Width, RequiredHeight(),
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);

        uint dpi = PInvoke.GetDpiForWindow(_handle);
        _scale = dpi == 0 ? 1.0 : dpi / 96.0;

        _scaled = _config with
        {
            Width = (int)Math.Round(_config.Width * _scale),
            RowHeight = (int)Math.Round(_config.RowHeight * _scale),
            FontSize = (int)Math.Round(_config.FontSize * _scale),
        };

        int width = Math.Min(_scaled.Width, work.right - work.left - 64);
        int height = RequiredHeight();

        // A third of the way down rather than centred: the eye goes there first, and
        // it leaves the window being searched for visible underneath.
        int x = work.left + ((work.right - work.left - width) / 2);
        int y = work.top + ((work.bottom - work.top - height) / 3);

        _bounds = new Rect(x, y, width, height);

        PInvoke.SetWindowPos(
            _handle, HWND.Null, x, y, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
    }

    private HMONITOR TargetMonitor()
    {
        const MONITOR_FROM_FLAGS Nearest = MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST;

        switch (_config.Placement)
        {
            case PalettePlacement.CursorMonitor:
                return PInvoke.GetCursorPos(out System.Drawing.Point point)
                    ? PInvoke.MonitorFromPoint(point, Nearest)
                    : PInvoke.MonitorFromWindow(_handle, Nearest);

            case PalettePlacement.Primary:
                return PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);

            case PalettePlacement.FocusedMonitor:
            default:
                // Read before the palette takes the foreground, so this is still the
                // window the user was working in.
                HWND foreground = PInvoke.GetForegroundWindow();

                return foreground.IsNull
                    ? PInvoke.MonitorFromWindow(_handle, Nearest)
                    : PInvoke.MonitorFromWindow(foreground, Nearest);
        }
    }

    /// <summary>Asks the compositor for rounded corners.</summary>
    /// <remarks>
    /// Windows 11 only, and failure is ignored. Mica is deliberately not requested:
    /// the palette fills its whole client area, so a backdrop drawn behind it would
    /// never be seen, and GDI cannot leave genuinely transparent pixels without a
    /// layered window.
    /// </remarks>
    private unsafe void RoundTheCorners()
    {
        const DWMWINDOWATTRIBUTE CornerPreference = (DWMWINDOWATTRIBUTE)33;
        const int Round = 2;

        int value = Round;
        PInvoke.DwmSetWindowAttribute(_handle, CornerPreference, &value, sizeof(int));
    }

    // ---- drawing ---------------------------------------------------------------

    private void Repaint()
    {
        if (_handle.IsNull) return;

        PInvoke.InvalidateRect(_handle, (RECT?)null, false);
        PInvoke.UpdateWindow(_handle);
    }

    private void Paint()
    {
        if (_renderer is null || _bounds.Width == 0) return;

        var canvas = new Rect(0, 0, _bounds.Width, _bounds.Height);

        _renderer.BeginFrame(canvas, _scaled.Background);

        try
        {
            PaletteRenderer.Draw(_renderer, _model, _scaled, Layout(), OverlayTitle());
        }
        finally
        {
            _renderer.EndFrame();
        }
    }

    // ---- window plumbing --------------------------------------------------------

    private static string PrefixFor(PaletteMode mode) =>
        PaletteModel.PrefixFor(mode) is var prefix && prefix != '\0'
            ? prefix.ToString()
            : string.Empty;

    private static bool IsDown(VIRTUAL_KEY key) => (PInvoke.GetKeyState((int)key) & 0x8000) != 0;

    private static unsafe void EnsureClassRegistered()
    {
        if (s_classRegistered) return;

        fixed (char* className = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = HINSTANCE.Null,
                lpszClassName = className,

                // A shadow, which is most of what separates a window that floats
                // above the desktop from one painted onto it. The compositor draws
                // it, so it costs this process nothing at all - no layered window, no
                // second surface, no per-frame work.
                style = WNDCLASS_STYLES.CS_DROPSHADOW,

                // Every pixel comes from the off-screen buffer. Letting Windows erase
                // first is a visible flash on a window that opens and closes as often
                // as this one.
                hbrBackground = HBRUSH.Null,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
            };

            if (PInvoke.RegisterClassEx(in wc) == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (s_windows.TryGetValue((nint)hwnd.Value, out PaletteWindow? window))
            {
                switch (message)
                {
                    case PInvoke.WM_PAINT:
                        PInvoke.BeginPaint(hwnd, out PAINTSTRUCT ps);
                        window.Paint();
                        PInvoke.EndPaint(hwnd, in ps);
                        return new LRESULT(0);

                    case PInvoke.WM_MOUSEMOVE:
                        window.OnMouseMove((short)(lParam.Value & 0xFFFF), (short)((lParam.Value >> 16) & 0xFFFF));
                        return new LRESULT(0);

                    case PInvoke.WM_LBUTTONDOWN:
                        window.OnClick((short)(lParam.Value & 0xFFFF), (short)((lParam.Value >> 16) & 0xFFFF));
                        return new LRESULT(0);

                    case PInvoke.WM_MOUSEWHEEL:
                        window.OnWheel((short)((wParam.Value >> 16) & 0xFFFF));
                        return new LRESULT(0);

                    case PInvoke.WM_MOUSELEAVE:
                        window._trackingMouse = false;
                        return new LRESULT(0);

                    case PInvoke.WM_CHAR:
                        window.OnCharacter((char)wParam.Value);
                        return new LRESULT(0);

                    case PInvoke.WM_KEYDOWN:
                    case PInvoke.WM_SYSKEYDOWN:
                        if (window.OnKey((VIRTUAL_KEY)(ushort)wParam.Value)) return new LRESULT(0);
                        break;

                    case PInvoke.WM_ACTIVATE:
                        // WA_INACTIVE. The user clicked elsewhere, or something else
                        // took the foreground - either way the palette has been
                        // dismissed. Not when it is giving focus away itself, which
                        // produces the identical message.
                        if ((wParam.Value & 0xFFFF) == 0 && window._config.CloseOnBlur && !window._closing)
                            window.Close();

                        return new LRESULT(0);

                    case PInvoke.WM_CLOSE:
                        RequestShutdown?.Invoke();
                        return new LRESULT(0);

                    case PInvoke.WM_DESTROY:
                        s_windows.Remove((nint)hwnd.Value);
                        return new LRESULT(0);

                    default:
                        break;
                }
            }
        }
        catch
        {
            // An exception escaping an UnmanagedCallersOnly callback tears the process
            // down. A missed keystroke is better than a palette that vanishes.
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _renderer?.Dispose();

        if (!_handle.IsNull)
        {
            s_windows.Remove((nint)_handle.Value);
            PInvoke.DestroyWindow(_handle);
            _handle = HWND.Null;
        }
    }
}
