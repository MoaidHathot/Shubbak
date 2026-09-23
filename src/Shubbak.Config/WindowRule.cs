using Shubbak.Core.Diagnostics;
using System.Text.RegularExpressions;

namespace Shubbak.Config;

/// <summary>How a matcher compares its pattern to a window attribute.</summary>
public enum MatchOperator
{
    /// <summary><c>=</c> - exact, case-insensitive.</summary>
    Equals,

    /// <summary><c>~=</c> - regular expression.</summary>
    Regex,

    /// <summary><c>^=</c> - prefix.</summary>
    StartsWith,

    /// <summary><c>$=</c> - suffix.</summary>
    EndsWith,

    /// <summary><c>*=</c> - substring.</summary>
    Contains,
}

/// <summary>Which window attribute a matcher looks at.</summary>
public enum MatchTarget
{
    Title,
    ClassName,
    ProcessName,
    ProcessPath,
}

/// <summary>
/// The comparison every matcher performs, whatever it is matching.
/// </summary>
/// <remarks>
/// Case-insensitive throughout. Window titles and class names vary in casing
/// between versions of the same application often enough that case-sensitive
/// matching is a trap rather than a feature, and monitor names arrive in whatever
/// case the panel's firmware chose.
/// </remarks>
public static class PatternMatch
{
    /// <summary>Tests one value against a pattern.</summary>
    /// <param name="op">How to compare.</param>
    /// <param name="pattern">The pattern as written.</param>
    /// <param name="value">The attribute; null reads as empty.</param>
    /// <param name="compiled">
    /// The regex cache for this pattern, compiled on first use. A rule set can hold
    /// dozens of patterns, and most are never exercised in a given session.
    /// </param>
    public static bool Test(MatchOperator op, string pattern, string? value, ref Regex? compiled)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        value ??= string.Empty;

        return op switch
        {
            MatchOperator.Equals => string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase),
            MatchOperator.StartsWith => value.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
            MatchOperator.EndsWith => value.EndsWith(pattern, StringComparison.OrdinalIgnoreCase),
            MatchOperator.Contains => value.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            MatchOperator.Regex => MatchesRegex(pattern, value, ref compiled),
            _ => false,
        };
    }

    /// <summary>How long a regex may take on one window before it is read as not matching.</summary>
    public static TimeSpan RegexTimeout { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A regex that runs out of time does not match.
    /// </summary>
    /// <remarks>
    /// The timeout exists so a pattern with catastrophic backtracking cannot hold the
    /// window manager's loop while a long title is examined. Throwing when it fires
    /// defeated that purpose from the other side: the exception left the rule engine,
    /// the event that had prompted the match was lost with every event queued behind
    /// it, and the window was never managed at all - and the loop had already paid the
    /// hundred milliseconds. Not matching is the answer a rule that cannot be evaluated
    /// deserves; the pattern is named once so it can be fixed.
    /// </remarks>
    private static bool MatchesRegex(string pattern, string value, ref Regex? compiled)
    {
        compiled ??= new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

        try
        {
            return compiled.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            bool first;
            lock (s_timedOut) first = s_timedOut.Add(pattern);

            if (first)
            {
                Log.Warn(
                    LogCategory.Rule,
                    $"the pattern /{pattern}/ took more than {RegexTimeout.TotalMilliseconds:F0} ms against a {value.Length}-character value and is read as not matching; it probably backtracks badly");
            }

            return false;
        }
    }

    /// <summary>The patterns that have timed out, so each is remarked on once.</summary>
    private static readonly HashSet<string> s_timedOut = new(StringComparer.Ordinal);

    /// <summary>The operator as the config writes it.</summary>
    public static string Symbol(MatchOperator op) => op switch
    {
        MatchOperator.Equals => "=",
        MatchOperator.Regex => "~=",
        MatchOperator.StartsWith => "^=",
        MatchOperator.EndsWith => "$=",
        MatchOperator.Contains => "*=",
        _ => "?",
    };
}

