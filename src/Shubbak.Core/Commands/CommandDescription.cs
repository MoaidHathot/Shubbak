namespace Shubbak.Core.Commands;

/// <summary>
/// Naming commands in log lines.
/// </summary>
/// <remarks>
/// A binding runs a list, and the log has to say which list without printing the
/// arguments - those can contain a whole shell command line, which is neither
/// readable at the end of a log line nor the thing being diagnosed. The names alone
/// answer "did the right binding fire?", which is the question being asked.
/// </remarks>
public static class CommandDescription
{
    /// <summary>The command names, separated by semicolons.</summary>
    public static string Describe(this IReadOnlyList<WmCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        return commands.Count switch
        {
            // The overwhelmingly common case, and worth not allocating a joiner for:
            // this runs on the tick path whenever debug logging is on.
            0 => string.Empty,
            1 => commands[0].Name,
            _ => string.Join("; ", commands.Select(command => command.Name)),
        };
    }
}
