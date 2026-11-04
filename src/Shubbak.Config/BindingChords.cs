using System.Numerics;

namespace Shubbak.Config;

/// <summary>
/// Compiles sided modifiers outside the hook. Generic chords accept either side;
/// specific chords win, then file order. Context overlays retain last-wins order.
/// Families without sided bindings need no expansion or extra lookup at runtime.
/// Shared by the runtime and mode-exit validation so their winners cannot diverge.
/// </summary>
public static class BindingChords
{
    private static readonly (int Basic, int Left, int Right)[] Families =
    [
        (KeyParser.ModAlt, KeyParser.LeftAlt, KeyParser.RightAlt),
        (KeyParser.ModControl, KeyParser.LeftControl, KeyParser.RightControl),
        (KeyParser.ModShift, KeyParser.LeftShift, KeyParser.RightShift),
        (KeyParser.ModWindows, KeyParser.LeftWindows, KeyParser.RightWindows),
    ];

    public static (int ModifierMask, Dictionary<int, T> Bindings) Compile<T>(
        IReadOnlyList<Keybinding> defaults, IReadOnlyList<Keybinding> overlay, Func<Keybinding, T> create)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(create);
        int mask = KeyParser.BasicMask;
        foreach (Keybinding binding in defaults.Concat(overlay)) mask |= binding.Key.Modifiers & KeyParser.SideMask;
        foreach ((_, int left, int right) in Families)
            if ((mask & (left | right)) != 0) mask |= left | right;

        Dictionary<int, T> result = [];
        Span<int> expanded = stackalloc int[81]; // At most three choices per family.
        IEnumerable<Keybinding> ordered = mask == KeyParser.BasicMask ? defaults
            : defaults.OrderByDescending(b => BitOperations.PopCount((uint)(b.Key.Modifiers & KeyParser.SideMask)));
        foreach (Keybinding binding in ordered)
        {
            T value = create(binding);
            int count = Expand(binding.Key, mask, expanded);
            for (int i = 0; i < count; i++) result.TryAdd(expanded[i], value);
        }
        foreach (Keybinding binding in overlay)
        {
            T value = create(binding);
            int count = Expand(binding.Key, mask, expanded);
            for (int i = 0; i < count; i++) result[expanded[i]] = value;
        }
        return (mask, result);
    }

    private static int Expand(KeyBinding binding, int mask, Span<int> keys)
    {
        keys[0] = ((binding.Modifiers & KeyParser.BasicMask) << 16) | binding.VirtualKey;
        int count = 1;
        Span<int> choices = stackalloc int[3];
        foreach ((int basic, int left, int right) in Families)
        {
            if ((binding.Modifiers & basic) == 0 || (mask & (left | right)) == 0) continue;
            int required = binding.Modifiers & (left | right);
            int options = 0;
            if ((left & required) == required) choices[options++] = left;
            if ((right & required) == required) choices[options++] = right;
            choices[options++] = left | right;
            // Work backwards: expansion must not overwrite unread prefixes.
            for (int i = count - 1; i >= 0; i--)
            {
                int prefix = keys[i];
                for (int j = 0; j < options; j++) keys[i * options + j] = prefix | (choices[j] << 16);
            }
            count *= options;
        }
        return count;
    }
}
