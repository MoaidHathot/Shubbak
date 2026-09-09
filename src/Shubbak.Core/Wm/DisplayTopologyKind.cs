namespace Shubbak.Core.Wm;

/// <summary>
/// The arrangement the user chose with Win+P.
/// </summary>
/// <remarks>
/// <para>
/// Named for the four choices on that panel rather than the constants behind them,
/// because the choice is what a person recognises. <c>Unknown</c> is a failed call or
/// a machine with no display at all.
/// </para>
/// <para>
/// Lives here rather than in the platform layer because it is written in config files
/// - a context can ask <c>display-topology "extend"</c> - and the config library has
/// no Win32 in it. The platform layer reads it; this names it.
/// </para>
/// </remarks>
public enum DisplayTopologyKind
{
    /// <summary>Could not be read.</summary>
    Unknown,

    /// <summary>"PC screen only": the built-in panel and nothing else.</summary>
    Internal,

    /// <summary>"Duplicate": every display shows the same desktop.</summary>
    Clone,

    /// <summary>"Extend": one desktop across several displays.</summary>
    Extend,

    /// <summary>"Second screen only": the built-in panel is off.</summary>
    External,
}

/// <summary>
/// The names a <see cref="DisplayTopologyKind"/> goes by outside the process.
/// </summary>
/// <remarks>
/// Spelt out rather than derived from the member names, for the same reason
/// <see cref="UserActivityNames"/> are: renaming a member must not change what a config
/// file or a subscriber reads.
/// </remarks>
public static class DisplayTopologyNames
{
    /// <summary>The wire name of <paramref name="kind"/>.</summary>
    public static string Wire(this DisplayTopologyKind kind) => kind switch
    {
        DisplayTopologyKind.Internal => "internal",
        DisplayTopologyKind.Clone => "clone",
        DisplayTopologyKind.Extend => "extend",
        DisplayTopologyKind.External => "external",
        _ => "unknown",
    };

    /// <summary>The kind a wire name stands for, or null if it is not one.</summary>
    public static DisplayTopologyKind? Parse(string? name) => name?.ToLowerInvariant() switch
    {
        "internal" => DisplayTopologyKind.Internal,
        "clone" or "duplicate" => DisplayTopologyKind.Clone,
        "extend" => DisplayTopologyKind.Extend,
        "external" => DisplayTopologyKind.External,
        "unknown" => DisplayTopologyKind.Unknown,
        _ => null,
    };

    /// <summary>The names a config file may write, for a diagnostic to list.</summary>
    public static readonly string[] Accepted = ["internal", "clone", "extend", "external"];
}
