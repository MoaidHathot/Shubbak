using Shubbak.Core.Diagnostics;

namespace Shubbak.Config;

/// <summary>How many of each a load produced.</summary>
/// <param name="Errors">Problems that make the file untrustworthy.</param>
/// <param name="Warnings">Problems worth saying that do not stop anything.</param>
public readonly record struct DiagnosticCounts(int Errors, int Warnings)
{
    /// <summary>Whether there is anything at all to say.</summary>
    public bool Any => Errors > 0 || Warnings > 0;

    /// <summary>"2 errors, 5 warnings", pluralised, or an empty string when clean.</summary>
    public string Describe()
    {
        if (!Any) return string.Empty;

        if (Errors == 0) return Warnings == 1 ? "1 warning" : $"{Warnings} warnings";
        if (Warnings == 0) return Errors == 1 ? "1 error" : $"{Errors} errors";

        string errors = Errors == 1 ? "1 error" : $"{Errors} errors";
        string warnings = Warnings == 1 ? "1 warning" : $"{Warnings} warnings";

        return $"{errors}, {warnings}";
    }
}

/// <summary>
/// Puts config diagnostics somewhere a detached process can be read from.
/// </summary>
/// <remarks>
/// <para>
/// Every process that reads the configuration wrote its diagnostics to
/// <c>Console.Error</c> and nowhere else, and none of the three has a console on the
/// path that matters. <c>shubbak-wm</c> only asks for one with <c>--foreground</c>;
/// Taj and Dalil only for <c>--help</c> and <c>--version</c>. Started at logon, or from
/// <c>startup-command</c>, all three formatted the line, the column, the caret and the
/// hint, and dropped the lot on the floor.
/// </para>
/// <para>
/// Which made the headline promise - "instead of failing silently: you press the key,
/// nothing happens, and you go hunting" - true only of <c>shubbak check-config</c>, a
/// thing you have to decide to run. Every process already opens a log file precisely
/// because standard error goes nowhere; this is the two being introduced.
/// </para>
/// <para>
/// One place rather than three, because three is how this happened: the bar's
/// reporting was written, the daemon's was written, the palette's never was, and
/// nobody noticed that neither of the first two reached a log.
/// </para>
/// </remarks>
public static class ConfigDiagnostics
{
    /// <summary>
    /// Writes diagnostics to the log, one line each, and says what was found.
    /// </summary>
    /// <remarks>
    /// The one-line form rather than the rendered one. A caret drawn under a source
    /// line needs the column to survive, and a log file has a timestamp and a category
    /// in front of every line - so the caret would point several characters to the left
    /// of the thing it means. The position is in the text instead, which is what an
    /// editor can be told to jump to.
    /// </remarks>
    /// <param name="diagnostics">What the loader produced.</param>
    /// <param name="path">The file they came from.</param>
    /// <param name="what">
    /// Which part of the file this process reads, so three logs reporting on one file
    /// do not read as three copies of the same problem.
    /// </param>
    public static DiagnosticCounts Report(
        IEnumerable<Diagnostic> diagnostics, string? path, string what)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        int errors = 0;
        int warnings = 0;

        foreach (Diagnostic diagnostic in diagnostics)
        {
            string line = path is { Length: > 0 }
                ? $"{path}:{diagnostic}"
                : diagnostic.ToString();

            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                errors++;
                Log.Error(LogCategory.Config, line);
            }
            else
            {
                warnings++;
                Log.Warn(LogCategory.Config, line);
            }
        }

        var counts = new DiagnosticCounts(errors, warnings);

        // A summary as well as the lines. Somebody scanning a log for why the bar looks
        // wrong will find one sentence faster than they will count twelve.
        if (counts.Any)
            Log.Warn(LogCategory.Config, $"{what}: {counts.Describe()}");

        return counts;
    }
}
