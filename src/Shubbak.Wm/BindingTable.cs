using Shubbak.Config;
using Shubbak.Native;

namespace Shubbak.Wm;

/// <summary>The command and mode captured by the hook, before either can change.</summary>
public sealed class BoundShortcut(Keybinding binding, string? mode)
    : KeyboardBinding(binding.Release, binding.RepeatsOnHold,
        (KeyModifiers)(KeyParser.BasicMask | (binding.Key.Modifiers & KeyParser.SideMask)))
{
    public Keybinding Binding { get; } = binding;
    public string? Mode { get; } = mode;
}

/// <summary>
/// Matches keystrokes to bindings, honouring the active binding mode.
/// </summary>
/// <remarks>
/// <para>
/// The lookup runs <b>inside</b> the low-level keyboard hook callback, so it must
/// be allocation-free and return in microseconds - anything slower risks the 300 ms
/// unhook threshold that silently disables every binding
/// (docs/adr/0001-language-choice.md, constraint 1).
/// </para>
/// <para>
/// The table is therefore built once, up front, and rebuilt wholesale on config
/// reload. Probing is a single dictionary lookup on a packed integer key, with no
/// allocation on any path.
/// </para>
/// </remarks>
public sealed class BindingTable
{
    /// <summary>
    /// Everything a lookup needs, published as one reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These were four separate fields, written one after another by a reload on the
    /// daemon thread and read on the keyboard hook thread with nothing ordering the
    /// two. The hook could therefore see a mixture: the new default table alongside
    /// the previous active mode, or - worse - the window between clearing the active
    /// mode and re-selecting it, during which every keystroke resolved against the
    /// defaults instead of the mode.
    /// </para>
    /// <para>
    /// That is not theoretical. Reloading while a non-pass-through mode is active
    /// meant keystrokes briefly stopped being swallowed, which for a <c>pause</c>
    /// mode is exactly the behaviour it exists to prevent.
    /// </para>
    /// <para>
    /// Marking each field <c>volatile</c> would not have fixed it. Four volatile
    /// fields are still four writes, and consistency <i>between</i> them is the
    /// property that matters. One immutable record behind one volatile reference
    /// makes a reload a single publication: the hook reads the reference once and
    /// everything it then looks at belongs together.
    /// </para>
    /// </remarks>
    private sealed record Snapshot(
        KeyMap Default,
        Dictionary<string, ModeTable> Modes,
        ModeTable? ActiveMode,
        string? ActiveModeName);

    private volatile Snapshot _state = new(
        new KeyMap([], KeyParser.BasicMask),
        new Dictionary<string, ModeTable>(StringComparer.OrdinalIgnoreCase),
        null,
        null);

    private sealed record KeyMap(Dictionary<int, BoundShortcut> Bindings, int ModifierMask);
    private sealed record ModeTable(KeyMap Keys, bool PassThrough);

    private static KeyMap Compile(IReadOnlyList<Keybinding> defaults, IReadOnlyList<Keybinding> overlay, string? mode)
    {
        var compiled = BindingChords.Compile(defaults, overlay, b => new BoundShortcut(b, mode));
        return new KeyMap(compiled.Bindings, compiled.ModifierMask);
    }

    /// <summary>
    /// Rebuilds the table from config, keeping the active mode if it still exists.
    /// </summary>
    /// <returns>
    /// The name of the active mode that the new config no longer declares, or null if
    /// nothing was lost - either because no mode was active or because it survived.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This used to drop the active mode on the floor and tell nobody. The lookup
    /// table went back to the default bindings, which is the safe half, while
    /// <c>WindowManager.BindingMode</c> kept whatever it had - so after reloading
    /// inside a mode called <c>pause</c>, the keyboard was on the default bindings
    /// while <c>diagnose</c>, the bar and the state machine all still said
    /// <c>pause</c>. Three surfaces describing a state the keyboard was not in.
    /// </para>
    /// <para>
    /// It then got worse if the user tried the obvious remedy.
    /// <c>SetBindingMode</c> short-circuits when the name is unchanged, so pressing
    /// the key that enables <c>pause</c> found it already active, emitted no event,
    /// and never reached this table. <b>The mode could not be entered again</b> until
    /// <c>wm-disable-binding-mode</c> was run - which looked like it did nothing, and
    /// was the thing that fixed it.
    /// </para>
    /// <para>
    /// Keeping the mode is also the better behaviour on its own terms: a reload is not
    /// a request to leave the mode you are in.
    /// </para>
    /// </remarks>
    public string? Load(ShubbakConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        Dictionary<string, ModeTable> modes = new(StringComparer.OrdinalIgnoreCase);
        foreach (BindingMode mode in config.BindingModes)
        {
            modes[mode.Name] = new ModeTable(Compile(mode.Keybindings, [], mode.Name), mode.PassThrough);
        }

        string? wasActive = _state.ActiveModeName;

        // Re-selected against the new tables before anything is published, so the hook
        // never observes the gap between losing the old mode and regaining the new one.
        ModeTable? active = wasActive is not null && modes.TryGetValue(wasActive, out ModeTable? kept)
            ? kept
            : null;

        // IReadOnlyList can still be a caller-owned mutable List. Later overlay
        // compilation must use the defaults that were actually loaded.
        _baseDefaults = config.Keybindings.ToArray();
        _baseMap = Compile(_baseDefaults, [], null);

        // The overlay in force survives a reload, as the mode does: a reload is not a
        // request to leave the context you are in. The daemon re-applies the effective
        // config straight after, which replaces it anyway.
        _state = new Snapshot(Merge(_overlay), modes, active, active is null ? null : wasActive);

        // A mode that has been deleted from the config leaves the keyboard on the
        // defaults, and everything that reports the mode has to be told so.
        return wasActive is not null && active is null ? wasActive : null;
    }

