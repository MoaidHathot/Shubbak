using Shubbak.Core.Diagnostics;
using Shubbak.Core.Geometry;
using Shubbak.Core.Rendering;
using Shubbak.Ui.Layout;
using Taj.Core.Sources;
using Taj.Core.Widgets;

namespace Taj.Core;

/// <summary>Where a bar sits on its monitor.</summary>
public enum BarEdge
{
    Top,
    Bottom,
}

/// <summary>One bar's appearance and contents.</summary>
/// <param name="Name">Profile name, referenced by bar rules.</param>
/// <param name="Edge">Which edge of the monitor.</param>
/// <param name="Height">Height in device-independent pixels.</param>
/// <param name="Background">Bar background colour.</param>
/// <param name="Padding">Space inside the bar.</param>
/// <param name="Zones">
/// Top-level regions, laid out left to right. Three is only a convention; a profile
/// may declare as many as it likes, each with its own alignment and growth.
/// </param>
public sealed record BarProfile(
    string Name,
    BarEdge Edge,
    int Height,
    Colour Background,
    Edges Padding,
    IReadOnlyList<BarZone> Zones);

/// <summary>A region of a bar.</summary>
/// <param name="Id">Identifier, for styling and diagnostics.</param>
/// <param name="Justify">How its widgets are distributed.</param>
/// <param name="Grow">Share of leftover width.</param>
/// <param name="Gap">Space between widgets.</param>
/// <param name="Widgets">What it contains.</param>
public sealed record BarZone(
    string Id,
    JustifyContent Justify,
    double Grow,
    int Gap,
    IReadOnlyList<IWidget> Widgets);

/// <summary>
/// Builds a bar's visual tree and keeps it in step with its sources.
/// </summary>
/// <remarks>
/// <para>
/// The layer between the reactive sources and the renderer. It owns the current
/// values, rebuilds the tree when a value a widget depends on changes, and reports
/// that a redraw is needed. It knows nothing about drawing.
/// </para>
/// <para>
/// Rebuilding the whole tree on a change is deliberate. The tree is a few dozen
/// small objects; building it is far cheaper than the diffing machinery needed to
/// avoid building it, and it makes every render a pure function of the current
/// values - which is what makes the output testable.
/// </para>
/// </remarks>
public sealed class BarModel : IDisposable
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ISource> _sources = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private BarProfile _profile;
    private bool _dirty = true;
    private bool _disposed;

    public BarModel(BarProfile profile) =>
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    /// <summary>The profile currently in effect.</summary>
    public BarProfile Profile
    {
        get => _profile;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            lock (_gate)
            {
                if (ReferenceEquals(_profile, value)) return;
                _profile = value;
                _dirty = true;
            }
        }
    }

    /// <summary>True when the tree needs rebuilding and redrawing.</summary>
    public bool IsDirty
    {
        get { lock (_gate) return _dirty; }
    }

    /// <summary>Registers a source and begins listening to it.</summary>
    public void AddSource(ISource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_gate)
        {
            if (!_sources.TryAdd(source.Name, source)) return;
        }

        source.Changed += OnSourceChanged;
        source.Start();

        OnSourceChanged(source);
    }

    /// <summary>
    /// Swaps the whole set of sources for a new one.
    /// </summary>
    /// <remarks>
    /// The old sources are disposed rather than dropped: each one owns a timer, so
    /// leaving them running would add a clock on every reload and the bar would slowly
    /// fill with ticking it no longer reads. Values are kept until the replacements
    /// publish, so the bar does not blank while the new sources take their first
    /// reading.
    /// </remarks>
    public void ReplaceSources(IEnumerable<ISource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        ISource[] previous;

        lock (_gate)
        {
            previous = [.. _sources.Values];
            _sources.Clear();
        }

        foreach (ISource source in previous)
        {
            source.Changed -= OnSourceChanged;
            source.Dispose();
        }

        foreach (ISource source in sources) AddSource(source);
    }

    /// <summary>Sets a value directly, for push sources driven by the host.</summary>
    public void SetValue(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        lock (_gate)
        {
            if (_values.TryGetValue(name, out string? existing) &&
                string.Equals(existing, value, StringComparison.Ordinal))
            {
                return;
            }

            _values[name] = value;
            _dirty = true;
        }
    }

    /// <summary>The current value of a source.</summary>
    public string? GetValue(string name)
    {
        lock (_gate) return _values.GetValueOrDefault(name);
    }

    private void OnSourceChanged(ISource source)
    {
        lock (_gate)
        {
            _values[source.Name] = source.Value;

            // Rebuild only if some widget actually depends on this source. A source
            // nothing displays - left over after a profile switch, say - must not
            // cost a redraw.
            _dirty = true;
        }
    }

    /// <summary>
    /// Builds the visual tree for the current values.
    /// </summary>
    /// <remarks>
    /// Clears the dirty flag. Pure with respect to the values, so the same values
    /// always produce the same tree.
    /// </remarks>
    public VisualNode Build()
    {
        Dictionary<string, string?> snapshot;
        BarProfile profile;

        lock (_gate)
        {
            snapshot = new Dictionary<string, string?>(_values, StringComparer.Ordinal);
            profile = _profile;
            _dirty = false;
        }

        var root = new VisualNode
        {
            Id = "bar",
            Kind = VisualKind.Container,
            Direction = FlexDirection.Row,
            Align = AlignItems.Stretch,
            Box = new BoxStyle(Padding: profile.Padding),
            Style = VisualStyle.Default with { Background = profile.Background },
        };

        foreach (BarZone zone in profile.Zones)
        {
            var container = new VisualNode
            {
                Id = zone.Id,
                Kind = VisualKind.Container,
                Direction = FlexDirection.Row,
                Justify = zone.Justify,
                Align = AlignItems.Stretch,
                Gap = zone.Gap,
                Box = new BoxStyle(Grow: zone.Grow),
            };

            foreach (IWidget widget in zone.Widgets)
            {
                try
                {
                    container.Add(widget.Build(snapshot));
                }
                catch (Exception ex)
                {
                    // One broken widget must not blank the whole bar.
                    Log.Error(LogCategory.Wm, $"widget '{widget.Id}' failed to build", ex);
                }
            }

            root.Add(container);
        }

        return root;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ISource[] sources;
        lock (_gate)
        {
            sources = [.. _sources.Values];
            _sources.Clear();
        }

        foreach (ISource source in sources)
        {
            source.Changed -= OnSourceChanged;
            source.Dispose();
        }
    }
}

