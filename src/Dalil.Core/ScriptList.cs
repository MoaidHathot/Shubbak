using System.Diagnostics;

namespace Dalil.Core;

/// <summary>
/// Runs the program behind a <see cref="MacroParamSource.Script"/> prompt and
/// collects what it prints.
/// </summary>
/// <remarks>
/// <para>
/// The palette's half of the bar's <c>kind="command"</c> source, with the shape
/// inverted: the bar keeps a program running and shows its last line; the palette
/// runs one when asked and shows every line. Same command line, same splitting of
/// program from arguments, same rule that the palette is what started it and so the
/// palette is what stops it.
/// </para>
/// <para>
/// Bounded three ways, because this runs on a keystroke. A program that has not
/// finished in <see cref="DefaultTimeout"/> is stopped, with everything it started,
/// and the frame says so rather than waiting; more than <see cref="MaxLines"/> lines
/// are not read, since nobody scrolls a thousand rows and a program that prints a
/// million must not hold the palette's memory; and standard error is read too, so a
/// script that failed says why in the frame rather than in a console nobody saw.
/// </para>
/// </remarks>
public static class ScriptList
{
    /// <summary>How long a program may take before it is stopped and the frame says so.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The most lines read from a program. Anything after is left unread.</summary>
    public const int MaxLines = 1000;

    /// <summary>What a run produced: the lines, or why there are none.</summary>
    /// <param name="Lines">Every line printed to standard output, unread past <see cref="MaxLines"/>.</param>
    /// <param name="Failure">Why the program could not be run or did not finish, or null when it did.</param>
    public sealed record Result(IReadOnlyList<string> Lines, string? Failure)
    {
        /// <summary>Whether the program ran to completion in time.</summary>
        public bool Succeeded => Failure is null;
    }

    /// <summary>Runs the program and gathers its output.</summary>
    /// <param name="commandLine">The program and its arguments, the program quoted if its path has a space.</param>
    /// <param name="timeout">How long to wait, or null for <see cref="DefaultTimeout"/>.</param>
    /// <param name="token">Cancels the wait; the program is stopped.</param>
    public static async Task<Result> RunAsync(string commandLine, TimeSpan? timeout = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        (string file, string arguments) = Split(commandLine);

        using var process = new Process();

        process.StartInfo = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new Result([], $"could not start '{file}': {ex.Message}");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? DefaultTimeout);

        List<string> lines = [];
        Task<string> errors = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            while (lines.Count < MaxLines)
            {
                string? line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                if (line is null) break;

                lines.Add(line);
            }

            // Past the cap the rest is discarded rather than read, so the wait below
            // is for the program to finish and not for its output to be drained.
            if (lines.Count >= MaxLines) process.StandardOutput.Close();

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop(process);

            return new Result(
                lines,
                token.IsCancellationRequested
                    ? "the palette moved on before the program finished"
                    : $"the program did not finish within {(timeout ?? DefaultTimeout).TotalSeconds:F0} seconds and was stopped");
        }

        string stderr;

        try
        {
            stderr = await errors.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            stderr = string.Empty;
        }

        // A program that exited badly and printed nothing usable is a failure worth
        // the words it wrote to standard error. One that exited badly but printed
        // choices is believed: a warning on stderr is not a reason to hide the list.
        if (process.ExitCode != 0 && lines.All(string.IsNullOrWhiteSpace))
        {
            string said = stderr.Trim();

            return new Result(
                lines,
                said.Length > 0
                    ? $"exit code {process.ExitCode}: {FirstLines(said)}"
                    : $"the program exited with code {process.ExitCode} and printed nothing");
        }

        return new Result(lines, null);
    }

    /// <summary>The first few lines of an error, so a stack trace does not become a row.</summary>
    private static string FirstLines(string text)
    {
        string[] parts = text.Split('\n', 4, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 3 ? string.Join(" / ", parts[..3]) + " \u2026" : string.Join(" / ", parts);
    }

    /// <summary>Program and arguments, the program quoted if its path has a space; the bar's rule.</summary>
    public static (string File, string Arguments) Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        commandLine = commandLine.Trim();

        if (commandLine.StartsWith('"'))
        {
            int close = commandLine.IndexOf('"', 1);
            if (close > 0) return (commandLine[1..close], commandLine[(close + 1)..].Trim());
        }

        int space = commandLine.IndexOf(' ', StringComparison.Ordinal);

        return space < 0
            ? (commandLine, string.Empty)
            : (commandLine[..space], commandLine[(space + 1)..]);
    }

    /// <summary>
    /// Stops a program that outstayed its welcome, and everything it started: a
    /// script is nearly always <c>pwsh -File</c> something, and ending the shell alone
    /// leaves its children running.
    /// </summary>
    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            // Already gone, or not ours to end; either way there is nothing more to do.
        }
    }
}
