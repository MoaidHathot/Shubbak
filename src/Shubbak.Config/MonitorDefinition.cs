using System.Text.RegularExpressions;

namespace Shubbak.Config;

/// <summary>Which fact about a display a monitor matcher looks at.</summary>
public enum MonitorMatchTarget
{
    /// <summary>The name the panel reports in its EDID: <c>DELL U3219Q</c>.</summary>
    FriendlyName,

    /// <summary>The connector's device interface path, stable across replug and renumbering.</summary>
    DevicePath,

    /// <summary>The GDI name Windows hands out in enumeration order: <c>\\.\DISPLAY1</c>.</summary>
    DeviceId,
}

/// <summary>
/// One condition on a display.
/// </summary>
/// <param name="Target">Which fact to inspect.</param>
/// <param name="Operator">How to compare.</param>
/// <param name="Pattern">The pattern as written.</param>
/// <param name="Negated">Whether the condition is inverted.</param>
/// <param name="Span">Where in the config it came from.</param>
public sealed record MonitorMatcher(
    MonitorMatchTarget Target,
    MatchOperator Operator,
    string Pattern,
    bool Negated,
    TextSpan Span)
{
    private Regex? _compiled;

    /// <summary>Tests one fact.</summary>
    public bool Matches(string? value)
    {
        bool result = PatternMatch.Test(Operator, Pattern, value, ref _compiled);

        return Negated ? !result : result;
    }

    public override string ToString()
    {
        string target = Target switch
        {
            MonitorMatchTarget.FriendlyName => "name",
            MonitorMatchTarget.DevicePath => "path",
            MonitorMatchTarget.DeviceId => "device",
            _ => "?",
        };

        return $"{(Negated ? "!" : "")}{target}{PatternMatch.Symbol(Operator)}\"{Pattern}\"";
    }
}

/// <summary>
/// A display, described by what it is rather than by where Windows happened to
/// enumerate it.
/// </summary>
/// <param name="Name">The name workspaces and commands refer to it by.</param>
/// <param name="Matchers">Conditions on its name, path or device id; all must hold.</param>
/// <param name="Internal">
/// When set, whether the panel must be built in (<c>true</c>) or must not be
/// (<c>false</c>). A panel whose kind the platform layer could not determine matches
/// neither.
/// </param>
/// <param name="Primary">When set, whether it must be the primary display.</param>
/// <param name="Span">Where in the config it came from.</param>
/// <remarks>
/// <para>
/// <c>monitor=1</c> on a workspace names a position in the enumeration, and the
/// enumeration is reordered by Windows on replug, on DisplayPort wake and on a driver
/// restart - which is how a workspace bound to "the right-hand screen" ends up on the
/// left one after a dock. This names the screen instead, by what the display
/// configuration reports about it, so the binding follows the panel.
/// </para>
/// <para>
/// A definition may match several displays at once - two of the same model report
/// the same friendly name, and this project's own desktop is the example - so
/// whoever resolves a name to a display takes the first match, and the device path
/// is how to tell twins apart.
/// </para>
/// </remarks>
public sealed record MonitorDefinition(
    string Name,
    IReadOnlyList<MonitorMatcher> Matchers,
    bool? Internal,
    bool? Primary,
    TextSpan Span)
{
    /// <summary>Whether anything at all was asked of the display.</summary>
    public bool HasConditions => Matchers.Count > 0 || Internal is not null || Primary is not null;

    /// <summary>True when every condition holds.</summary>
    /// <remarks>
    /// A definition with no conditions matches nothing, not everything. The loader warns
    /// about one; matching every display would make the warning describe a definition
    /// that also, silently, took over every workspace bound to it.
    /// </remarks>
    public bool Matches(MonitorAttributes monitor)
    {
        if (!HasConditions) return false;

        foreach (MonitorMatcher matcher in Matchers)
            if (!matcher.Matches(monitor.Get(matcher.Target))) return false;

        // Unknown is not a value. A display whose kind could not be read is neither
        // built in nor external as far as a condition is concerned, so a definition
        // asking either way does not match it - which is the answer that cannot bind
        // a workspace to the wrong screen.
        if (Internal is { } internalWanted && monitor.IsInternal != internalWanted) return false;
        if (Primary is { } primaryWanted && monitor.IsPrimary != primaryWanted) return false;

        return true;
    }
}

/// <summary>The facts about a display a definition can see.</summary>
/// <param name="DeviceId">The GDI name, <c>\\.\DISPLAY1</c>.</param>
/// <param name="FriendlyName">The EDID name, or null when the panel reports none.</param>
/// <param name="DevicePath">The connector's device interface path, or null when unknown.</param>
/// <param name="IsInternal">Whether the panel is built in, or null when unknown.</param>
/// <param name="IsPrimary">Whether it is the primary display.</param>
public readonly record struct MonitorAttributes(
    string DeviceId,
    string? FriendlyName,
    string? DevicePath,
    bool? IsInternal,
    bool IsPrimary)
{
    public string? Get(MonitorMatchTarget target) => target switch
    {
        MonitorMatchTarget.FriendlyName => FriendlyName,
        MonitorMatchTarget.DevicePath => DevicePath,
        MonitorMatchTarget.DeviceId => DeviceId,
        _ => null,
    };
}