    /// <summary>The default bindings as the config wrote them, before any overlay.</summary>
    private Keybinding[] _baseDefaults = [];
    private KeyMap _baseMap = new([], KeyParser.BasicMask);

    /// <summary>The bindings the active contexts lay over the defaults, in order.</summary>
    private Keybinding[] _overlay = [];

    /// <summary>
    /// Lays a set of bindings over the default table, or takes the overlay off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Merged here, once, into the table the hook reads - not consulted by the hook as a
    /// second table. The callback does one dictionary lookup per key-down today and
    /// does one after this; a context flipping costs a rebuild of a few hundred entries
    /// on the daemon thread and the hook thread nothing at all. That is rule three in
    /// <c>ideas/contexts.md</c>, and this is where it is kept.
    /// </para>
    /// <para>
    /// Later entries win on the same key, so the caller's order is the cascade's order.
    /// A binding with no commands claims the key and does nothing when it fires, which
    /// is how a context disarms a key: the chord is swallowed rather than reaching the
    /// application, and nothing runs.
    /// </para>
    /// <para>
    /// The overlay applies to the default table only. Inside a binding mode the mode's
    /// table is the whole keyboard, as it always was; a mode that wants a context's
    /// key can bind it itself.
    /// </para>
    /// </remarks>
    public void SetOverlay(IReadOnlyList<Keybinding> overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        // Re-submitting the same list can carry new contents. Snapshot them here,
        // not during a later reload, and never publish caller-owned storage.
        _overlay = overlay.Count == 0 ? [] : overlay.ToArray();

        Snapshot state = _state;
        _state = state with { Default = Merge(_overlay) };
    }

    private KeyMap Merge(Keybinding[] overlay) =>
        overlay.Length == 0 ? _baseMap : Compile(_baseDefaults, overlay, null);

    /// <summary>How many bindings the overlay in force contributes.</summary>
    public int OverlayCount => _overlay.Length;

    /// <summary>Switches binding mode; null returns to the default set.</summary>
    /// <returns>False when no such mode is declared, in which case nothing changed.</returns>
    public bool SetMode(string? mode)
    {
        Snapshot state = _state;

        if (mode is null)
        {
            _state = state with { ActiveMode = null, ActiveModeName = null };
            return true;
        }

        if (!state.Modes.TryGetValue(mode, out ModeTable? table)) return false;

        _state = state with { ActiveMode = table, ActiveModeName = mode };
        return true;
    }

    /// <summary>Names of the configured binding modes.</summary>
    public IEnumerable<string> ModeNames => _state.Modes.Keys;

    /// <summary>
    /// Whether the current table claims this edge. The hook captures on key-down.
    /// </summary>
    /// <remarks>
    /// Key-down is claimed for every binding. Key-up for the same combination is
    /// swallowed too by the hook, because letting it through leaves applications
    /// believing a modifier is still held - and is claimed here, as an event worth
    /// delivering, only for a binding that runs on release.
    /// </remarks>
    public bool IsBound(ushort virtualKey, KeyModifiers modifiers, bool isKeyDown)
    {
        KeyboardBinding? binding = Capture(virtualKey, modifiers);
        return isKeyDown ? binding is not null : binding?.Release == true;
    }

    /// <summary>One snapshot lookup, performed only on a fresh press.</summary>
    public KeyboardBinding? Capture(ushort virtualKey, KeyModifiers modifiers)
    {
        // Read once. Re-reading would reintroduce exactly what the snapshot removes:
        // a reload landing between two reads and answering half of one question with
        // the old table and half with the new.
        Snapshot state = _state;
        ModeTable? mode = state.ActiveMode;
        KeyMap map = mode?.Keys ?? state.Default;
        int key = Pack((int)modifiers & map.ModifierMask, virtualKey);

        if (mode is not null)
        {
            if (map.Bindings.TryGetValue(key, out BoundShortcut? binding)) return binding;

            // A non-pass-through mode swallows everything. That is the entire point
            // of a `pause` mode: it exists to make the keyboard inert apart from the
            // binding that leaves it.
            //
            // Everything except the modifier keys themselves, which are never a
            // binding on their own and must not be claimed. Swallowing a modifier
            // stops it reaching the input state that ReadModifiers consults, so the
            // very next keystroke reports no modifiers held - and a mode whose only
            // way out is alt+shift+p can then never match it. The keyboard was inert
            // with no way back, which is the worst failure this program can have.
            return !mode.PassThrough && !IsModifierKey(virtualKey) ? KeyboardBinding.Suppress : null;
        }

        return map.Bindings.GetValueOrDefault(key);
    }

    /// <summary>Whether a key is a modifier, and so never a binding by itself.</summary>
    private static bool IsModifierKey(ushort virtualKey) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => true,   // shift, left shift, right shift
        0x11 or 0xA2 or 0xA3 => true,   // control, left control, right control
        0x12 or 0xA4 or 0xA5 => true,   // alt, left alt, right alt
        0x5B or 0x5C => true,           // left windows, right windows
        _ => false,
    };

    /// <summary>Inspects the current binding; dispatch uses the captured binding instead.</summary>
    public Keybinding? Resolve(ushort virtualKey, KeyModifiers modifiers)
    {
        return (Capture(virtualKey, modifiers) as BoundShortcut)?.Binding;
    }

    /// <summary>Packs modifiers and key into one integer, so lookup is one hash.</summary>
    private static int Pack(int modifiers, ushort virtualKey) => (modifiers << 16) | virtualKey;
}
