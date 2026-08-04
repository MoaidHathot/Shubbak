using System.Globalization;

namespace Shubbak.Config;

/// <summary>
/// Parses key binding strings such as <c>alt+shift+3</c>.
/// </summary>
/// <remarks>
/// <para>
/// Accepts the spellings GlazeWM users already have in their configs, including
/// <c>oem_quotes</c> and bare punctuation like <c>alt+-</c>, so migrating a config
/// does not mean relearning key names.
/// </para>
/// <para>
/// Two caveats that belong to Windows rather than to this parser, and that no
/// diagnostic here can detect:
/// </para>
/// <para>
/// A numpad key only reports <c>VK_NUMPAD0</c>-<c>VK_NUMPAD9</c> while Num Lock is
/// on. With it off the same physical keys report the navigation codes - home, end,
/// the arrows - so <c>alt+numpad1</c> will not fire, and <c>alt+end</c> will.
/// </para>
/// <para>
/// Letters and punctuation resolve to virtual-key codes, which are positional. On
/// AZERTY <c>alt+a</c> binds the key where Q sits on QWERTY, and <c>;</c> is a
/// different physical key on most non-US layouts. Whether that is right depends on
/// whether the user means the key labelled A or the key where A is on QWERTY, and
/// there is currently no way to say which.
/// </para>
/// </remarks>
public static class KeyParser
{
    /// <summary>Modifier bit flags, matching <c>Shubbak.Native.KeyModifiers</c>.</summary>
    public const int ModAlt = 1 << 0;
    public const int ModControl = 1 << 1;
    public const int ModShift = 1 << 2;
    public const int ModWindows = 1 << 3;

    private static readonly Dictionary<string, ushort> s_namedKeys = BuildNamedKeys();

    private static Dictionary<string, ushort> BuildNamedKeys()
    {
        var keys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["backspace"] = 0x08,
            ["tab"] = 0x09,
            ["enter"] = 0x0D,
            ["return"] = 0x0D,
            ["escape"] = 0x1B,
            ["esc"] = 0x1B,
            ["space"] = 0x20,
            ["pageup"] = 0x21,
            ["pagedown"] = 0x22,
            ["end"] = 0x23,
            ["home"] = 0x24,
            ["left"] = 0x25,
            ["up"] = 0x26,
            ["right"] = 0x27,
            ["down"] = 0x28,
            ["insert"] = 0x2D,
            ["delete"] = 0x2E,

            // Locks and system keys.
            ["pause"] = 0x13,
            ["capslock"] = 0x14,
            ["printscreen"] = 0x2C,
            ["prtsc"] = 0x2C,
            ["apps"] = 0x5D,
            ["menu"] = 0x5D,
            ["numlock"] = 0x90,
            ["scrolllock"] = 0x91,

            // Numpad operators. The digits are added below.
            ["numpad_multiply"] = 0x6A,
            ["numpad_add"] = 0x6B,
            ["numpad_separator"] = 0x6C,
            ["numpad_subtract"] = 0x6D,
            ["numpad_decimal"] = 0x6E,
            ["numpad_divide"] = 0x6F,

            // Browser keys, as found on most keyboards with a media row.
            ["browser_back"] = 0xA6,
            ["browser_forward"] = 0xA7,
            ["browser_refresh"] = 0xA8,
            ["browser_stop"] = 0xA9,
            ["browser_search"] = 0xAA,
            ["browser_favorites"] = 0xAB,
            ["browser_home"] = 0xAC,

            // Media keys.
            ["volume_mute"] = 0xAD,
            ["volume_down"] = 0xAE,
            ["volume_up"] = 0xAF,
            ["media_next"] = 0xB0,
            ["media_prev"] = 0xB1,
            ["media_stop"] = 0xB2,
            ["media_play_pause"] = 0xB3,

            // OEM keys. GlazeWM's own names are accepted alongside the punctuation
            // they represent, because existing configs use them.
            ["oem_1"] = 0xBA,        // ;:
            ["semicolon"] = 0xBA,
            ["oem_plus"] = 0xBB,     // =+
            ["oem_comma"] = 0xBC,
            ["comma"] = 0xBC,
            ["oem_minus"] = 0xBD,
            ["oem_period"] = 0xBE,
            ["period"] = 0xBE,
            ["oem_2"] = 0xBF,        // /?
            ["oem_3"] = 0xC0,        // `~
            ["oem_4"] = 0xDB,        // [{
            ["oem_5"] = 0xDC,        // \|
            ["oem_6"] = 0xDD,        // ]}
            ["oem_7"] = 0xDE,        // '"
            ["oem_quotes"] = 0xDE,

            // The key beside left shift on every non-US physical keyboard, and the
            // one beside it on some. Absent before, so an ISO keyboard had two keys
            // that simply could not be named.
            ["oem_8"] = 0xDF,
            ["oem_102"] = 0xE2,
            ["backslash_iso"] = 0xE2,
        };

