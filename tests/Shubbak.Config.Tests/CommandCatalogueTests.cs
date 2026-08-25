using Shubbak.Core.Commands;

namespace Shubbak.Config.Tests;

/// <summary>
/// Holds the command catalogue and the parser to each other.
/// </summary>
/// <remarks>
/// <para>
/// The command set used to be stated in three places that had to agree and had no way
/// of noticing when they did not: the parser's switch, a hand-written array of verbs
/// used only for typo suggestions, and the executor's switch. The array was the one
/// that rotted, because nothing fails when a suggestion list falls behind - the user
/// simply gets no hint for whichever command was added last, and nobody connects the
/// two.
/// </para>
/// <para>
/// The catalogue is now the single description. These tests are what make that true
/// rather than aspirational.
/// </para>
/// </remarks>
public sealed class CommandCatalogueTests
{
    [Fact]
    public void EveryCataloguedVerbParses()
    {
        List<string> unparseable = [];

        foreach (CommandSpec spec in CommandCatalogue.Commands)
        {
            foreach (string verb in new[] { spec.Verb }.Concat(spec.Aliases))
            {
                if (!Parses(verb)) unparseable.Add(verb);
            }
        }

        Assert.True(
            unparseable.Count == 0,
            $"the catalogue describes verbs the parser does not accept: {string.Join(", ", unparseable)}");
    }

    [Fact]
    public void EveryVerbTheParserAcceptsIsCatalogued()
    {
        // Read from the parser's own source rather than from a list written here,
        // which would be a fourth place to keep in step and would defeat the purpose.
        IReadOnlyList<string> accepted = VerbsInTheParserSwitch();

        Assert.NotEmpty(accepted);

        List<string> undescribed = [.. accepted.Where(v => CommandCatalogue.Find(v) is null)];

        Assert.True(
            undescribed.Count == 0,
            $"the parser accepts verbs the catalogue does not describe: {string.Join(", ", undescribed)}");
    }

    [Fact]
    public void EverySummaryIsUsableInAMenu()
    {
        foreach (CommandSpec spec in CommandCatalogue.Commands)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Summary), $"{spec.Verb} has no summary");

            // One line, sentence case, no trailing stop: these are list entries rather
            // than prose, and a client should not have to reformat them.
            Assert.DoesNotContain('\n', spec.Summary);
            Assert.DoesNotContain('\r', spec.Summary);
            Assert.False(spec.Summary.EndsWith('.'), $"{spec.Verb}'s summary ends with a full stop");
        }
    }

    [Fact]
    public void NoVerbIsDescribedTwice()
    {
        List<string> all = [.. CommandCatalogue.Verbs];
        List<string> duplicated = [.. all.GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];

        Assert.True(duplicated.Count == 0, $"duplicated: {string.Join(", ", duplicated)}");
    }

    [Fact]
    public void AMisspeltCommandIsCorrectedFromTheCatalogue()
    {
        var span = new TextSpan(new TextPosition(1, 1, 0), 10);

        Assert.False(CommandParser.TryParse(
            "focus-recent-windo", span, out _, out Diagnostic? error));

        // The verb was added at the same time as the catalogue replaced the array. On
        // the old arrangement it would have produced no suggestion at all, silently.
        Assert.NotNull(error);
        Assert.Contains("focus-recent-window", error.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsingAValidConfigDoesNotBuildTheCatalogue()
    {
        CommandCatalogue.ResetForTests();

        // A realistic config: every command form the parser knows, several times over.
        // If any of this consulted the catalogue, a table of forty records would be
        // built during every startup and every reload, to serve typo suggestions that
        // a correct config never asks for.
        string[] commands =
        [
            "focus --direction left", "focus --direction right", "focus --workspace 3",
            "focus --next", "focus-recent-window", "focus-window 0x1D0076",
            "move --direction up", "move --workspace 2", "move-workspace --direction left",
            "resize --width 10", "split vertical", "layout fibonacci", "layout --cycle",
            "toggle-floating", "toggle-fullscreen", "toggle-minimized", "close",
            "tag 4", "sticky", "scratchpad notes", "equalise",
            "signal palette", "wm-toggle-pause", "wm-reload-config", "wm-redraw",
        ];

        foreach (string text in commands)
        {
            var span = new TextSpan(new TextPosition(1, 1, 0), text.Length);

            Assert.True(
                CommandParser.TryParse(text, span, out _, out _),
                $"'{text}' should parse; the guard below is only meaningful if it does");
        }

        Assert.False(
            CommandCatalogue.IsBuilt,
            "parsing a valid config built the command catalogue. It is meant to be " +
            "reached only by typo suggestion, `query commands` and tests - see the " +
            "note on CommandCatalogue.");
    }

    [Fact]
    public void AFailedParseIsWhatBuildsIt()
    {
        CommandCatalogue.ResetForTests();

        var span = new TextSpan(new TextPosition(1, 1, 0), 8);
        CommandParser.TryParse("focuss", span, out _, out _);

        // The other half of the promise. Suggestion has to read the catalogue, and a
        // test that only asserted it stays unbuilt would also pass if nothing ever
        // used it at all.
        Assert.True(CommandCatalogue.IsBuilt);
    }

    private static bool Parses(string verb)
    {
        // Enough arguments to satisfy anything in the catalogue. Parsing is being
        // tested for verb recognition, not for argument validation, and a verb that
        // ignores the extras is fine.
        string text = verb switch
        {
            "focus" or "move" or "move-workspace" => $"{verb} --direction left",
            "resize" => "resize --width 10",
            "split" or "layout" => $"{verb} horizontal",
            "tag" => "tag 3",
            "scratchpad" => "scratchpad notes",
            "focus-window" => "focus-window 0x1234",
            "signal" => "signal palette",
            "shell-exec" => "shell-exec pwsh",
            "wm-enable-binding-mode" => "wm-enable-binding-mode resize",
            _ => verb,
        };

        var span = new TextSpan(new TextPosition(1, 1, 0), text.Length);
        return CommandParser.TryParse(text, span, out _, out _);
    }

    /// <summary>The case labels of the parser's verb switch.</summary>
    /// <remarks>
    /// Reading the source is crude, and it is still the right thing here: the
    /// alternative is a list maintained by hand, which is the exact failure this test
    /// exists to prevent. Direction shorthands are excluded - they are arguments the
    /// parser also accepts as whole commands, not verbs in their own right.
    /// </remarks>
    private static IReadOnlyList<string> VerbsInTheParserSwitch()
    {
        string source = File.ReadAllText(ParserSourcePath());
        List<string> verbs = [];

        foreach (System.Text.RegularExpressions.Match match in
            System.Text.RegularExpressions.Regex.Matches(source, @"case ""([a-z0-9-]+)""(?: or ""([a-z0-9-]+)"")?:"))
        {
            foreach (System.Text.RegularExpressions.Group group in match.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1))
                if (group.Success) verbs.Add(group.Value);
        }

        return [.. verbs.Where(v => v is not ("left" or "right" or "up" or "down"))];
    }

    private static string ParserSourcePath()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Shubbak.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "Shubbak.Config", "CommandParser.cs");
    }
}
