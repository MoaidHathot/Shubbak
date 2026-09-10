using Shubbak.Config;
using Shubbak.Config.Kdl;

namespace Ayn.Core;

/// <summary>
/// The watcher's own settings: which context to hold for which device, and how long
/// a change has to last before it is believed.
/// </summary>
/// <param name="Camera">
/// The context held while any program has the camera open, or null to leave the
/// camera alone.
/// </param>
/// <param name="Microphone">
/// The context held while any program has the microphone open, or null to leave the
/// microphone alone.
/// </param>
/// <param name="Settle">
/// How long a device has to stay in its new state before the window manager is told,
/// or null for the default. A call opens and closes the camera several times while it
/// is setting up, and a context that flapped with it would run its on-enter and
/// on-exit twice. Nullable rather than zero-for-unsaid, because zero is a value
/// somebody can mean: believe it at once.
/// </param>
public sealed record AynConfig(
    string? Camera = "camera",
    string? Microphone = "microphone",
    TimeSpan? Settle = null)
{
    /// <summary>What <see cref="Settle"/> is when the file does not say.</summary>
    public static TimeSpan DefaultSettle { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The settle time, with the default applied.</summary>
    public TimeSpan EffectiveSettle => Settle ?? DefaultSettle;

    /// <summary>The context for a device, or null when that device is not watched.</summary>
    public string? ContextFor(DeviceKind device) => device switch
    {
        DeviceKind.Camera => Camera,
        DeviceKind.Microphone => Microphone,
        _ => null,
    };

    /// <summary>Whether anything is watched at all.</summary>
    public bool WatchesAnything => Camera is not null || Microphone is not null;
}

/// <summary>The result of reading the <c>ayn</c> section.</summary>
/// <param name="Config">What was read, with defaults for anything not said.</param>
/// <param name="Diagnostics">What was wrong with it. Warnings only; nothing here is fatal.</param>
public sealed record AynConfigLoad(AynConfig Config, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Reads the <c>ayn</c> section of the shared configuration file.
/// </summary>
/// <remarks>
/// <para>
/// Same file and same parser as the window manager, the bar and the palette. A
/// watcher with its own file would be a second place to look for why a meeting was
/// not noticed.
/// </para>
/// <para>
/// The KDL parser's own diagnostics are not repeated here: the window manager's
/// loader reports them, and <c>shubbak check-config</c> runs both. A missing section
/// means the defaults, which watch both devices under the names <c>camera</c> and
/// <c>microphone</c>.
/// </para>
/// </remarks>
public static class AynConfigLoader
{
    /// <summary>Every setting the section accepts, for the unknown-setting warning.</summary>
    public static IReadOnlyList<string> KnownKeys { get; } = ["camera", "microphone", "settle"];

    /// <summary>The longest a change may be asked to hold before it is believed.</summary>
    public const int MaxSettleMilliseconds = 10_000;

    public static AynConfig Load(string source) => Validate(source).Config;

    public static AynConfigLoad Validate(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<Diagnostic> diagnostics = [];
        KdlParseResult parsed = KdlParser.Parse(source);

        if (parsed.HasErrors) return new AynConfigLoad(new AynConfig(), diagnostics);

        AynConfig config = parsed.Document.Node("ayn") is { } node
            ? Read(node, diagnostics)
            : new AynConfig();

        return new AynConfigLoad(config, diagnostics);
    }

    private static AynConfig Read(KdlNode node, List<Diagnostic> diagnostics)
    {
        WarnAboutUnknown(node, diagnostics);

        var defaults = new AynConfig();

        return new AynConfig(
            ContextName(node, "camera", defaults.Camera, diagnostics),
            ContextName(node, "microphone", defaults.Microphone, diagnostics),
            Settle(node, diagnostics));
    }

    /// <summary>
    /// A context name, or null for <c>#false</c>, or the default when unsaid.
    /// </summary>
    /// <remarks>
    /// <c>camera #false</c> is how a device is left alone: the watcher still runs for
    /// the other one, and says nothing about this one. An empty string is refused
    /// rather than read as "off", because <c>camera ""</c> is far more likely a slip
    /// than a decision.
    /// </remarks>
    private static string? ContextName(KdlNode node, string key, string? fallback, List<Diagnostic> diagnostics)
    {
        if (Setting(node, key) is not { } value) return fallback;

        if (value.TryAsBool(out bool enabled)) return enabled ? fallback : null;

        string name = value.AsString();

        if (name.Length == 0)
        {
            diagnostics.Add(Diagnostic.Warning(
                "AYN0002",
                $"'{key}' names no context; the default '{fallback}' is used.",
                value.Span,
                $"Write {key} \"{fallback}\" to name the context, or {key} #false to leave the {key} alone."));

            return fallback;
        }

        return name;
    }

    private static TimeSpan? Settle(KdlNode node, List<Diagnostic> diagnostics)
    {
        if (Setting(node, "settle") is not { } value) return null;

        if (!value.TryAsInt(out int milliseconds))
        {
            diagnostics.Add(Diagnostic.Warning(
                "AYN0003",
                $"'settle' should be a whole number of milliseconds, not '{value.AsString()}'; " +
                $"the default of {AynConfig.DefaultSettle.TotalMilliseconds:F0} is used.",
                value.Span));

            return null;
        }

        if (milliseconds < 0 || milliseconds > MaxSettleMilliseconds)
        {
            int clamped = Math.Clamp(milliseconds, 0, MaxSettleMilliseconds);

            diagnostics.Add(Diagnostic.Warning(
                "AYN0004",
                $"'settle' is {milliseconds}; it is kept between 0 and {MaxSettleMilliseconds}, so {clamped} is used.",
                value.Span));

            milliseconds = clamped;
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static void WarnAboutUnknown(KdlNode node, List<Diagnostic> diagnostics)
    {
        foreach (KdlNode child in node.Children) Warn(child.Name, child.NameSpan);

        foreach ((string name, KdlValue value) in node.Properties) Warn(name, value.Span);

        void Warn(string name, TextSpan span)
        {
            if (KnownKeys.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

            diagnostics.Add(Diagnostic.Warning(
                "AYN0001",
                $"Unknown setting '{name}' in 'ayn'; it will be ignored.",
                span,
                Suggestion.Closest(name, KnownKeys) is { } guess ? $"Did you mean '{guess}'?" : null));
        }
    }

    private static KdlValue? Setting(KdlNode node, string name) =>
        node.Child(name)?.Argument(0) ?? node.Property(name);
}
