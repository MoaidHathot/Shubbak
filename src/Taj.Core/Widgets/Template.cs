using System.Text;

namespace Taj.Core.Widgets;

/// <summary>
/// Substitutes <c>{{ source }}</c> placeholders in a widget's template.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately tiny. A bar template turns a handful of values into a short string;
/// a full expression language would be a large amount of code, a large amount of
/// documentation, and a new way for a config to fail at runtime.
/// </para>
/// <para>
/// Supported: <c>{{ name }}</c>, and <c>{{ name | filter }}</c> with a small set of
/// filters. Filters chain left to right.
/// </para>
/// </remarks>
public static class Template
{
    /// <summary>Renders a template against the given values.</summary>
    /// <param name="template">Text containing <c>{{ ... }}</c> placeholders.</param>
    /// <param name="values">Source name to current value.</param>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        if (!template.Contains("{{", StringComparison.Ordinal)) return template;

        var output = new StringBuilder(template.Length);
        int index = 0;

        while (index < template.Length)
        {
            int open = template.IndexOf("{{", index, StringComparison.Ordinal);

            if (open < 0)
            {
                output.Append(template, index, template.Length - index);
                break;
            }

            output.Append(template, index, open - index);

            int close = template.IndexOf("}}", open, StringComparison.Ordinal);

            if (close < 0)
            {
                // Unterminated: emit it literally rather than swallowing the rest of
                // the template, so the mistake is visible on the bar.
                output.Append(template, open, template.Length - open);
                break;
            }

            output.Append(Evaluate(template[(open + 2)..close], values));
            index = close + 2;
        }

        return output.ToString();
    }

    /// <summary>The source names a template refers to.</summary>
    /// <remarks>
    /// Used to subscribe a widget to exactly the sources it needs, so it re-renders
    /// only when one of them changes.
    /// </remarks>
    public static IReadOnlyList<string> Dependencies(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        List<string> names = [];
        int index = 0;

        while (true)
        {
            int open = template.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0) break;

            int close = template.IndexOf("}}", open, StringComparison.Ordinal);
            if (close < 0) break;

            string expression = template[(open + 2)..close];
            int pipe = expression.IndexOf('|', StringComparison.Ordinal);
            string name = (pipe < 0 ? expression : expression[..pipe]).Trim();

            if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);

            index = close + 2;
        }

        return names;
    }

    private static string Evaluate(string expression, IReadOnlyDictionary<string, string?> values)
    {
        string[] parts = expression.Split('|', StringSplitOptions.TrimEntries);

        string name = parts[0];
        string result = values.TryGetValue(name, out string? value) ? value ?? string.Empty : string.Empty;

        for (int i = 1; i < parts.Length; i++) result = ApplyFilter(result, parts[i]);

        return result;
    }

    private static string ApplyFilter(string input, string filter)
    {
        // Filters take at most one argument, separated by a colon.
        int colon = filter.IndexOf(':', StringComparison.Ordinal);
        string name = (colon < 0 ? filter : filter[..colon]).Trim();
        string argument = colon < 0 ? string.Empty : filter[(colon + 1)..].Trim();

        switch (name.ToLowerInvariant())
        {
            case "truncate":
            {
                // The filter that earns its place: window titles are unbounded and
                // would otherwise push everything else off the bar.
                if (!int.TryParse(argument, out int max) || max <= 0) return input;
                return input.Length <= max ? input : string.Concat(input.AsSpan(0, max - 1), "\u2026");
            }

            case "upper": return input.ToUpperInvariant();
            case "lower": return input.ToLowerInvariant();
            case "trim": return input.Trim();

            case "default":
                return input.Length == 0 ? argument : input;

            case "pad":
            {
                if (!int.TryParse(argument, out int width)) return input;
                return width < 0 ? input.PadLeft(-width) : input.PadRight(width);
            }

            case "replace":
            {
                // replace:from,to
                int comma = argument.IndexOf(',', StringComparison.Ordinal);
                if (comma < 0) return input;

                return input.Replace(
                    argument[..comma], argument[(comma + 1)..], StringComparison.Ordinal);
            }

            case "icon":
                return LayoutIcon(input);

            default:
                // An unknown filter passes the value through unchanged rather than
                // blanking the widget, so a typo degrades gracefully.
                return input;
        }
    }

    /// <summary>
    /// Replaces a layout name with a glyph that shows its shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The word "splith" says nothing at a glance, and at bar size it is a smear.
    /// A glyph that looks like the arrangement it names is read without being read.
    /// </para>
    /// <para>
    /// Box-drawing and block characters rather than an icon font, so it needs nothing
    /// installed. An unrecognised layout keeps its name, which is what a custom layout
    /// should do.
    /// </para>
    /// <para>
    /// Every glyph here is one that Segoe UI Variable Text actually has. The geometric
    /// shapes that read most obviously - the half-filled squares at U+25E7 and U+25E8,
    /// and U+229E for a grid - are in neither Segoe UI Variable nor Segoe UI, so six of
    /// the eleven layouts drew a borrowed glyph from a substitute font at a width the
    /// layout had not reserved, and came out clipped. The quadrant and box-drawing
    /// characters below say the same thing and are present.
    /// </para>
    /// </remarks>
    private static string LayoutIcon(string layout) => layout switch
    {
        "splith" => "\u2502\u2502",              // ││  side by side
        "splitv" => "\u2261",                    // ≡   stacked
        "fibonacci" => "\u2524",                 // ┤   one large pane, the rest divided
        "fibonacci-v" => "\u252C",               // ┬
        "fibonacci-mirrored" => "\u251C",        // ├
        "master-left" => "\u258C",               // ▌   master fills the left half
        "master-right" => "\u2590",              // ▐
        "master-top" => "\u2580",                // ▀
        "master-bottom" => "\u2584",             // ▄
        "grid" => "\u253C",                      // ┼   divided both ways
        "monocle" => "\u25A0",                   // ■
        _ => layout,
    };
}