        // Three spellings each, because all three get guessed and none is obviously
        // the right one to have picked.
        for (ushort digit = 0; digit <= 9; digit++)
        {
            ushort code = (ushort)(0x60 + digit);

            keys[$"numpad{digit}"] = code;
            keys[$"kp{digit}"] = code;
            keys[$"num{digit}"] = code;
        }

        return keys;
    }

    /// <summary>Single punctuation characters mapped to their virtual-key codes.</summary>
    private static readonly Dictionary<char, ushort> s_punctuation = new()
    {
        [';'] = 0xBA,
        ['='] = 0xBB,
        [','] = 0xBC,
        ['-'] = 0xBD,
        ['.'] = 0xBE,
        ['/'] = 0xBF,
        ['`'] = 0xC0,
        ['['] = 0xDB,
        ['\\'] = 0xDC,
        [']'] = 0xDD,
        ['\''] = 0xDE,
    };

    /// <summary>
    /// Parses a binding such as <c>alt+shift+h</c>.
    /// </summary>
    /// <param name="text">The binding as written.</param>
    /// <param name="span">Where it came from, for diagnostics.</param>
    /// <param name="binding">The parsed binding.</param>
    /// <param name="diagnostic">Why parsing failed.</param>
    public static bool TryParse(
        string text, TextSpan span, out KeyBinding binding, out Diagnostic? diagnostic)
    {
        binding = default;
        diagnostic = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            diagnostic = Diagnostic.Error("SHB0201", "Empty key binding.", span);
            return false;
        }

        int modifiers = 0;
        ushort virtualKey = 0;
        bool haveKey = false;

        foreach (string part in SplitParts(text))
        {
            switch (part.ToLowerInvariant())
            {
                case "alt": modifiers |= ModAlt; continue;
                case "ctrl" or "control": modifiers |= ModControl; continue;
                case "shift": modifiers |= ModShift; continue;
                case "win" or "super" or "meta" or "cmd": modifiers |= ModWindows; continue;
            }

            if (haveKey)
            {
                diagnostic = Diagnostic.Error(
                    "SHB0202",
                    $"Binding '{text}' names more than one non-modifier key ('{part}' follows another).",
                    span,
                    "A binding must have exactly one main key, e.g. alt+shift+h.");
                return false;
            }

            if (!TryResolveKey(part, out virtualKey))
            {
                diagnostic = Diagnostic.Error(
                    "SHB0203",
                    $"Unknown key '{part}' in binding '{text}'.",
                    span,
                    "Use a letter, digit, F-key, punctuation such as '-', or a name: " +
                    "enter, escape, space, tab, backspace, delete, insert, home, end, " +
                    "pageup, pagedown, the arrows, numpad0-9 (also kp0-9 or num0-9), " +
                    "numpad_add and friends, volume_up, media_play_pause, browser_back, " +
                    "printscreen, capslock, numlock, scrolllock, pause, apps, or oem_102.");
                return false;
            }

            haveKey = true;
        }

        if (!haveKey)
        {
            diagnostic = Diagnostic.Error(
                "SHB0204",
                $"Binding '{text}' has modifiers but no key.",
                span,
                "Add the key the modifiers apply to, e.g. alt+shift+h.");
            return false;
        }

        binding = new KeyBinding(modifiers, virtualKey, text);
        return true;
    }

    /// <summary>
    /// Splits on '+' while keeping a literal '+' key working.
    /// </summary>
    /// <remarks>
    /// A naive split breaks <c>alt++</c>, and more importantly breaks
    /// <c>alt+-</c> which appears in the author's config. A trailing separator is
    /// therefore treated as the key itself.
    /// </remarks>
    private static List<string> SplitParts(string text)
    {
        List<string> parts = [];
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '+') continue;

            // A '+' immediately after a separator is the key '+', not a separator.
            if (i == start)
            {
                parts.Add("+");
                start = i + 1;
                continue;
            }

            parts.Add(text[start..i]);
            start = i + 1;
        }

        if (start < text.Length) parts.Add(text[start..]);

        return parts;
    }

    private static bool TryResolveKey(string token, out ushort virtualKey)
    {
        virtualKey = 0;

        if (token.Length == 0) return false;

        if (s_namedKeys.TryGetValue(token, out virtualKey)) return true;

        // Function keys.
        if ((token[0] is 'f' or 'F') && token.Length is 2 or 3 &&
            int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int fn) &&
            fn is >= 1 and <= 24)
        {
            virtualKey = (ushort)(0x70 + fn - 1);
            return true;
        }

        if (token.Length == 1)
        {
            char c = token[0];

            if (c is >= 'a' and <= 'z') { virtualKey = (ushort)char.ToUpperInvariant(c); return true; }
            if (c is >= 'A' and <= 'Z') { virtualKey = c; return true; }
            if (c is >= '0' and <= '9') { virtualKey = c; return true; }
            if (c == '+') { virtualKey = 0xBB; return true; }

            if (s_punctuation.TryGetValue(c, out virtualKey)) return true;
        }

        return false;
    }
}
