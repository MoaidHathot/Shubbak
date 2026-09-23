namespace Shubbak.Config.Tests;

/// <summary>
/// The ways a rule can say how a pattern should be compared.
/// </summary>
/// <remarks>
/// Both spellings are documented - a symbolic one borrowed from CSS attribute
/// selectors, and a named one - and only the named one is reachable without care.
/// KDL excludes <c>=</c> from identifiers, so <c>title ~= "x"</c> is read as the
/// property <c>~</c> with the value <c>"x"</c> rather than as an operator token.
/// </remarks>
public sealed class MatcherOperatorTests
{
    private static WindowRule Load(string matcher)
    {
        ConfigLoadResult result = ConfigLoader.Load($$"""
            rules {
                rule "r" {
                    match { {{matcher}} }
                    do { ignore }
                }
            }
            """);

        Assert.False(
            result.HasErrors,
            $"'{matcher}' did not load:\n" +
            string.Join("\n", result.Errors.Select(d => d.ToString())));

        return Assert.Single(result.Config.Rules);
    }

    [Theory]
    // Named form.
    [InlineData("title equals=\"x\"", MatchOperator.Equals)]
    [InlineData("title is=\"x\"", MatchOperator.Equals)]
    [InlineData("title regex=\"x\"", MatchOperator.Regex)]
    [InlineData("title matches=\"x\"", MatchOperator.Regex)]
    [InlineData("title starts-with=\"x\"", MatchOperator.StartsWith)]
    [InlineData("title prefix=\"x\"", MatchOperator.StartsWith)]
    [InlineData("title ends-with=\"x\"", MatchOperator.EndsWith)]
    [InlineData("title suffix=\"x\"", MatchOperator.EndsWith)]
    [InlineData("title contains=\"x\"", MatchOperator.Contains)]
    // Symbolic form.
    [InlineData("title = \"x\"", MatchOperator.Equals)]
    [InlineData("title ~= \"x\"", MatchOperator.Regex)]
    [InlineData("title ^= \"x\"", MatchOperator.StartsWith)]
    [InlineData("title $= \"x\"", MatchOperator.EndsWith)]
    [InlineData("title *= \"x\"", MatchOperator.Contains)]
    public void EveryDocumentedOperatorIsUnderstood(string matcher, MatchOperator expected)
    {
        WindowRule rule = Load(matcher);
        WindowMatcher parsed = Assert.Single(rule.Matchers);

        Assert.Equal(expected, parsed.Operator);
        Assert.Equal("x", parsed.Pattern);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("class-name")]
    [InlineData("process")]
    [InlineData("process-name")]
    [InlineData("path")]
    [InlineData("process-path")]
    [InlineData("title")]
    public void EveryDocumentedTargetIsUnderstood(string target)
    {
        WindowRule rule = Load($"{target} = \"x\"");

        Assert.Single(rule.Matchers);
    }

    [Fact]
    public void AnUnknownMatcherIsReported()
    {
        // Dropped in silence before, so a misspelt target produced a rule that
        // matched on nothing else in the block and quietly did the wrong thing.
        ConfigLoadResult result = ConfigLoader.Load("""
            rules {
                rule "r" {
                    match { proces = "firefox" }
                    do { ignore }
                }
            }
            """);

        Assert.Contains(result.Diagnostics, d => d.Code == "SHB0426");
    }

    [Fact]
    public void ARegexThatRunsOutOfTimeDoesNotMatchRatherThanThrowing()
    {
        // Catastrophic backtracking: every nested quantifier on a value that nearly
        // matches. The timeout exists so this cannot hold the window manager's loop;
        // throwing when it fired lost the event that prompted the match and every one
        // queued behind it, so the window was never managed at all.
        var matcher = new WindowMatcher(
            MatchTarget.Title, MatchOperator.Regex, "^(a+)+$", Negated: false, default);

        string value = new string('a', 40) + "!";

        bool matched = matcher.Matches(value);

        Assert.False(matched);

        // Asked again the answer is the same, and still an answer.
        Assert.False(matcher.Matches(value));

        // And a negated matcher reads the timeout the same way it reads any non-match.
        var negated = matcher with { Negated = true };
        Assert.True(negated.Matches(value));
    }
}
