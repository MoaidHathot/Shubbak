using Ayn.Core;

namespace Ayn;

/// <summary>One parsed source text, reused when a daemon reload did not edit the file.</summary>
internal sealed class ConfigurationCache
{
    internal const int MaximumCachedCharacters = 256 * 1024;
    private string? _text;
    private AynConfigLoad? _load;

    internal AynConfigLoad Read(string path)
    {
        // Compare decoded chunks, not only timestamps: an explicit reload must see
        // same-length edits and replacements that preserve a file's mtime. StreamReader
        // also preserves ReadAllText's BOM/encoding behavior without allocating a new
        // large string for every unchanged file.
        if (_text is { } text && _load is { } known && Matches(path, text)) return known;
        return Parse(File.ReadAllText(path));
    }

    private static bool Matches(string path, string expected)
    {
        using var reader = new StreamReader(path);
        Span<char> buffer = stackalloc char[1024];
        int position = 0, count;
        while ((count = reader.Read(buffer)) != 0)
        {
            if (count > expected.Length - position || !buffer[..count].SequenceEqual(expected.AsSpan(position, count))) return false;
            position += count;
        }
        return position == expected.Length;
    }

    internal AynConfigLoad Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_load is { } known && string.Equals(_text, text, StringComparison.Ordinal)) return known;
        AynConfigLoad load = AynConfigLoader.Validate(text);
        // An exceptional huge file should not become permanent extra resident text.
        _text = text.Length <= MaximumCachedCharacters ? text : null;
        _load = _text is null ? null : load;
        return load;
    }
}
