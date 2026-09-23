using System.Diagnostics;
using Dalil.Core;
using Shubbak.Companion;
using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ipc;
using Shubbak.Native;
using Shubbak.Ui.Gdi;
using Shubbak.Ui.Layout;
using Shubbak.Ui.Rendering;
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
public sealed class PaletteWindow : CompanionWindow
{
    private const string WindowClass = "DalilPaletteWindow";

    /// <summary>Posted by the IPC reader thread to wake the message loop.</summary>

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

    private GdiRenderer? _renderer;
    private Rect _bounds;
    private bool _open;

    /// <summary>How many rows the window is currently tall enough for.</summary>
    private int _rowsShown;

    /// <summary>
    /// Where "here" is, and everywhere a window could be sent.
    /// </summary>
    /// <remarks>
    /// Kept so that acting on several marked windows can build its own list without a
    /// round trip. A single row carries its actions with it, built when the row was;
    /// a set of rows has no such place to hang them, and asking the host at the moment
    /// Ctrl+Enter is pressed would put a pipe request in front of a keystroke.
    /// </remarks>
    private string? _here;
    private IReadOnlyList<string> _workspaces = [];

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
    /// <param name="SavedSelection">
    /// The row that was selected before it took over, so Escape lands back on it
    /// rather than at the top of the list.
    /// </param>
    /// <param name="Entries">The rows it shows.</param>
    /// <param name="Whole">
    /// The single value this frame is showing broken across its rows, when it is one.
    /// Set only for an expanded row: copying there has to yield the text as it was
    /// written, not as it happened to be broken to fit this window's width.
    /// </param>
    /// <param name="Confirms">
    /// Whether this frame is the one asking whether an irreversible thing should
    /// happen. Its "yes" row is itself destructive, so without knowing this the choice
    /// would be routed straight back into another confirmation, for ever.
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
        PaletteEntry? SavedSelection,
        IReadOnlyList<PaletteEntry> Entries,
        string? Whole = null,
        bool Confirms = false);

    /// <summary>Lists opened from a row, innermost last.</summary>
    private readonly Stack<Overlay> _overlays = new();

    /// <summary>When the palette was last shown, for <see cref="TimeSinceShown"/>.</summary>
    private long _shownAtTicks;

    /// <summary>How many times the foreground has been asked for since the palette was shown.</summary>
    private int _foregroundAttempts;

    /// <summary>
    /// Whether the palette has been in front at any point this showing.
    /// </summary>
    /// <remarks>
    /// The difference between a palette that was left and one that was never reached.
    /// Losing the foreground after having it is a blur, and <c>close-on-blur</c> says
    /// what to do about that; never having had it is being stranded, and a stranded
    /// palette is put away whatever the setting, because nobody can reach it.
    /// </remarks>
    private bool _hadForeground;

    /// <summary>
    /// What last kept the palette from the foreground, in words, for the log when it
    /// finally gets there or the host gives up. Null while nothing has.
    /// </summary>
    private string? _obstacle;

    /// <summary>Where the pointer was last seen, so a resting cursor is ignored.</summary>
    private (int X, int Y) _lastMouse = (int.MinValue, int.MinValue);


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

    /// <summary>
    /// Raised with a window handle and a title when a row asks for the rules that could
    /// be written for the window.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="ExplainRequested"/>, and for the same reason: the
    /// choices depend on the window's report, which has to be fetched, so the palette
    /// stays open and the host calls <see cref="ShowRuleChoices"/> when it arrives.
    /// </remarks>
    public event Action<long, string>? ComposeRequested;

    /// <summary>
    /// Raised when a row asks for its rule to be added to the configuration file.
    /// </summary>
    /// <remarks>
    /// The one thing the palette asks for that changes a file the user maintains by
    /// hand, so the row that raises it says so in its name. The window manager does
    /// the writing, validates first, and answers with what happened; the host calls
    /// <see cref="ShowRuleChange"/> with the answer, which carries the undo.
    /// </remarks>
    public event Action<RuleToAdd, string>? ApplyRuleRequested;

    /// <summary>Raised when a row asks for its rule to be taken out of the configuration file.</summary>
    public event Action<RuleToRemove, string>? RemoveRuleRequested;

    public PaletteWindow(DalilConfig config)
        : base(new WindowClassOptions(WindowClass, DropShadow: true))
    {
        _config = config;
        _scaled = config;
        _rowsShown = config.VisibleRows;
        _model.Prefixes = PalettePrefixes.With(config.Prefixes);
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

    /// <summary>The window as the palette's own manifest spells it; see <see cref="CompanionWindow.Handle"/>.</summary>
    private HWND Hwnd => new(Handle);

    /// <summary>Creates the window, hidden.</summary>
    public bool Create()
    {
        if (!CreateWindow(
                (uint)(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST),
                (uint)WINDOW_STYLE.WS_POPUP,
                "Dalil",
                new Rect(0, 0, _config.Width, HeightFor(_config.VisibleRows))))
        {
            return false;
        }

        _renderer = new GdiRenderer(Handle);

        RoundTheCorners();
        return true;
    }

    /// <summary>Supplies rows derived from the query itself.</summary>
    public void Augment(QueryAugmenter compose) => _model.Augmenter = compose;

    /// <summary>Records what the window manager last said about itself.</summary>
    public void SetStatus(WmStatus status)
    {
        _model.SetStatus(status);
        if (_open) Repaint();
    }

    /// <summary>
    /// Records where a marked window could be sent.
    /// </summary>
    /// <remarks>
    /// Refreshed with the lists, because a workspace can be created while the palette
    /// is open and a stale list would offer somewhere that no longer exists.
    /// </remarks>
    public void SetContext(string? focusedWorkspace, IReadOnlyList<string> workspaces)
    {
        _here = focusedWorkspace;
        _workspaces = workspaces ?? [];
    }

    /// <summary>Replaces the rows on offer.</summary>
    /// <remarks>
    /// Ignored while a frame is showing. Window events keep arriving whether or not
    /// the palette is busy, and a refresh landing mid-decision would replace
    /// "close it / float it / bring it here" with the window list underneath the
    /// user's finger - and Enter would then act on whatever had taken that row.
    /// </remarks>
    public void SetEntries(IEnumerable<PaletteEntry> entries)
    {
        if (_overlays.Count > 0) return;

        _model.SetEntries(entries);
        if (_open) Refreshed();
    }

    /// <summary>
    /// Types something into an open palette, as though it had been typed.
    /// </summary>
    /// <remarks>
    /// For a key that asked for an action which turns out to want an answer first. The
    /// name goes into the box so the row is already under the selection, which makes
    /// Enter the next key whether the action asked or not - the same gesture either
    /// way, rather than one that sometimes finishes and sometimes leaves you searching.
    /// </remarks>
    public void Prefill(string term)
    {
        if (!_open || _overlays.Count > 0) return;

        _model.SetQuery(_model.Query + term);
        Refreshed();
    }

    /// <summary>Applies a reloaded configuration.</summary>
    public void Reconfigure(DalilConfig config)
    {
        _config = config;
        _scaled = config;
        _model.Prefixes = PalettePrefixes.With(config.Prefixes);

        if (!config.ShowIcons) WindowIcons.Clear();

        if (_open) Refreshed();
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
        if (!Exists) return false;

        bool wasOpen = _open;

        // A fresh open is a fresh question, so anything opened last time goes - and so
        // does anything marked. Marks are a sentence somebody was in the middle of;
        // finding them still set on a palette opened an hour later, and acting on them,
        // is the worst possible way to discover the feature exists.
        _overlays.Clear();
        _model.Overlaid = false;
        _model.ClearMarks();

        _model.SetQuery(PrefixFor(mode));
        _closing = false;

        if (!wasOpen)
        {
            PositionOnTargetMonitor();

            // Stamped before the show, so the grace it starts covers the show itself.
            _shownAtTicks = Stopwatch.GetTimestamp();
            _foregroundAttempts = 0;
            _hadForeground = false;
            _obstacle = null;

            Show();
        }

        _open = true;
        Refreshed();

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
    /// same wall that stops the window manager tiling elevated windows. The final
    /// warning, when the host gives up, says so rather than leaving the user to
    /// conclude the palette is broken.
    /// </para>
    /// <para>
    /// Each attempt is written down at debug and the one that finally lands is written
    /// at info when it was not the first, naming what stood in the way. A palette that
    /// took three tries to reach the keyboard is working, and is also the only evidence
    /// there will be when somebody asks why it flickered.
    /// </para>
    /// </remarks>
    public bool EnsureForeground()
    {
        if (!Exists || !_open) return false;

        _foregroundAttempts++;

        ForegroundAttempt attempt = Foreground.Take(Hwnd);

        if (attempt.InFront)
        {
            if (_foregroundAttempts > 1)
            {
                Log.Info(LogCategory.Wm,
                    $"the palette took the keyboard on attempt {_foregroundAttempts}, " +
                    $"{TimeSinceShown.TotalMilliseconds:F0} ms after opening; before that " +
                    (_obstacle ?? "it was not in front"));
            }
            else if (attempt.Nudged)
            {
                // The second route on the first try: the ordinary one was refused, which
                // on a fresh desktop means a UWP frame was in front. Worth a line, because
                // it is the answer to "why did the first open ever fail".
                Log.Debug(LogCategory.Wm,
                    $"the palette took the keyboard after a nudge, {TimeSinceShown.TotalMilliseconds:F0} ms after opening " +
                    $"(attach {(attempt.Attached ? "ok" : "refused")}, request {(attempt.Accepted ? "accepted" : "refused")})");
            }

            _hadForeground = true;
            _obstacle = null;
            return true;
        }

        _obstacle = attempt.Describe();

        Log.Debug(LogCategory.Wm,
            $"foreground attempt {_foregroundAttempts}, {TimeSinceShown.TotalMilliseconds:F0} ms after opening: " +
            _obstacle);

        return false;
    }

    /// <summary>Whether the palette is showing but not actually receiving keys.</summary>
    /// <remarks>
    /// A palette that failed to activate never became active, so it will never be
    /// told it has been deactivated - which means close-on-blur cannot dismiss it and
    /// it sits there looking normal and answering nothing. The host polls this and
    /// puts it away rather than leaving a window nobody can reach.
    /// </remarks>
    public unsafe bool IsStranded =>
        _open && Exists && PInvoke.GetForegroundWindow() != Hwnd;

    /// <summary>How long the palette has been on screen this time; zero when it is not.</summary>
    /// <remarks>
    /// What the host judges a stranded palette by. Inside <see cref="PaletteInput.OpeningGrace"/>
    /// of being shown, not being in front is a switch that has not landed yet and is
    /// retried; after it, it is a palette nobody can reach and is put away.
    /// </remarks>
    public TimeSpan TimeSinceShown => _open ? Stopwatch.GetElapsedTime(_shownAtTicks) : TimeSpan.Zero;

    /// <summary>What stood in the way the last time the foreground was asked for and not given.</summary>
    public string LastForegroundObstacle => _obstacle ?? "unknown";

    /// <summary>Whether the palette has been in front at any point this showing.</summary>
    public bool HadForeground => _hadForeground;

    /// <summary>Whether losing the foreground dismisses the palette, as the configuration says.</summary>
    public bool ClosesOnBlur => _config.CloseOnBlur;

    /// <summary>
    /// Raised when the palette lost the foreground moments after being shown, and kept
    /// itself open on the assumption that it was taken rather than left.
    /// </summary>
    /// <remarks>
    /// The host answers by scheduling the same repairs a failed open gets. The window
    /// does not take the foreground back from inside <c>WM_ACTIVATE</c>: that message
    /// is the system part-way through handing activation to somebody else, and asking
    /// for it back in the middle of that is asking two windows to be active at once.
    /// </remarks>
    public event Action? ForegroundLost;

    /// <summary>Hides the palette.</summary>
    public void Close()
    {
        if (!_open || !Exists) return;

        // Forgotten on the way out, so the next open starts from the list rather than
        // from whatever was showing when it was dismissed.
        _overlays.Clear();
        _model.Overlaid = false;
        _model.ClearMarks();

        _closing = true;
        _open = false;

        Hide();

        Closed?.Invoke();
    }

    /// <summary>
    /// Raised when the palette has been put away, however that happened.
    /// </summary>
    /// <remarks>
    /// For the host's own per-showing state - the widened inspect list, today - which
    /// is a moment rather than a preference and should not be waiting the next time the
    /// palette opens.
    /// </remarks>
    public event Action? Closed;

    // ---- input ---------------------------------------------------------------

    /// <summary>A printable character was typed.</summary>
    /// <remarks>
    /// <para>
    /// Control characters arrive here too - Enter, Escape, Backspace all produce a
    /// <c>WM_CHAR</c> - and every one of them is handled as a key rather than as text.
    /// </para>
    /// <para>
    /// So does anything chorded with Ctrl, which is what makes Ctrl+Space a mark rather
    /// than a space. Not when Alt is also down: that combination is AltGr, and on the
    /// European layouts this whole exercise is partly for, AltGr is how <c>@</c>,
    /// <c>#</c>, <c>[</c> and <c>{</c> are typed at all. Filtering on Ctrl alone would
    /// have made the palette unable to search for an email address on a German
    /// keyboard.
    /// </para>
    /// </remarks>
    private void OnCharacter(char value)
    {
        if (char.IsControl(value)) return;
        if (IsDown(VIRTUAL_KEY.VK_CONTROL) && !IsDown(VIRTUAL_KEY.VK_MENU)) return;

        PaletteMode before = _model.Mode;

        // Not simply appended: a prefix typed while there is nothing to search replaces
        // the mode rather than being searched for. See PaletteModel.AfterTyping.
        _model.Insert(value);

        Announce(before);
    }

    /// <summary>
    /// Tells the host when a mode change fell out of an edit.
    /// </summary>
    /// <remarks>
    /// The single place a mode change is noticed, so no route into a mode can forget
    /// to refill the list. There are five: Tab, a jump key, typing a prefix, deleting
    /// one, and choosing a mode from the help list.
    /// </remarks>
    private void Announce(PaletteMode before)
    {
        if (_model.Mode != before) ModeChanged?.Invoke(_model.Mode);

        Refreshed();
    }

    /// <summary>A key that means something other than a character.</summary>
    /// <returns>Whether the key was handled here.</returns>
    private bool OnKey(VIRTUAL_KEY key)
    {
        bool control = IsDown(VIRTUAL_KEY.VK_CONTROL);
        bool shift = IsDown(VIRTUAL_KEY.VK_SHIFT);
        bool alt = IsDown(VIRTUAL_KEY.VK_MENU);

        // Before everything, because a digit is otherwise just a character and would
        // be typed into the box. Ctrl+Alt is AltGr and belongs to the text.
        if (!alt && PaletteInput.JumpFor(key, control) is { } jump)
        {
            SwitchTo(jump);
            return true;
        }

        switch (key)
        {
            case VIRTUAL_KEY.VK_ESCAPE:
                // Backs out of the frame rather than dismissing. Escape means "not
                // that" one level at a time; throwing the whole palette away because
                // somebody changed their mind about an action would mean starting the
                // search again.
                if (_overlays.Count > 0) Pop();
                else Close();

                return true;

            case VIRTUAL_KEY.VK_RETURN when alt:
                return TryChord(key, control, shift, alt);

            case VIRTUAL_KEY.VK_RETURN when control:
                EnterActions();
                return true;

            case VIRTUAL_KEY.VK_RETURN:
                Choose();
                return true;

            case VIRTUAL_KEY.VK_SPACE when control && !alt:
                Mark();
                return true;

            case VIRTUAL_KEY.VK_C when control:
                Copy(everything: shift);
                return true;

            case VIRTUAL_KEY.VK_BACK:
                // Empty and inside a frame, Backspace has nothing to delete, so it
                // goes back instead. Escape already did, but Backspace is what a hand
                // reaches for when the thing on screen arrived by pressing Enter -
                // and doing nothing at all was the least useful of the three options.
                if (_model.Query.Length == 0 && _overlays.Count > 0) Pop();
                else Edit(() => _model.DeleteBack(wholeWord: control));

                return true;

            case VIRTUAL_KEY.VK_DELETE:
                Edit(_model.DeleteForward);
                return true;

            case VIRTUAL_KEY.VK_LEFT:
                _model.MoveCaret(-1);
                Repaint();
                return true;

            case VIRTUAL_KEY.VK_RIGHT:
                _model.MoveCaret(1);
                Repaint();
                return true;

            case VIRTUAL_KEY.VK_HOME when !control:
                _model.CaretToEdge(end: false);
                Repaint();
                return true;

            case VIRTUAL_KEY.VK_END when !control:
                _model.CaretToEdge(end: true);
                Repaint();
                return true;

            case VIRTUAL_KEY.VK_UP:
                Move(-1);
                return true;

            case VIRTUAL_KEY.VK_DOWN:
                Move(1);
                return true;

            case VIRTUAL_KEY.VK_PRIOR:
                Move(-_rowsShown);
                return true;

            case VIRTUAL_KEY.VK_NEXT:
                Move(_rowsShown);
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
                SwitchTo(_model.NextMode(forward: !shift));
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
                Edit(_model.ClearTerm);
                return true;

            default:
                return TryChord(key, control, shift, alt);
        }
    }

    /// <summary>Runs an edit and reports whatever it did to the mode.</summary>
    private void Edit(Action edit)
    {
        PaletteMode before = _model.Mode;

        edit();

        Announce(before);
    }

    /// <summary>
    /// Marks or unmarks the selected window.
    /// </summary>
    /// <remarks>
    /// Ctrl+Space rather than Space, which is a character somebody is very likely to
    /// be typing: window titles are full of them. Moving down afterwards, so marking
    /// six windows in a row is six presses of one chord rather than twelve keystrokes
    /// alternating with the arrow key.
    /// </remarks>
    private void Mark()
    {
        if (!_model.ToggleMark()) return;

        _model.MoveSelection(1);
        Repaint();
    }

    /// <summary>
    /// Acts on a chord, from the main list or from inside a frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always, now. The chords used to be gated behind <c>action-guard</c>, whose
    /// default disabled every one of them except inspecting - while the action list
    /// went on printing them as badges beside the rows they belonged to. So the keys
    /// were advertised in the one place they were redundant, and refused in the only
    /// place they would have saved anything.
    /// </para>
    /// <para>
    /// What replaced the guard is narrower and stricter: the two actions that cannot be
    /// undone ask first, by whichever route they were reached. Closing a window is now
    /// harder than it was with the guard on, and floating one is eight keystrokes
    /// easier.
    /// </para>
    /// <para>
    /// The two lookups look in different places for the same thing. From the main list
    /// the chords belong to the selected row's actions; inside a frame the rows are
    /// those actions, and each carries its own.
    /// </para>
    /// </remarks>
    private bool TryChord(VIRTUAL_KEY key, bool control, bool shift, bool alt)
    {
        if (PaletteInput.ChordFor(key, control, shift, alt) is not { } wanted) return false;

        if (_overlays.Count > 0)
        {
            // The frame's own rows rather than the filtered ones. A chord names the
            // action, not whatever happens to have survived what is typed in the box.
            PaletteEntry? row = _overlays.Peek().Entries
                .FirstOrDefault(e => string.Equals(e.Chord, wanted, StringComparison.Ordinal));

            return row is not null && Act(row, row.Primary);
        }

        if (_model.Selected is not { } selected) return false;
        if (selected.Entry.ResolveActions() is not { Count: > 0 } actions) return false;
        if (actions.FirstOrDefault(a => a.Chord == wanted) is not { } action) return false;

        return Act(
            new PaletteEntry(
                action.Name, action.Description, [], action.Command,
                Explains: action.Explains, Expands: action.Expands,
                Destructive: action.Destructive, Copies: action.Copies,
                Applies: action.Applies, Removes: action.Removes, Composes: action.Composes),
            selected.Entry.Primary);
    }

    /// <summary>Does what a chord selected, and says whether anything happened.</summary>
    /// <remarks>
    /// Explaining leaves the palette open, exactly as choosing the same row from the
    /// list does: the report is fetched and needs somewhere to arrive. So does asking
    /// about something irreversible. Everything else closes first, because the command
    /// usually raises another window and a palette still topmost would cover the thing
    /// that was just asked for.
    /// </remarks>
    private bool Act(PaletteEntry action, string leaf)
    {
        if (action.Explains is { } handle)
        {
            ExplainRequested?.Invoke(handle, Breadcrumb(leaf));
            return true;
        }

        if (action.Copies is { Length: > 0 } text)
        {
            CopyAndClose(text);
            return true;
        }

        if (action.Applies is { } addition)
        {
            ApplyRuleRequested?.Invoke(addition, Breadcrumb(leaf));
            return true;
        }

        if (action.Removes is { } removal)
        {
            RemoveRuleRequested?.Invoke(removal, Breadcrumb(leaf));
            return true;
        }

        if (action.Composes is { } subject)
        {
            ComposeRequested?.Invoke(subject, Breadcrumb(leaf));
            return true;
        }

        if (action.Expands is { Length: > 0 } whole)
        {
            Expand(whole, Breadcrumb(action.Primary));
            return true;
        }

        if (action.Command.Length == 0) return false;

        if (action.Destructive && _config.ConfirmDestructive)
        {
            Confirm(action.Primary, action.Command, leaf);
            return true;
        }

        Send(action.Command);
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
    public void Push(string title, IReadOnlyList<PaletteEntry> entries) =>
        Push(title, entries, whole: null, confirms: false);

    private void Push(
        string title, IReadOnlyList<PaletteEntry> entries, string? whole, bool confirms)
    {
        if (entries.Count == 0) return;

        _overlays.Push(new Overlay(title, _model.Query, _model.Selected?.Entry, entries, whole, confirms));

        // Told before the rows are installed, so the derived rows for the list
        // underneath are not appended to this frame; see PaletteModel.Overlaid.
        _model.Overlaid = true;
        _model.SetQuery(string.Empty);
        _model.SetEntries(entries);

        Refreshed();
    }

    /// <summary>Asks whether something irreversible should really happen.</summary>
    private void Confirm(string what, string command, string subject) =>
        Push(Breadcrumb(subject), PaletteActions.Confirmation(what, command), whole: null, confirms: true);

    /// <summary>
    /// Opens one row's whole text, broken across as many rows as it needs.
    /// </summary>
    /// <remarks>
    /// The answer to a report row being one clipped line, and to a composed rule being
    /// twelve of them. Rather than teaching the palette to wrap - which would mean
    /// variable row heights, a measuring layout pass and a window that resizes
    /// underneath the selection - the text becomes several ordinary rows in an ordinary
    /// frame, and Escape leaves it the way Escape leaves an action list.
    /// </remarks>
    private void Expand(string whole, string title)
    {
        if (_renderer is not { } renderer) return;

        // The width a row's text actually gets, which is what it has to be broken to
        // fit. Measured against the same renderer that will draw it.
        PaletteLayout layout = Layout();
        int width = layout.RowBounds(0).Width - layout.RowTextInset - layout.TextInset;

        IReadOnlyList<PaletteEntry> lines =
            PaletteEntries.ForWrapped(whole, width, text => renderer.Measure(text, RowFont()).Width);

        if (lines.Count == 0) return;

        Push(title, lines, whole, confirms: false);
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

        Push(title, PaletteEntries.ForReport(report, _here, _workspaces));
    }

    /// <summary>Says why a report could not be fetched, where the report would have been.</summary>
    public void ShowReportFailure(string title, string reason)
    {
        if (!_open) return;

        Push(title, PaletteEntries.ForReportFailure(reason));
    }

    /// <summary>
    /// Shows the rules that could be written for a window, once its report has arrived.
    /// </summary>
    /// <remarks>
    /// The same arrangement as <see cref="ShowReport"/>: the row that asked was chosen
    /// a moment ago and the palette stayed open for this. The workspaces are the
    /// palette's own, read when it opened, so "send it to" names the same list every
    /// other picker does.
    /// </remarks>
    public void ShowRuleChoices(string title, WindowReport report)
    {
        if (!_open) return;

        Push(title, PaletteActions.AsEntries(PaletteActions.RuleChoices(report, _here, _workspaces)));
    }

    /// <summary>
    /// Shows what adding or removing a rule did, with the way back.
    /// </summary>
    /// <remarks>
    /// Pushed on top of whatever asked, rather than replacing it, so Escape returns to
    /// the list of rules and the frame beneath that to the report - the same route
    /// back as everywhere else.
    /// </remarks>
    public void ShowRuleChange(string title, RuleChange change, bool added)
    {
        if (!_open) return;

        Push(title, PaletteEntries.ForRuleChange(change, added));
    }

    /// <summary>
    /// Shows what can be done to the selected row, or to everything marked.
    /// </summary>
    /// <remarks>
    /// Marks win. Somebody who has marked four windows and pressed Ctrl+Enter is asking
    /// about the four, not about whichever one the selection happens to be resting on -
    /// and if they were not, the count in the corner has been telling them so since the
    /// first mark.
    /// </remarks>
    private void EnterActions()
    {
        if (_model.MarkedCount > 0)
        {
            List<string> targets = [.. _model.Marked.Select(e => e.Target!).Where(t => t is not null)];

            IReadOnlyList<PaletteAction> bulk = PaletteActions.ForMany(targets, _here, _workspaces);
            if (bulk.Count == 0) return;

            List<PaletteEntry> rows = [.. PaletteActions.AsEntries(bulk)];

            // The way out. Marks are otherwise cleared only by unmarking each one or by
            // dismissing the palette, and somebody who has changed their mind about a
            // set of six should not have to do either.
            rows.Add(new PaletteEntry(
                "Clear the marks",
                "Leave every window alone",
                [],
                PaletteEntries.BuiltinClearMarks,
                Rank: -1));

            string many = targets.Count == 1 ? "1 window" : $"{targets.Count} windows";

            Push(many, rows);
            return;
        }

        if (_model.Selected?.Entry is not { HasActions: true } entry) return;

        Push(Breadcrumb(entry.Primary), PaletteActions.AsEntries(entry.ResolveActions()));
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
    /// A window row is copied with its dim half attached, because the reason to copy
    /// one is almost always to put its class or its process into a rule and both of
    /// those live there. Copying the title alone handed over the one attribute
    /// guaranteed to be the wrong thing to match on.
    /// </para>
    /// </remarks>
    private void Copy(bool everything)
    {
        string? text = everything
            ? PaletteInput.CopyText(
                _model.Selected?.Entry,
                _model.Rows.Select(r => r.Entry),
                _overlays.Count > 0 ? _overlays.Peek().Whole : null,
                everything: true)
            : _model.Selected?.Entry is { } row
                ? PaletteInput.DescribeForClipboard(row)
                : null;

        if (string.IsNullOrEmpty(text)) return;

        _ = Clipboard.SetText(text, Handle);
    }

    /// <summary>
    /// Copies what a row was chosen for, and goes away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Closing is the confirmation. Every other action that does what it was asked
    /// closes the palette, and a palette that stayed open after "Copy the rule" would
    /// leave the user wondering whether it had - the clipboard being the one place a
    /// program can put something with nothing on screen to show for it.
    /// </para>
    /// <para>
    /// Failing is different. The clipboard can be held by another program for longer
    /// than the retries wait, and a palette that vanished then would have reported
    /// success by vanishing. So it stays, and says.
    /// </para>
    /// </remarks>
    private void CopyAndClose(string text)
    {
        if (Clipboard.SetText(text, Handle))
        {
            Close();
            return;
        }

        Push(
            Breadcrumb("copy"),
            PaletteEntries.ForReportFailure(
                "Nothing was copied - the clipboard would not take it. Another program may be holding it; try again."));
    }

    /// <summary>Puts text on the clipboard, on behalf of the host.</summary>
    /// <remarks>
    /// The window is the owner because a clipboard needs one, and the host does not
    /// have a window. Everything the palette itself copies goes through
    /// <see cref="Copy"/>; this is for the rows the host answers, where the thing worth
    /// copying - the path of the file in effect - is something only the host knows.
    /// </remarks>
    public bool CopyToClipboard(string text) => Clipboard.SetText(text, Handle);

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

        if (_overlays.Count > 0)
        {
            _model.SetEntries(_overlays.Peek().Entries);
            _model.SetQuery(frame.SavedQuery);
        }
        else
        {
            // Back to the mode's own list, which the host owns and restores. The
            // window never kept a copy to go stale. Told before the query goes back,
            // so the derived rows the list carries are computed again.
            _model.Overlaid = false;
            _model.SetQuery(frame.SavedQuery);
            ModeChanged?.Invoke(_model.Mode);
        }

        // The query going back resets the selection to the top, as typing should; a
        // frame closing should put it back where it was.
        _model.Reselect(frame.SavedSelection);

        Refreshed();
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
    protected override void OnMouseMove(int x, int y)
    {
        if (x == _lastMouse.X && y == _lastMouse.Y) return;
        _lastMouse = (x, y);

        (int first, int count) = _model.VisibleWindow(_rowsShown);
        int slot = Layout().SlotAt(x, y);

        if (slot < 0 || slot >= count) return;
        if (_model.SelectedIndex == first + slot) return;

        _model.SelectAt(first + slot);
        Repaint();
    }

    protected override void OnMouseDown(MouseButton button, int x, int y)
    {
        if (button != MouseButton.Left) return;

        (int first, int count) = _model.VisibleWindow(_rowsShown);
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
    protected override void OnWheel(int delta) => Move(delta > 0 ? -3 : 3);


    /// <summary>
    /// Acts on the selected row.
    /// </summary>
    /// <remarks>
    /// What a row does is decided by <see cref="PaletteInput.Choose"/>, which is where
    /// that reasoning lives and where it is tested. This is the half that needs a
    /// window: sending, opening and closing.
    /// </remarks>
    private void Choose()
    {
        if (_model.Selected is not { } row) return;

        PaletteEntry entry = row.Entry;

        bool inside = _overlays.Count > 0;
        bool confirming = inside && _overlays.Peek().Confirms;

        // Not inside the frame that is already asking. Its "yes" row is itself marked
        // destructive - which is what draws it in the warning colour - so routing it
        // back through the same test would ask the same question for ever.
        bool confirm = _config.ConfirmDestructive && !confirming;

        switch (PaletteInput.Choose(entry, _model.Mode, inside, confirm))
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

            case PaletteChoice.Copy:
                CopyAndClose(entry.Copies!);
                return;

            case PaletteChoice.Apply:
                // Fetched, like a report: the window manager edits the file and says
                // what happened, and the answer needs somewhere to arrive.
                ApplyRuleRequested?.Invoke(entry.Applies!, Breadcrumb(entry.Primary));
                return;

            case PaletteChoice.Remove:
                RemoveRuleRequested?.Invoke(entry.Removes!, Breadcrumb(entry.Primary));
                return;

            case PaletteChoice.Compose:
                ComposeRequested?.Invoke(entry.Composes!.Value, Breadcrumb(entry.Primary));
                return;

            case PaletteChoice.OpenChildren:
                Push(Breadcrumb(entry.Primary), PaletteActions.AsEntries(entry.ResolveActions()));
                return;

            case PaletteChoice.Complete:
                _model.SetQuery(">" + entry.Primary + " ");
                Refreshed();
                return;

            case PaletteChoice.Confirm:
                Confirm(entry.Primary, entry.Command, entry.Primary);
                return;

            case PaletteChoice.Run:
                Send(entry.Command);
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// Sends a command, or handles the ones the palette answers itself.
    /// </summary>
    /// <remarks>
    /// Closed before a command goes out, deliberately. The command usually raises
    /// another window, and a palette still on screen and still topmost when that
    /// happens covers the thing the user just asked to see.
    /// <para>
    /// Clearing the marks is the exception that stays open: it is a correction, and
    /// dismissing the palette because somebody corrected themselves would throw away
    /// the search that got them there.
    /// </para>
    /// </remarks>
    private void Send(string command)
    {
        if (string.Equals(command, PaletteEntries.BuiltinClearMarks, StringComparison.Ordinal))
        {
            _model.ClearMarks();
            Pop();
            return;
        }

        // Widening the list is a correction too, and one the host answers: the palette
        // stays where it is and the host refills the list it is looking at.
        if (PaletteEntries.StaysOpen(command))
        {
            CommandRequested?.Invoke(command);
            return;
        }

        Close();
        CommandRequested?.Invoke(command);
    }

    /// <summary>What the search box calls the list currently showing.</summary>
    private string? OverlayTitle() => _overlays.Count > 0 ? _overlays.Peek().Title : null;

    /// <summary>Changes mode and tells the host to refill the list.</summary>
    private void SwitchTo(PaletteMode mode)
    {
        // A frame is about one row of the list underneath it. Changing which list that
        // is while a report or an action list is open would leave the frame describing
        // something that is no longer there.
        _overlays.Clear();
        _model.Overlaid = false;

        _model.SetMode(mode);

        ModeChanged?.Invoke(mode);
        Refreshed();
    }

    // ---- placement -------------------------------------------------------------

    /// <summary>
    /// How tall the palette needs to be for a given number of rows.
    /// </summary>
    /// <remarks>
    /// A search row, the results, and a hint bar. The hint bar is not optional: mode
    /// prefixes are punctuation, and punctuation nobody is shown is punctuation nobody
    /// finds.
    /// </remarks>
    private int HeightFor(int rows)
    {
        // Asked of a layout rather than assembled here, so the height the window is
        // given and the positions drawn inside it come from the same arithmetic.
        var probe = new PaletteLayout(_scaled, _scale, new Rect(0, 0, _scaled.Width, 0));

        return probe.ListTop + (_scaled.RowHeight * Math.Max(1, rows)) + probe.HintBar;
    }

    /// <summary>
    /// Resizes to fit what is being shown, then repaints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A search that matched two things used to be drawn as two rows above ten rows of
    /// empty background, which reads as the window having failed to finish drawing.
    /// </para>
    /// <para>
    /// Only when the number of rows has actually changed, which is the whole of the
    /// cost control: typing narrows the list a row at a time, so this fires perhaps
    /// four times over a word rather than on every keystroke, and a
    /// <c>SetWindowPos</c> on a small popup with no move, no z-order change and no
    /// activation is a few microseconds. The window grows downwards from a fixed top
    /// edge, so nothing the eye is reading moves.
    /// </para>
    /// </remarks>
    private void Refreshed()
    {
        if (_open && _config.ShrinkToFit && Exists)
        {
            int rows = _model.RowsToShow(_scaled.VisibleRows);

            if (rows != _rowsShown)
            {
                _rowsShown = rows;

                int height = HeightFor(rows);
                _bounds = new Rect(_bounds.X, _bounds.Y, _bounds.Width, height);

                PInvoke.SetWindowPos(
                    Hwnd, HWND.Null, 0, 0, _bounds.Width, height,
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE);
            }
        }

        Repaint();
    }

    /// <summary>The layout for the window as it currently stands.</summary>
    private PaletteLayout Layout() =>
        new(_scaled, _scale, new Rect(0, 0, _bounds.Width, _bounds.Height), _config.ShowIcons);

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
            Hwnd, HWND.Null, work.left + 32, work.top + 32, _scaled.Width, HeightFor(_rowsShown),
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);

        uint dpi = Dpi;
        _scale = dpi == 0 ? 1.0 : dpi / 96.0;

        _scaled = _config with
        {
            Width = (int)Math.Round(_config.Width * _scale),
            RowHeight = (int)Math.Round(_config.RowHeight * _scale),
            FontSize = (int)Math.Round(_config.FontSize * _scale),
        };

        // The full height, whatever is about to be shown in it. The list is filled in
        // straight afterwards and will shrink the window if it needs to; starting from
        // the largest size means the top edge is placed once, for the tallest the
        // window can be, and never moves again.
        _rowsShown = _scaled.VisibleRows;

        int width = Math.Min(_scaled.Width, work.right - work.left - 64);
        int height = HeightFor(_rowsShown);

        // A third of the way down rather than centred: the eye goes there first, and
        // it leaves the window being searched for visible underneath.
        int x = work.left + ((work.right - work.left - width) / 2);
        int y = work.top + ((work.bottom - work.top - height) / 3);

        _bounds = new Rect(x, y, width, height);

        PInvoke.SetWindowPos(
            Hwnd, HWND.Null, x, y, width, height,
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
                    : PInvoke.MonitorFromWindow(Hwnd, Nearest);

            case PalettePlacement.Primary:
                return PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);

            case PalettePlacement.FocusedMonitor:
            default:
                // Read before the palette takes the foreground, so this is still the
                // window the user was working in.
                HWND foreground = PInvoke.GetForegroundWindow();

                return foreground.IsNull
                    ? PInvoke.MonitorFromWindow(Hwnd, Nearest)
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
        PInvoke.DwmSetWindowAttribute(Hwnd, CornerPreference, &value, sizeof(int));
    }

    // ---- drawing ---------------------------------------------------------------

    protected override void OnPaint()
    {
        if (_renderer is null || _bounds.Width == 0) return;

        var canvas = new Rect(0, 0, _bounds.Width, _bounds.Height);

        _renderer.BeginFrame(canvas, _scaled.Background);

        try
        {
            PaletteRenderer.Draw(
                _renderer, _model, _scaled, Layout(),
                new PaletteChrome(
                    OverlayTitle(),

                    // A pure dictionary read. Nothing on the paint path is allowed to
                    // ask another process anything - see WindowIcons.
                    _config.ShowIcons ? WindowIcons.Get : null,
                    _config.ShowIcons ? _renderer : null,
                    OverlayIsExpanded: _overlays.Count > 0 && _overlays.Peek().Whole is { Length: > 0 }));
        }
        finally
        {
            _renderer.EndFrame();
        }
    }

    // ---- window plumbing --------------------------------------------------------

    private string PrefixFor(PaletteMode mode) =>
        _model.Prefixes.PrefixFor(mode) is var prefix && prefix != '\0'
            ? prefix.ToString()
            : string.Empty;

    private static bool IsDown(VIRTUAL_KEY key) => (PInvoke.GetKeyState((int)key) & 0x8000) != 0;

    /// <summary>
    /// The messages the palette answers itself: the keyboard, and the activation
    /// message that says whether it still has it. Painting, the pointer, closing and
    /// the session ending are the base class's.
    /// </summary>
    protected override unsafe bool OnMessage(uint message, nuint wParam, nint lParam, out nint result)
    {
        result = 0;

        switch (message)
        {
            case PInvoke.WM_CHAR:
                OnCharacter((char)wParam);
                return true;

            case PInvoke.WM_KEYDOWN:
            case PInvoke.WM_SYSKEYDOWN:
                return OnKey((VIRTUAL_KEY)(ushort)wParam);

            case PInvoke.WM_ACTIVATE:
            {
                // WA_ACTIVE or WA_CLICKACTIVE. Remembered as having been in front
                // only when the system agrees: this thread's own SetActiveWindow
                // produces the same message whether or not the foreground moved,
                // and a palette that was never in front must not be mistaken for
                // one that was and then left.
                if ((wParam & 0xFFFF) != 0)
                {
                    if (PInvoke.GetForegroundWindow() == Hwnd) _hadForeground = true;
                    return true;
                }

                // WA_INACTIVE. Not when the palette is giving focus away itself,
                // which produces the identical message.
                if (_closing) return true;

                // Who has it now. The message names the window being activated
                // only when it is on this thread; across threads - every case
                // that matters here - the handle is null and the answer is read
                // from the system instead.
                nint taker = lParam != 0 ? lParam : (nint)PInvoke.GetForegroundWindow().Value;
                string holder = Foreground.Describe(taker);

                // In the first moments after opening, whatever close-on-blur says.
                // Nobody clicks away from a palette they asked for a quarter of a
                // second ago; a deactivation that early is the previous window's
                // thread finishing an activation the system had already queued,
                // or a window manager pass putting focus where it thinks it goes,
                // and the palette is the thing the user wanted. It stays, says so,
                // and asks the host to take the foreground back.
                if (PaletteInput.IsSettlingIn(TimeSinceShown))
                {
                    _obstacle = $"the foreground went to {holder}";

                    Log.Info(LogCategory.Wm,
                        $"the palette lost the foreground to {holder} " +
                        $"{TimeSinceShown.TotalMilliseconds:F0} ms after opening; taking it back");

                    ForegroundLost?.Invoke();
                    return true;
                }

                // The user clicked elsewhere, or something else took the
                // foreground - either way the palette has been left, and
                // close-on-blur says whether that dismisses it. Said at the
                // default level and naming the taker: one line per dismissal,
                // and the only evidence there is when the palette is reported
                // to have closed by itself.
                if (_config.CloseOnBlur)
                {
                    Log.Info(LogCategory.Wm,
                        $"put away: the foreground went to {holder} " +
                        $"{TimeSinceShown.TotalMilliseconds:F0} ms after opening");

                    Close();
                }

                return true;
            }

            default:
                return false;
        }
    }

    public override void Dispose()
    {
        if (!Exists) return;

        _renderer?.Dispose();
        _renderer = null;

        base.Dispose();
    }
}