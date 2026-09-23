using Shubbak.Config;
using Shubbak.Config.Kdl;

namespace Ayn.Core;

/// <summary>
/// A <c>by</c> rule: a context held while one particular program has a device.
/// </summary>
/// <param name="Device">The device.</param>
/// <param name="App">
/// The program, as its name appears in the consent store - <c>ms-teams.exe</c>,
/// <c>Microsoft.WindowsCamera</c> - with <c>*</c> and <c>?</c> as wildcards.
/// </param>
/// <param name="Context">The context to hold.</param>
/// <remarks>
/// <c>camera-in-use</c> says a meeting may be on; <c>camera { by "ms-teams.exe" "in-a-call" }</c>
/// says which meeting, so the file can treat a Teams call and an OBS stream
/// differently without a context knowing the difference between programs.
/// </remarks>
public sealed record AppRule(DeviceKind Device, string App, string Context);

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
/// <param name="ScreenCaptured">The context held while any program is capturing the screen, or null.</param>
/// <param name="SpeakerMuted">The context held while the default speaker is muted, or null.</param>
/// <param name="OnBattery">The context held while the machine runs on its battery, or null.</param>
/// <param name="BatteryLow">The context held while the battery is at or below <paramref name="BatteryLowPercent"/>, or null.</param>
/// <param name="LidClosed">The context held while the lid is shut, or null.</param>
/// <param name="UserAway">The context held while Windows judges nobody to be at the keyboard, or null.</param>
/// <param name="DarkTheme">The context held while apps are set to the dark theme, or null.</param>
/// <param name="BatteryLowPercent">What counts as low, in percent. Twenty unless the file says.</param>
/// <param name="Renew">
/// How often a held context is asserted again, with a time to live of twice that on
/// each hold, or null for never. Off unless the file says: the lease already dies with
/// the connection, and this is the belt for a watcher that is alive but stuck. A
/// stalled window manager that let a renewal miss its time to live would drop the
/// context and take it back a moment later, running whatever the file hangs on that,
/// which is not a risk to take without being asked.
/// </param>
/// <param name="CameraIgnores">Programs whose use of the camera does not count: a recording tool that keeps it open all day.</param>
/// <param name="MicrophoneIgnores">Programs whose use of the microphone does not count.</param>
/// <param name="ScreenIgnores">Programs whose capture of the screen does not count.</param>
/// <param name="AppRules">The <c>by</c> rules; see <see cref="AppRule"/>.</param>
/// <remarks>
/// Facts are named <c>subject-state</c> - <c>camera-in-use</c>, <c>microphone-muted</c>
/// - so that the ones about one device sort together and the next device slots in
/// without anything being renamed. The first three default to those names; every fact
/// added since is off until the file names it, so a file that never mentioned the
/// watcher does not wake up holding contexts it never declared.
/// </remarks>
public sealed record AynConfig(
    string? CameraInUse = "camera-in-use",
    string? MicrophoneInUse = "microphone-in-use",
    string? MicrophoneMuted = "microphone-muted",
    TimeSpan? Settle = null,
    string? ScreenCaptured = null,
    string? SpeakerMuted = null,
    string? OnBattery = null,
    string? BatteryLow = null,
    string? LidClosed = null,
    string? UserAway = null,
    string? DarkTheme = null,
    int BatteryLowPercent = 20,
    TimeSpan? Renew = null,
    IReadOnlyList<string>? CameraIgnores = null,
    IReadOnlyList<string>? MicrophoneIgnores = null,
    IReadOnlyList<string>? ScreenIgnores = null,
    IReadOnlyList<AppRule>? AppRules = null)
{
    /// <summary>What <see cref="Settle"/> is when the file does not say.</summary>
    public static TimeSpan DefaultSettle { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The settle time, with the default applied.</summary>
    public TimeSpan EffectiveSettle => Settle ?? DefaultSettle;

    /// <summary>The time to live put on each hold, or null: twice <see cref="Renew"/>.</summary>
    public TimeSpan? LeaseTtl => Renew is { } renew && renew > TimeSpan.Zero ? renew * 2 : null;

    /// <summary>Programs whose use of the camera does not count; empty when none.</summary>
    public IReadOnlyList<string> CameraIgnores { get; init; } = CameraIgnores ?? [];

    /// <summary>Programs whose use of the microphone does not count; empty when none.</summary>
    public IReadOnlyList<string> MicrophoneIgnores { get; init; } = MicrophoneIgnores ?? [];

    /// <summary>Programs whose capture of the screen does not count; empty when none.</summary>
    public IReadOnlyList<string> ScreenIgnores { get; init; } = ScreenIgnores ?? [];

    /// <summary>The <c>by</c> rules; empty when none.</summary>
    public IReadOnlyList<AppRule> AppRules { get; init; } = AppRules ?? [];

    /// <summary>The context for a fact, or null when that fact is not reported.</summary>
    public string? ContextFor(Fact fact) => fact switch
    {
        Fact.CameraInUse => CameraInUse,
        Fact.MicrophoneInUse => MicrophoneInUse,
        Fact.MicrophoneMuted => MicrophoneMuted,
        Fact.ScreenCaptured => ScreenCaptured,
        Fact.SpeakerMuted => SpeakerMuted,
        Fact.OnBattery => OnBattery,
        Fact.BatteryLow => BatteryLow,
        Fact.LidClosed => LidClosed,
        Fact.UserAway => UserAway,
        Fact.DarkTheme => DarkTheme,
        _ => null,
    };

    /// <summary>The context for a slot: a fact's, or a <c>by</c> rule's.</summary>
    public string? ContextFor(FactKey key)
    {
        if (key.App is null) return ContextFor(key.Fact);

        foreach (AppRule rule in AppRules)
        {
            if (rule.Device.InUseFact() == key.Fact && string.Equals(rule.App, key.App, StringComparison.OrdinalIgnoreCase))
                return rule.Context;
        }

        return null;
    }

    /// <summary>The programs whose use of a device does not count.</summary>
    public IReadOnlyList<string> IgnoredApps(DeviceKind device) => device switch
    {
        DeviceKind.Camera => CameraIgnores,
        DeviceKind.Microphone => MicrophoneIgnores,
        DeviceKind.Screen => ScreenIgnores,
        _ => [],
    };

    /// <summary>Whether a device's use is reported at all: its fact, or any rule about it.</summary>
    public bool Watches(DeviceKind device) =>
        ContextFor(device.InUseFact()) is not null || AppRules.Any(rule => rule.Device == device);

    /// <summary>Whether anything is reported at all.</summary>
    public bool WatchesAnything =>
        FactNames.All.Any(fact => ContextFor(fact) is not null) || AppRules.Count > 0;

    /// <summary>Whether the consent store needs watching: some device's use is reported.</summary>
    public bool NeedsConsentStore => DeviceKinds.All.Any(Watches);

    /// <summary>Whether the audio endpoint needs watching: the microphone's mute is reported.</summary>
    public bool NeedsAudioEndpoint => MicrophoneMuted is not null;

    /// <summary>Whether the speaker's mute is reported.</summary>
    public bool NeedsSpeakerEndpoint => SpeakerMuted is not null;

    /// <summary>Whether any power fact is reported.</summary>
    public bool NeedsPower =>
        OnBattery is not null || BatteryLow is not null || LidClosed is not null || UserAway is not null;

    /// <summary>Whether the theme is reported.</summary>
    public bool NeedsTheme => DarkTheme is not null;
}

/// <summary>The result of reading the <c>ayn</c> section.</summary>
/// <param name="Config">What was read, with defaults for anything not said.</param>
/// <param name="Diagnostics">What was wrong with it. Warnings only; nothing here is fatal.</param>
/// <param name="SyntaxErrors">
/// Whether the file did not parse at all, in which case <paramref name="Config"/> is
/// the defaults and <paramref name="Diagnostics"/> is empty - the parser's own
/// complaint is the window manager's to report, not this loader's to repeat. A host
/// starting up on such a file still owes the user one line saying which file it is
/// ignoring.
/// </param>
public sealed record AynConfigLoad(AynConfig Config, IReadOnlyList<Diagnostic> Diagnostics, bool SyntaxErrors = false);

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
/// Nested by subject, so the next device slots in beside these:
/// </para>
/// <code>
/// ayn {
///     camera     { in-use "camera-in-use"; ignore "obs64.exe"; by "ms-teams.exe" "in-a-call" }
///     microphone { in-use "microphone-in-use"; muted "microphone-muted" }
///     screen     { captured "screen-captured" }
///     speaker    { muted "speaker-muted" }
///     power      { on-battery "on-battery"; battery-low "battery-low"; battery-low-at 20; lid-closed "lid-closed"; user-away "user-away" }
///     theme      { dark "dark-theme" }
///     settle 500
///     renew 60
/// }
/// </code>
/// <para>
/// <c>camera #false</c> turns a whole device off; <c>muted #false</c> turns one fact
/// off. The KDL parser's own diagnostics are not repeated here: the window manager's
/// loader reports them, and <c>shubbak check-config</c> runs both. A missing section
/// means the defaults, which report the first three facts under their own names.
/// </para>
/// </remarks>
public static class AynConfigLoader
{
    /// <summary>Every setting the section accepts, for the unknown-setting warning.</summary>
    public static IReadOnlyList<string> KnownKeys { get; } =
        ["camera", "microphone", "screen", "speaker", "power", "theme", "settle", "renew"];

    /// <summary>What a <c>camera</c> block accepts.</summary>
    public static IReadOnlyList<string> KnownCameraKeys { get; } = ["in-use", "ignore", "by"];

    /// <summary>What a <c>microphone</c> block accepts.</summary>
    public static IReadOnlyList<string> KnownMicrophoneKeys { get; } = ["in-use", "muted", "ignore", "by"];

    /// <summary>What a <c>screen</c> block accepts.</summary>
    public static IReadOnlyList<string> KnownScreenKeys { get; } = ["captured", "ignore", "by"];

    /// <summary>What a <c>speaker</c> block accepts.</summary>
    public static IReadOnlyList<string> KnownSpeakerKeys { get; } = ["muted"];

    /// <summary>What a <c>power</c> block accepts.</summary>
    public static IReadOnlyList<string> KnownPowerKeys { get; } =
        ["on-battery", "battery-low", "battery-low-at", "lid-closed", "user-away"];

    /// <summary>What a <c>theme</c> block accepts.</summary>
    public static IReadOnlyList<string> KnownThemeKeys { get; } = ["dark"];

    /// <summary>The longest a change may be asked to hold before it is believed.</summary>
    public const int MaxSettleMilliseconds = 10_000;

    /// <summary>The shortest renewal the file may ask for, in seconds.</summary>
    public const int MinRenewSeconds = 5;

    public static AynConfig Load(string source) => Validate(source).Config;

    public static AynConfigLoad Validate(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<Diagnostic> diagnostics = [];
        KdlParseResult parsed = KdlParser.Parse(source);

        if (parsed.HasErrors) return new AynConfigLoad(new AynConfig(), diagnostics, SyntaxErrors: true);

        AynConfig config = parsed.Document.Node("ayn") is { } node
            ? Read(node, DeclaredContexts(parsed.Document), diagnostics)
            : new AynConfig();

        return new AynConfigLoad(config, diagnostics);
    }

    private static AynConfig Read(KdlNode node, List<string> declaredContexts, List<Diagnostic> diagnostics)
    {
        WarnAboutUnknown(node, KnownKeys, "ayn", diagnostics);

        var defaults = new AynConfig();
        var names = new Naming(declaredContexts, diagnostics);
        List<AppRule> rules = [];

        DeviceBlock camera = Device(node, DeviceKind.Camera, KnownCameraKeys, "in-use", defaults.CameraInUse, null, names, rules);
        DeviceBlock microphone = Device(node, DeviceKind.Microphone, KnownMicrophoneKeys, "in-use", defaults.MicrophoneInUse, defaults.MicrophoneMuted, names, rules);
        DeviceBlock screen = Device(node, DeviceKind.Screen, KnownScreenKeys, "captured", defaults.ScreenCaptured, null, names, rules);

        string? speakerMuted = null;

        if (Block(node, "speaker", KnownSpeakerKeys, diagnostics) is { } speaker)
            speakerMuted = names.Context(speaker, "muted", null, "speaker");

        string? onBattery = null, batteryLow = null, lidClosed = null, userAway = null;
        int batteryLowPercent = defaults.BatteryLowPercent;

        if (Block(node, "power", KnownPowerKeys, diagnostics) is { } power)
        {
            onBattery = names.Context(power, "on-battery", null, "power");
            batteryLow = names.Context(power, "battery-low", null, "power");
            lidClosed = names.Context(power, "lid-closed", null, "power");
            userAway = names.Context(power, "user-away", null, "power");
            batteryLowPercent = Percent(power, "battery-low-at", defaults.BatteryLowPercent, diagnostics);
        }

        string? darkTheme = null;

        if (Block(node, "theme", KnownThemeKeys, diagnostics) is { } theme)
            darkTheme = names.Context(theme, "dark", null, "theme");

        var config = new AynConfig(
            camera.InUse, microphone.InUse, microphone.Muted, Settle(node, diagnostics),
            screen.InUse, speakerMuted, onBattery, batteryLow, lidClosed, userAway, darkTheme,
            batteryLowPercent, Renew(node, diagnostics),
            camera.Ignores, microphone.Ignores, screen.Ignores, rules);

        WarnAboutSharedContexts(config, names, diagnostics);

        return config;
    }

    private readonly record struct DeviceBlock(string? InUse, string? Muted, IReadOnlyList<string> Ignores);

    /// <summary>
    /// One device's block: <c>camera { in-use "..." }</c>, or <c>camera #false</c> for
    /// none of it.
    /// </summary>
    private static DeviceBlock Device(
        KdlNode parent, DeviceKind device, IReadOnlyList<string> known, string inUseKey,
        string? inUseDefault, string? mutedDefault, Naming names, List<AppRule> rules)
    {
        string word = device.Word();
        KdlNode? node = parent.Child(word);

        if (node is null)
        {
            // Property form: camera=#false.
            if (parent.Property(word) is { } flag && flag.TryAsBool(out bool on) && !on) return new(null, null, []);

            return new(inUseDefault, mutedDefault, []);
        }

        if (node.Argument(0) is { } argument)
        {
            if (argument.TryAsBool(out bool enabled))
                return enabled ? new(inUseDefault, mutedDefault, []) : new(null, null, []);

            // camera "meeting" reads as a name for the device's use, which is what
            // somebody who wrote it meant; the shape that takes more facts is the
            // block, and the hint says so.
            string shorthand = argument.AsString();

            names.Diagnostics.Add(Diagnostic.Warning(
                "AYN0007",
                $"'{word} \"{shorthand}\"' is read as '{word} {{ {inUseKey} \"{shorthand}\" }}'.",
                argument.Span,
                $"Write {word} {{ {inUseKey} \"{shorthand}\" }}, which is where the device's other settings go too."));

            return new(shorthand.Length > 0 ? shorthand : inUseDefault, mutedDefault, []);
        }

        WarnAboutUnknown(node, known, word, names.Diagnostics);

        string? inUse = names.Context(node, inUseKey, inUseDefault, word);
        string? muted = mutedDefault is null ? null : names.Context(node, "muted", mutedDefault, word);

        List<string> ignores = [];

        foreach (KdlNode ignore in node.ChildrenNamed("ignore"))
        {
            foreach (KdlValue value in ignore.Arguments)
                if (value.AsString() is { Length: > 0 } pattern) ignores.Add(pattern);
        }

        foreach (KdlNode by in node.ChildrenNamed("by"))
        {
            string? app = by.Argument(0)?.AsString();
            KdlValue? contextValue = by.Argument(1);

            if (app is not { Length: > 0 } || contextValue is null || contextValue.AsString().Length == 0)
            {
                names.Diagnostics.Add(Diagnostic.Warning(
                    "AYN0008",
                    $"'{word} by' needs a program and a context; this rule is ignored.",
                    by.Span,
                    $"Write by \"ms-teams.exe\" \"in-a-call\" to hold in-a-call while that program has the {word}."));

                continue;
            }

            names.Declared(contextValue, $"{word} by \"{app}\"");
            rules.Add(new AppRule(device, app, contextValue.AsString()));
        }

        return new(inUse, muted, ignores);
    }

    /// <summary>A named block with nothing but settings in it, or null when absent or turned off.</summary>
    private static KdlNode? Block(KdlNode parent, string name, IReadOnlyList<string> known, List<Diagnostic> diagnostics)
    {
        KdlNode? node = parent.Child(name);
        if (node is null) return null;

        if (node.Argument(0) is { } argument && argument.TryAsBool(out bool enabled) && !enabled) return null;

        WarnAboutUnknown(node, known, name, diagnostics);
        return node;
    }

    /// <summary>
    /// Context names as the file spells them, with the checks every name gets.
    /// </summary>
    /// <remarks>
    /// An empty string is refused rather than read as "off", because <c>in-use ""</c>
    /// is far more likely a slip than a decision. A name the file's <c>contexts</c>
    /// section does not declare is pointed out here rather than found in the log
    /// after the window manager refused it - but only for a name the file wrote: the
    /// defaults are not warned about, or a file with no interest in the watcher would
    /// be told about three contexts it never mentioned.
    /// </remarks>
    private sealed class Naming(List<string> declaredContexts, List<Diagnostic> diagnostics)
    {
        public List<Diagnostic> Diagnostics => diagnostics;

        /// <summary>Where each context name was written, for the shared-context warning.</summary>
        public List<(string Context, string Where, TextSpan Span)> Written { get; } = [];

        /// <summary>A context name, or null for <c>#false</c>, or the default when unsaid.</summary>
        public string? Context(KdlNode node, string key, string? fallback, string where)
        {
            if (Setting(node, key) is not { } value) return fallback;

            if (value.TryAsBool(out bool enabled)) return enabled ? fallback : null;

            string name = value.AsString();

            if (name.Length == 0)
            {
                diagnostics.Add(Diagnostic.Warning(
                    "AYN0002",
                    fallback is null
                        ? $"'{where} {key}' names no context; the fact is not reported."
                        : $"'{where} {key}' names no context; the default '{fallback}' is used.",
                    value.Span,
                    fallback is null
                        ? $"Write {key} \"{key}\" to name the context."
                        : $"Write {key} \"{fallback}\" to name the context, or {key} #false to say nothing about it."));

                return fallback;
            }

            Declared(value, $"{where} {key}");
            return name;
        }

        /// <summary>Notes a written name and points out one the file does not declare.</summary>
        public void Declared(KdlValue value, string where)
        {
            string name = value.AsString();
            Written.Add((name, where, value.Span));

            if (declaredContexts.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

            diagnostics.Add(Diagnostic.Warning(
                "AYN0005",
                $"'{where}' names context '{name}', which the contexts section does not declare; " +
                "the window manager will refuse to hold it.",
                value.Span,
                declaredContexts.Count == 0
                    ? $"Add a contexts {{ }} section with context \"{name}\" {{ }} in it."
                    : Suggestion.Closest(name, declaredContexts) is { } guess
                        ? $"Did you mean '{guess}'?"
                        : $"Declared: {string.Join(", ", declaredContexts)}."));
        }
    }

    /// <summary>
    /// Two facts holding one context is a context nobody can let go of correctly.
    /// </summary>
    /// <remarks>
    /// Each fact hands its context back with <c>--auto</c> when it goes false, which
    /// drops the pin the other fact set: with <c>camera { in-use "busy" }</c> and
    /// <c>microphone { in-use "busy" }</c>, closing the camera ends <c>busy</c> while the
    /// microphone is still open. The file wants one fact, or two contexts and a third
    /// composed from them in the <c>contexts</c> section.
    /// </remarks>
    private static void WarnAboutSharedContexts(AynConfig config, Naming names, List<Diagnostic> diagnostics)
    {
        // Every name in force, written or defaulted, so a written name that collides
        // with a default is caught too.
        List<(string Context, string Where)> inForce = [];

        foreach (Fact fact in FactNames.All)
            if (config.ContextFor(fact) is { } context) inForce.Add((context, fact.Wire()));

        foreach (AppRule rule in config.AppRules)
            inForce.Add((rule.Context, $"{rule.Device.Word()} by \"{rule.App}\""));

        foreach (IGrouping<string, (string Context, string Where)> shared in inForce
                     .GroupBy(entry => entry.Context, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            // Reported at the last place the file wrote the name, or at nothing when
            // only defaults collide, which cannot happen with the defaults as they are.
            TextSpan span = names.Written.LastOrDefault(w => string.Equals(w.Context, shared.Key, StringComparison.OrdinalIgnoreCase)).Span;

            diagnostics.Add(Diagnostic.Warning(
                "AYN0006",
                $"Context '{shared.Key}' is held by more than one fact ({string.Join(", ", shared.Select(s => s.Where))}); " +
                "whichever goes false first will let go of it for the others.",
                span,
                "Give each fact its own context, and compose them in the contexts section."));
        }
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

    /// <summary>
    /// <c>renew</c>, in seconds: how often a held context is asserted again. Zero or
    /// <c>#false</c> is off, which is also the default.
    /// </summary>
    private static TimeSpan? Renew(KdlNode node, List<Diagnostic> diagnostics)
    {
        if (Setting(node, "renew") is not { } value) return null;

        if (value.TryAsBool(out bool on)) return on ? TimeSpan.FromSeconds(60) : null;

        if (!value.TryAsInt(out int seconds))
        {
            diagnostics.Add(Diagnostic.Warning(
                "AYN0009",
                $"'renew' should be a whole number of seconds, not '{value.AsString()}'; renewal is off.",
                value.Span,
                "Write renew 60 to assert every held context again each minute, with a two-minute time to live."));

            return null;
        }

        if (seconds <= 0) return null;

        if (seconds < MinRenewSeconds)
        {
            diagnostics.Add(Diagnostic.Warning(
                "AYN0009",
                $"'renew' is {seconds}; it is kept at {MinRenewSeconds} or more, so {MinRenewSeconds} is used.",
                value.Span,
                "A renewal is a command to the window manager, and a few seconds between them is as often as is useful."));

            seconds = MinRenewSeconds;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static int Percent(KdlNode node, string key, int fallback, List<Diagnostic> diagnostics)
    {
        if (Setting(node, key) is not { } value) return fallback;

        if (!value.TryAsInt(out int percent) || percent < 1 || percent > 100)
        {
            diagnostics.Add(Diagnostic.Warning(
                "AYN0010",
                $"'{key}' should be a percentage from 1 to 100, not '{value.AsString()}'; {fallback} is used.",
                value.Span));

            return fallback;
        }

        return percent;
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
