using System.Globalization;

namespace Shubbak.Config;

/// <summary>
/// Parses key binding strings such as <c>alt+shift+3</c>.
/// </summary>
/// <remarks>
/// Accepts the spellings GlazeWM users already have in their configs, including
/// <c>oem_quotes</c> and bare punctuation like <c>alt+-</c>, so migrating a config
/// does not mean relearning key names.
/// </remarks>
public static class KeyParser
{
    /// <summary>Modifier bit flags, matching <c>Shubbak.Native.KeyModifiers</c>.</summary>
    public const int ModAlt = 1 << 0;
    public const int ModControl = 1 << 1;
    public const int ModShift = 1 << 2;
    public const int ModWindows = 1 << 3;

    private static readonly Dictionary<string, ushort> s_namedKeys =
        new(StringComparer.OrdinalIgnoreCase)
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
        };

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
                    "Use a letter, digit, F-key, a name such as 'enter' or 'left', or punctuation such as '-'.");
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
