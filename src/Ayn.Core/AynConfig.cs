using Shubbak.Config;
using Shubbak.Config.Kdl;

namespace Ayn.Core;

/// <summary>
/// The watcher's own settings: which context to hold for which fact, and how long a
/// change has to last before it is believed.
/// </summary>
/// <param name="CameraInUse">The context held while any program has the camera open, or null to say nothing about it.</param>
/// <param name="MicrophoneInUse">The context held while any program has the microphone open, or null.</param>
/// <param name="MicrophoneMuted">The context held while the default microphone is muted at the system level, or null.</param>
/// <param name="Settle">
/// How long a device has to stay in or out of use before the window manager is told,
/// or null for the default. A call opens and closes the camera several times while it
/// is setting up, and a context that flapped with it would run its on-enter and
/// on-exit twice. Nullable rather than zero-for-unsaid, because zero is a value
/// somebody can mean: believe it at once. Mute is never settled: a person who pressed
/// the key wants the icon now.
/// </param>
/// <remarks>
/// Facts are named <c>subject-state</c> - <c>camera-in-use</c>, <c>microphone-muted</c>
/// - so that the ones about one device sort together and the next device slots in
/// without anything being renamed. The defaults are those names; the file can rename
/// any of them or turn any of them off.
/// </remarks>
public sealed record AynConfig(
    string? CameraInUse = "camera-in-use",
    string? MicrophoneInUse = "microphone-in-use",
    string? MicrophoneMuted = "microphone-muted",
    TimeSpan? Settle = null)
{
    /// <summary>What <see cref="Settle"/> is when the file does not say.</summary>
    public static TimeSpan DefaultSettle { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The settle time, with the default applied.</summary>
    public TimeSpan EffectiveSettle => Settle ?? DefaultSettle;

    /// <summary>The context for a fact, or null when that fact is not reported.</summary>
    public string? ContextFor(Fact fact) => fact switch
    {
        Fact.CameraInUse => CameraInUse,
        Fact.MicrophoneInUse => MicrophoneInUse,
        Fact.MicrophoneMuted => MicrophoneMuted,
        _ => null,
    };

    /// <summary>Whether anything is reported at all.</summary>
    public bool WatchesAnything => CameraInUse is not null || MicrophoneInUse is not null || MicrophoneMuted is not null;

    /// <summary>Whether the consent store needs watching: either device's use is reported.</summary>
    public bool NeedsConsentStore => CameraInUse is not null || MicrophoneInUse is not null;

    /// <summary>Whether the audio endpoint needs watching: the microphone's mute is reported.</summary>
    public bool NeedsAudioEndpoint => MicrophoneMuted is not null;
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
/// Nested by subject, so the next device slots in beside these two:
/// </para>
/// <code>
/// ayn {
///     camera     { in-use "camera-in-use" }
///     microphone { in-use "microphone-in-use"; muted "microphone-muted" }
///     settle 500
/// }
/// </code>
/// <para>
/// <c>camera #false</c> turns a whole device off; <c>muted #false</c> turns one fact
/// off. The KDL parser's own diagnostics are not repeated here: the window manager's
/// loader reports them, and <c>shubbak check-config</c> runs both. A missing section
/// means the defaults, which report all three facts under their own names.
/// </para>
/// </remarks>
public static class AynConfigLoader
{
    /// <summary>Every setting the section accepts, for the unknown-setting warning.</summary>
    public static IReadOnlyList<string> KnownKeys { get; } = ["camera", "microphone", "settle"];

    /// <summary>What a <c>camera</c> block accepts.</summary>
    public static IReadOnlyList<string> KnownCameraKeys { get; } = ["in-use"];

    /// <summary>What a <c>microphone</c> block accepts.</summary>
    public static IReadOnlyList<string> KnownMicrophoneKeys { get; } = ["in-use", "muted"];

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
            ? Read(node, DeclaredContexts(parsed.Document), diagnostics)
            : new AynConfig();

        return new AynConfigLoad(config, diagnostics);
    }

    private static AynConfig Read(KdlNode node, List<string> declaredContexts, List<Diagnostic> diagnostics)
    {
        WarnAboutUnknown(node, KnownKeys, "ayn", diagnostics);

        var defaults = new AynConfig();

        (string? cameraInUse, _) = Device(node, "camera", KnownCameraKeys, defaults.CameraInUse, null, declaredContexts, diagnostics);
        (string? microphoneInUse, string? microphoneMuted) = Device(
            node, "microphone", KnownMicrophoneKeys, defaults.MicrophoneInUse, defaults.MicrophoneMuted, declaredContexts, diagnostics);

        return new AynConfig(cameraInUse, microphoneInUse, microphoneMuted, Settle(node, diagnostics));
    }

    /// <summary>
    /// One device's block: <c>camera { in-use "..." }</c>, or <c>camera #false</c> for
    /// none of it.
    /// </summary>
    private static (string? InUse, string? Muted) Device(
        KdlNode parent, string device, IReadOnlyList<string> known,
        string? inUseDefault, string? mutedDefault,
        List<string> declaredContexts, List<Diagnostic> diagnostics)
    {
        KdlNode? node = parent.Child(device);

        if (node is null)
        {
            // Property form: camera=#false.
            if (parent.Property(device) is { } flag && flag.TryAsBool(out bool on) && !on) return (null, null);

            return (inUseDefault, mutedDefault);
        }

        if (node.Argument(0) is { } argument && argument.TryAsBool(out bool enabled))
            return enabled ? (inUseDefault, mutedDefault) : (null, null);

        WarnAboutUnknown(node, known, device, diagnostics);

        string? inUse = ContextName(node, "in-use", inUseDefault, device, declaredContexts, diagnostics);
        string? muted = mutedDefault is null
            ? null
            : ContextName(node, "muted", mutedDefault, device, declaredContexts, diagnostics);

        return (inUse, muted);
    }

    /// <summary>
    /// A context name, or null for <c>#false</c>, or the default when unsaid.
    /// </summary>
    /// <remarks>
    /// An empty string is refused rather than read as "off", because <c>in-use ""</c>
    /// is far more likely a slip than a decision. A name the file's <c>contexts</c>
    /// section does not declare is pointed out here rather than found in the log
    /// after the window manager refused it - but only for a name the file wrote: the
    /// defaults are not warned about, or a file with no interest in the watcher would
    /// be told about three contexts it never mentioned.
    /// </remarks>
    private static string? ContextName(
        KdlNode node, string key, string? fallback, string device,
        List<string> declaredContexts, List<Diagnostic> diagnostics)
    {
        if (Setting(node, key) is not { } value) return fallback;

        if (value.TryAsBool(out bool enabled)) return enabled ? fallback : null;

        string name = value.AsString();

        if (name.Length == 0)
        {
            diagnostics.Add(Diagnostic.Warning(
                "AYN0002",
                $"'{device} {key}' names no context; the default '{fallback}' is used.",
                value.Span,
                $"Write {key} \"{fallback}\" to name the context, or {key} #false to say nothing about it."));

            return fallback;
        }

        if (!declaredContexts.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(Diagnostic.Warning(
                "AYN0005",
                $"'{device} {key}' names context '{name}', which the contexts section does not declare; " +
                "the window manager will refuse to hold it.",
                value.Span,
                declaredContexts.Count == 0
                    ? $"Add a contexts {{ }} section with context \"{name}\" {{ }} in it."
                    : Suggestion.Closest(name, declaredContexts) is { } guess
                        ? $"Did you mean '{guess}'?"
                        : $"Declared: {string.Join(", ", declaredContexts)}."));
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

    /// <summary>The names the <c>contexts</c> section declares, from the same document.</summary>
    private static List<string> DeclaredContexts(KdlDocument document)
    {
        List<string> names = [];

        if (document.Node("contexts") is { } section)
        {
            foreach (KdlNode context in section.ChildrenNamed("context"))
                if (context.Argument(0)?.AsString() is { Length: > 0 } name)
                    names.Add(name);
        }

        return names;
    }

    private static void WarnAboutUnknown(KdlNode node, IReadOnlyList<string> known, string where, List<Diagnostic> diagnostics)
    {
        foreach (KdlNode child in node.Children) Warn(child.Name, child.NameSpan);

        foreach ((string name, KdlValue value) in node.Properties) Warn(name, value.Span);

        void Warn(string name, TextSpan span)
        {
            if (known.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

            diagnostics.Add(Diagnostic.Warning(
                "AYN0001",
                $"Unknown setting '{name}' in '{where}'; it will be ignored.",
                span,
                Suggestion.Closest(name, known) is { } guess ? $"Did you mean '{guess}'?" : null));
        }
    }

    private static KdlValue? Setting(KdlNode node, string name) =>
        node.Child(name)?.Argument(0) ?? node.Property(name);
}