/// <summary>
/// Chooses which profile a bar should use.
/// </summary>
/// <param name="Profile">Profile name to apply.</param>
/// <param name="Workspace">Match when this workspace is active, or null for any.</param>
/// <param name="MonitorIndex">Match on this monitor, or null for any.</param>
public sealed record BarRule(string Profile, string? Workspace = null, int? MonitorIndex = null)
{
    /// <summary>Whether this rule applies.</summary>
    public bool Matches(string activeWorkspace, int monitorIndex)
    {
        if (Workspace is not null &&
            !string.Equals(Workspace, activeWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MonitorIndex is null || MonitorIndex == monitorIndex;
    }
}

/// <summary>
/// Picks a profile from the rules.
/// </summary>
/// <remarks>
/// All profiles are built up front and held in memory, so switching is a pointer
/// swap rather than a process restart. Bars are small; the alternative would make
/// every workspace switch visibly slow.
/// </remarks>
public sealed class BarProfileSelector
{
    private readonly IReadOnlyList<BarRule> _rules;
    private readonly IReadOnlyDictionary<string, BarProfile> _profiles;
    private readonly BarProfile _fallback;

    public BarProfileSelector(
        IReadOnlyDictionary<string, BarProfile> profiles,
        IReadOnlyList<BarRule> rules,
        BarProfile fallback)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <summary>The profile to use, first matching rule wins.</summary>
    public BarProfile Select(string activeWorkspace, int monitorIndex)
    {
        foreach (BarRule rule in _rules)
        {
            if (!rule.Matches(activeWorkspace, monitorIndex)) continue;
            if (_profiles.TryGetValue(rule.Profile, out BarProfile? profile)) return profile;
        }

        return _fallback;
    }
}