/// <summary>
/// One condition on a window.
/// </summary>
/// <param name="Target">Which attribute to inspect.</param>
/// <param name="Operator">How to compare.</param>
/// <param name="Pattern">The pattern as written.</param>
/// <param name="Negated">Whether the condition is inverted.</param>
/// <param name="Span">Where in the config it came from.</param>
public sealed record WindowMatcher(
    MatchTarget Target,
    MatchOperator Operator,
    string Pattern,
    bool Negated,
    TextSpan Span)
{
    private Regex? _compiled;

    /// <summary>Tests one attribute value.</summary>
    public bool Matches(string? value)
    {
        bool result = PatternMatch.Test(Operator, Pattern, value, ref _compiled);

        return Negated ? !result : result;
    }

    public override string ToString()
    {
        // The config's spelling, not the enum's: `process=` is what a person wrote and
        // what a report should hand back to them.
        string target = Target switch
        {
            MatchTarget.Title => "title",
            MatchTarget.ClassName => "class",
            MatchTarget.ProcessName => "process",
            MatchTarget.ProcessPath => "path",
            _ => Target.ToString().ToLowerInvariant(),
        };

        return $"{(Negated ? "!" : "")}{target}{PatternMatch.Symbol(Operator)}\"{Pattern}\"";
    }
}

/// <summary>
/// A named, reusable set of matchers.
/// </summary>
/// <remarks>
/// Lets rules read semantically - <c>match app="browser-pip"</c> rather than a wall
/// of inline regexes - and lets one definition be shared by several rules.
/// </remarks>
public sealed record AppDefinition(string Name, IReadOnlyList<WindowMatcher> Matchers, TextSpan Span)
{
    /// <summary>True when every matcher matches.</summary>
    public bool Matches(WindowAttributes window)
    {
        foreach (WindowMatcher matcher in Matchers)
            if (!matcher.Matches(window.Get(matcher.Target))) return false;

        return Matchers.Count > 0;
    }
}

/// <summary>The window attributes a rule can see.</summary>
/// <param name="Title">Current window title.</param>
/// <param name="ClassName">Window class.</param>
/// <param name="ProcessName">Executable name without extension.</param>
/// <param name="ProcessPath">Full executable path, when readable.</param>
public readonly record struct WindowAttributes(
    string Title,
    string ClassName,
    string ProcessName,
    string? ProcessPath)
{
    public string? Get(MatchTarget target) => target switch
    {
        MatchTarget.Title => Title,
        MatchTarget.ClassName => ClassName,
        MatchTarget.ProcessName => ProcessName,
        MatchTarget.ProcessPath => ProcessPath,
        _ => null,
    };
}

/// <summary>When a rule is evaluated.</summary>
public enum RuleTrigger
{
    /// <summary>When the window first comes under management.</summary>
    OnManage,

    /// <summary>Whenever the window's title changes.</summary>
    OnTitleChange,

    /// <summary>Whenever the window gains focus.</summary>
    OnFocus,
}

/// <summary>
/// A window rule: conditions, and commands to run when they hold.
/// </summary>
/// <param name="Name">Label used in diagnostics and by <c>shubbak inspect</c>.</param>
/// <param name="Trigger">When to evaluate.</param>
/// <param name="Matchers">Inline conditions; all must match.</param>
/// <param name="AppReferences">Named app definitions; any one matching is enough.</param>
/// <param name="Commands">Commands to run, in order.</param>
/// <param name="Span">Where in the config it came from.</param>
public sealed record WindowRule(
    string Name,
    RuleTrigger Trigger,
    IReadOnlyList<WindowMatcher> Matchers,
    IReadOnlyList<string> AppReferences,
    IReadOnlyList<Core.Commands.WmCommand> Commands,
    TextSpan Span)
{
    /// <summary>
    /// Whether this rule applies to a window.
    /// </summary>
    /// <remarks>
    /// Inline matchers are combined with AND, app references with OR. That mirrors
    /// how the two are used: inline conditions narrow a single window down, whereas
    /// a list of apps enumerates alternatives.
    /// </remarks>
    public bool Matches(WindowAttributes window, IReadOnlyDictionary<string, AppDefinition> apps)
    {
        ArgumentNullException.ThrowIfNull(apps);

        foreach (WindowMatcher matcher in Matchers)
            if (!matcher.Matches(window.Get(matcher.Target))) return false;

        if (AppReferences.Count == 0) return Matchers.Count > 0;

        foreach (string reference in AppReferences)
            if (apps.TryGetValue(reference, out AppDefinition? app) && app.Matches(window)) return true;

        return false;
    }
}
