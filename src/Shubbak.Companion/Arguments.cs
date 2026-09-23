namespace Shubbak.Companion;

/// <summary>
/// The command line as the companions read it: <c>--flag value</c> pairs and bare
/// switches.
/// </summary>
/// <remarks>
/// One copy, where there were four - two of which disagreed. The bar's read a value
/// only when a following argument existed; the palette's and the watcher's took
/// <c>Array.IndexOf</c> and read past the end when the flag was last. Neither is
/// wrong for a flag that was given a value, and both are what a person typing
/// <c>--log-file</c> with nothing after it meets. This one never reads another flag
/// as a value: <c>taj --log-file --quiet</c> gives <c>--log-file</c> no value rather
/// than the value <c>--quiet</c>.
/// </remarks>
public static class Arguments
{
    /// <summary>The argument after <paramref name="flag"/>, or null when there is none or it is itself a flag.</summary>
    public static string? Value(string[] args, string flag)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], flag, StringComparison.Ordinal)) continue;

            string next = args[i + 1];
            return next.StartsWith("--", StringComparison.Ordinal) ? null : next;
        }

        return null;
    }

    /// <summary>Whether a switch is present anywhere on the line.</summary>
    public static bool Has(string[] args, string flag)
    {
        ArgumentNullException.ThrowIfNull(args);

        return Array.Exists(args, a => string.Equals(a, flag, StringComparison.Ordinal));
    }

    /// <summary>Whether the line asks for help: <c>--help</c>, <c>-h</c> or <c>help</c> first.</summary>
    public static bool AsksForHelp(string[] args) =>
        args is { Length: > 0 } && args[0] is "--help" or "-h" or "help";

    /// <summary>Whether the line asks for the version: <c>--version</c>, <c>-v</c> or <c>version</c> anywhere.</summary>
    public static bool AsksForVersion(string[] args) =>
        Array.Exists(args, a => a is "--version" or "-v" or "version");
}
