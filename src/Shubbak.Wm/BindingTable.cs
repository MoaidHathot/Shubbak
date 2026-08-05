using Shubbak.Config;
using Shubbak.Native;

namespace Shubbak.Wm;

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
        Dictionary<int, Keybinding> Default,
        Dictionary<string, ModeTable> Modes,
        ModeTable? ActiveMode,
        string? ActiveModeName);

    private volatile Snapshot _state = new(
        [],
        new Dictionary<string, ModeTable>(StringComparer.OrdinalIgnoreCase),
        null,
        null);

    private sealed record ModeTable(Dictionary<int, Keybinding> Bindings, bool PassThrough);

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

        Dictionary<int, Keybinding> defaults = [];
        foreach (Keybinding binding in config.Keybindings)
            defaults.TryAdd(Pack(binding.Key), binding);

        Dictionary<string, ModeTable> modes = new(StringComparer.OrdinalIgnoreCase);
        foreach (BindingMode mode in config.BindingModes)
        {
            Dictionary<int, Keybinding> bindings = [];
            foreach (Keybinding binding in mode.Keybindings)
                bindings.TryAdd(Pack(binding.Key), binding);

            modes[mode.Name] = new ModeTable(bindings, mode.PassThrough);
        }

        string? wasActive = _state.ActiveModeName;

        // Re-selected against the new tables before anything is published, so the hook
        // never observes the gap between losing the old mode and regaining the new one.
        ModeTable? active = wasActive is not null && modes.TryGetValue(wasActive, out ModeTable? kept)
            ? kept
            : null;

        _state = new Snapshot(defaults, modes, active, active is null ? null : wasActive);

        // A mode that has been deleted from the config leaves the keyboard on the
        // defaults, and everything that reports the mode has to be told so.
        return wasActive is not null && active is null ? wasActive : null;
    }

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
    /// Whether a keystroke is bound. Called from the hook callback.
    /// </summary>
    /// <remarks>
    /// Only key-down is claimed. Key-up for the same combination is swallowed too
    /// by the hook, because letting it through leaves applications believing a
    /// modifier is still held.
    /// </remarks>
    public bool IsBound(ushort virtualKey, KeyModifiers modifiers, bool isKeyDown)
    {
        if (!isKeyDown) return false;

        int key = Pack((int)modifiers, virtualKey);

        // Read once. Re-reading would reintroduce exactly what the snapshot removes:
        // a reload landing between two reads and answering half of one question with
        // the old table and half with the new.
        Snapshot state = _state;
        ModeTable? mode = state.ActiveMode;

        if (mode is not null)
        {
            if (mode.Bindings.ContainsKey(key)) return true;

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
            return !mode.PassThrough && !IsModifierKey(virtualKey);
        }

        return state.Default.ContainsKey(key);
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

    /// <summary>Resolves a keystroke to its binding, on the worker thread.</summary>
    public Keybinding? Resolve(ushort virtualKey, KeyModifiers modifiers)
    {
        int key = Pack((int)modifiers, virtualKey);

        // Read once. Re-reading would reintroduce exactly what the snapshot removes:
        // a reload landing between two reads and answering half of one question with
        // the old table and half with the new.
        Snapshot state = _state;
        ModeTable? mode = state.ActiveMode;

        if (mode is not null)
            return mode.Bindings.GetValueOrDefault(key);

        return state.Default.GetValueOrDefault(key);
    }

    private static int Pack(KeyBinding binding) => Pack(binding.Modifiers, binding.VirtualKey);

    /// <summary>Packs modifiers and key into one integer, so lookup is one hash.</summary>
    private static int Pack(int modifiers, ushort virtualKey) => (modifiers << 16) | virtualKey;
}
