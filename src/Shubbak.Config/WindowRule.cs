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
    /// <remarks>
    /// Case-insensitive throughout. Window titles and class names vary in casing
    /// between versions of the same application often enough that case-sensitive
    /// matching is a trap rather than a feature.
    /// </remarks>
    public bool Matches(string? value)
    {
        value ??= string.Empty;

        bool result = Operator switch
        {
            MatchOperator.Equals => string.Equals(value, Pattern, StringComparison.OrdinalIgnoreCase),
            MatchOperator.StartsWith => value.StartsWith(Pattern, StringComparison.OrdinalIgnoreCase),
            MatchOperator.EndsWith => value.EndsWith(Pattern, StringComparison.OrdinalIgnoreCase),
            MatchOperator.Contains => value.Contains(Pattern, StringComparison.OrdinalIgnoreCase),
            MatchOperator.Regex => CompiledRegex().IsMatch(value),
            _ => false,
        };

        return Negated ? !result : result;
    }

    private Regex CompiledRegex() =>
        // Compiled lazily and cached: a rule set can hold dozens of patterns, and
        // most are never exercised in a given session.
        _compiled ??= new Regex(
            Pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

    public override string ToString()
    {
        string op = Operator switch
        {
            MatchOperator.Equals => "=",
            MatchOperator.Regex => "~=",
            MatchOperator.StartsWith => "^=",
            MatchOperator.EndsWith => "$=",
            MatchOperator.Contains => "*=",
            _ => "?",
        };

        return $"{(Negated ? "!" : "")}{Target.ToString().ToLowerInvariant()}{op}\"{Pattern}\"";
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
