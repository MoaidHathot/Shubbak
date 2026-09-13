using System.Reflection;

namespace Shubbak.Config;

/// <summary>
/// The starter configuration: what a new install runs on until its owner edits it.
/// </summary>
/// <remarks>
/// <para>
/// Kept as <c>StarterConfig.kdl</c> beside this file and embedded, rather than as a
/// string constant in the CLI where it began. A config of a few hundred lines inside a
/// C# literal cannot be syntax-highlighted, diffed as KDL, or run through
/// <c>shubbak check-config</c> by the same script that checks every snippet in the
/// docs; as a file it can be all three. Living in this project rather than the CLI's
/// is what lets the window manager write it too - see <see cref="WriteIfMissing"/>.
/// </para>
/// <para>
/// It is deliberately not <c>docs/shubbak.example.kdl</c>. That one is a thousand
/// lines and exists to document every setting with the reasoning behind it, which is
/// the right thing to read and the wrong thing to inherit. This one is meant to be a
/// desktop somebody could keep: the keys, borders, animation, ten workspaces, a bar
/// with every indicator that matters, a palette with a dozen actions, and the watcher
/// - functional rather than minimal, but short enough to read in one sitting and
/// delete from.
/// </para>
/// </remarks>
public static class StarterConfig
{
    private const string ResourceName = "Shubbak.Config.StarterConfig.kdl";

    private static readonly Lazy<string> s_text = new(Read);

    /// <summary>The starter, exactly as <c>shubbak config init</c> writes it.</summary>
    public static string Text => s_text.Value;

    /// <summary>
    /// Writes the starter to <paramref name="path"/> unless a file is already there.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the file was written; <see langword="false"/> if one
    /// already existed and was left alone.
    /// </returns>
    /// <exception cref="IOException">The directory or file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The location is not writable.</exception>
    public static bool WriteIfMissing(string path)
    {
        if (File.Exists(path)) return false;

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        // Written beside the target and moved into place, so a crash halfway through
        // cannot leave a half-file that the resolver then finds and refuses to replace.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, Text);
        File.Move(temporary, path, overwrite: false);

        return true;
    }

    private static string Read()
    {
        using Stream stream = typeof(StarterConfig).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"embedded resource {ResourceName} is missing");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
