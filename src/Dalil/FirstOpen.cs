using Shubbak.Core.Diagnostics;

namespace Dalil;

/// <summary>
/// Remembers whether the palette has ever been opened on this machine.
/// </summary>
/// <remarks>
/// <para>
/// One empty file in the state directory, beside the logs and the session. Its
/// existence is the whole record; nothing is ever read from it. A registry value
/// would do the same job and be harder to find and to delete, and "delete this file
/// to see the welcome again" is an instruction anyone can follow.
/// </para>
/// <para>
/// Claiming is a single create-new, so two palettes racing - which the single
/// instance lock already prevents - would still agree on which was first. Failure to
/// write counts as "not first": a state directory that cannot be written to has
/// bigger problems than a welcome screen, and showing the keys every time until it
/// is fixed would be the wrong way to report them.
/// </para>
/// </remarks>
internal static class FirstOpen
{
    private static readonly string s_marker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Shubbak",
        "dalil.opened");

    /// <summary>
    /// Whether this is the first open ever, recording that it has now happened.
    /// </summary>
    public static bool Claim()
    {
        try
        {
            if (File.Exists(s_marker)) return false;

            if (Path.GetDirectoryName(s_marker) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            using FileStream _ = new(s_marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // CreateNew lost a race, or the directory is not ours to write. Either
            // way, not first.
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Debug(LogCategory.Wm, $"could not record the first open: {ex.Message}");
            return false;
        }
    }
}
