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
    /// Bindings for the default mode, keyed by packed modifiers+key.
    /// </summary>
    private Dictionary<int, Keybinding> _default = [];

    private Dictionary<string, ModeTable> _modes =
        new(StringComparer.OrdinalIgnoreCase);

    private ModeTable? _activeMode;
    private string? _activeModeName;

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

        string? wasActive = _activeModeName;

        _default = defaults;
        _modes = modes;
        _activeMode = null;
        _activeModeName = null;

        if (wasActive is null) return null;

        // Re-selected against the new tables. SetMode reports whether the name still
        // means anything, which is the one thing the caller has to know: a mode that
        // has been deleted from the config leaves the keyboard on the defaults, and
        // everything that reports the mode has to be told so.
        return SetMode(wasActive) ? null : wasActive;
    }

    /// <summary>Switches binding mode; null returns to the default set.</summary>
    /// <returns>False when no such mode is declared, in which case nothing changed.</returns>
    public bool SetMode(string? mode)
    {
        if (mode is null)
        {
            _activeMode = null;
            _activeModeName = null;
            return true;
        }

        if (!_modes.TryGetValue(mode, out ModeTable? table)) return false;

        _activeMode = table;
        _activeModeName = mode;
        return true;
    }

    /// <summary>Names of the configured binding modes.</summary>
    public IEnumerable<string> ModeNames => _modes.Keys;

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

        ModeTable? mode = _activeMode;

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

        return _default.ContainsKey(key);
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

        ModeTable? mode = _activeMode;

        if (mode is not null)
            return mode.Bindings.GetValueOrDefault(key);

        return _default.GetValueOrDefault(key);
    }

    private static int Pack(KeyBinding binding) => Pack(binding.Modifiers, binding.VirtualKey);

    /// <summary>Packs modifiers and key into one integer, so lookup is one hash.</summary>
    private static int Pack(int modifiers, ushort virtualKey) => (modifiers << 16) | virtualKey;
}
