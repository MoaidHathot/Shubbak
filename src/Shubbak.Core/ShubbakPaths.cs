namespace Shubbak.Core;

/// <summary>
/// Where Shubbak keeps what it keeps between runs: the logs, the session, the saved
/// arrangements, a crash report, the palette's memory.
/// </summary>
/// <remarks>
/// <para>
/// One answer for six places that each asked <c>Environment.GetFolderPath</c> for
/// <c>LocalApplicationData</c> and appended <c>Shubbak</c>. Not because six was untidy,
/// but because none of the six could be pointed anywhere else - and a test that starts
/// the real window manager needs it pointed at a directory of the test's own, or the
/// daemon under test reads the user's session, adopts the user's windows, and saves
/// over the user's session on the way out. That happened once. Setting the
/// <c>LOCALAPPDATA</c> variable does not help: on Windows the runtime asks the shell
/// for the known folder, which reads the profile, not the environment.
/// </para>
/// <para>
/// So <c>SHUBBAK_STATE_DIR</c> is honoured, and nothing else is: an absolute path to
/// use in place of <c>%LOCALAPPDATA%\Shubbak</c>. Read once, at first use, so every
/// program agrees with itself for its whole life. Documented for tests and for the
/// person who wants their session on another drive; not something a config file can
/// set, since the config file's own location is decided before this is.
/// </para>
/// </remarks>
public static class ShubbakPaths
{
    /// <summary>The variable that moves everything: an absolute directory path.</summary>
    public const string StateDirectoryVariable = "SHUBBAK_STATE_DIR";

    /// <summary>The directory, without a trailing separator. Not created by asking.</summary>
    public static string StateDirectory { get; } = Resolve();

    private static string Resolve()
    {
        string? wanted = Environment.GetEnvironmentVariable(StateDirectoryVariable);

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(wanted));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A variable that does not name a path is ignored rather than fatal:
                // the programs must start, and the default is what they would have used.
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shubbak");
    }
}
