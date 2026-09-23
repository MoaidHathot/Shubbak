using Taj.Core.Sources;

namespace Taj.Core;

/// <summary>
/// One set of sources, feeding every bar.
/// </summary>
/// <remarks>
/// <para>
/// A source is about the machine, not about a display: the clock, the keyboard
/// layout, a script's output are the same on every monitor. Each bar used to own a
/// set of its own, so a two-monitor desktop ran two clocks, two keyboard polls and
/// two copies of every <c>kind="command"</c> script - and a reload, which replaces
/// every source, killed and restarted each script once per display, one after the
/// other, on the thread that draws the bars.
/// </para>
/// <para>
/// The hub owns the sources and fans each value out to every model attached to it.
/// A model attached later is brought up to date at once, so a bar created for a
/// display plugged in after startup shows the clock rather than a blank until it next
/// ticks. Standing down stops the sources once, for all bars, since a source with no
/// bar on screen has nobody to publish to.
/// </para>
/// <para>
/// The fan-out runs on whatever thread the source published from - a timer's, a
/// reader's - which is the thread the models already accepted values from when they
/// owned the sources themselves; <see cref="BarModel.SetValue"/> is safe from any.
/// </para>
/// </remarks>
public sealed class SourceHub : IDisposable
{
    private readonly Dictionary<string, ISource> _sources = new(StringComparer.Ordinal);
    private readonly List<BarModel> _models = [];
    private readonly Lock _gate = new();
    private bool _stoodDown;
    private bool _disposed;

    /// <summary>The sources by name.</summary>
    public IReadOnlyCollection<string> Names
    {
        get { lock (_gate) return [.. _sources.Keys]; }
    }

    /// <summary>Starts feeding a model, and gives it every value the sources have so far.</summary>
    public void Attach(BarModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        ISource[] sources;

        lock (_gate)
        {
            if (_models.Contains(model)) return;
            _models.Add(model);
            sources = [.. _sources.Values];
        }

        foreach (ISource source in sources) model.SetValue(source.Name, source.Value);
    }

    /// <summary>Stops feeding a model. Its values are left as they are.</summary>
    public void Detach(BarModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_gate) _models.Remove(model);
    }

    /// <summary>
    /// Swaps the whole set of sources for a new one.
    /// </summary>
    /// <remarks>
    /// The old sources are disposed rather than dropped: each one owns a timer or a
    /// process, so leaving them running would add a clock on every reload. Values are
    /// kept in the models until the replacements publish, so no bar blanks while the
    /// new sources take their first reading. A source that was stood down when the
    /// swap happened is created stood down too.
    /// </remarks>
    public void Replace(IEnumerable<ISource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        ISource[] previous;
        List<ISource> added = [];
        List<ISource> shadowed = [];
        bool stoodDown;

        lock (_gate)
        {
            previous = [.. _sources.Values];
            _sources.Clear();

            // The first declaration of a name wins, as it did when each model kept
            // its own. The loser is never started, but it was constructed - a process
            // source owns a cancellation source from birth - so it is disposed rather
            // than dropped.
            foreach (ISource source in sources)
            {
                if (_sources.TryAdd(source.Name, source)) added.Add(source);
                else shadowed.Add(source);
            }

            stoodDown = _stoodDown;
        }

        foreach (ISource source in previous)
        {
            source.Changed -= OnSourceChanged;
            source.Dispose();
        }

        foreach (ISource source in shadowed) source.Dispose();

        foreach (ISource source in added)
        {
            source.Changed += OnSourceChanged;
            source.Start();

            if (stoodDown) source.StandDown();

            OnSourceChanged(source);
        }
    }

    /// <summary>Stops every source that polls, because no bar is showing them. Idempotent.</summary>
    public void StandDown()
    {
        ISource[] sources;

        lock (_gate)
        {
            _stoodDown = true;
            sources = [.. _sources.Values];
        }

        foreach (ISource source in sources) source.StandDown();
    }

    /// <summary>Starts every source polling again, each taking a reading at once. Idempotent.</summary>
    public void StandUp()
    {
        ISource[] sources;

        lock (_gate)
        {
            _stoodDown = false;
            sources = [.. _sources.Values];
        }

        foreach (ISource source in sources) source.StandUp();
    }

    private void OnSourceChanged(ISource source)
    {
        BarModel[] models;
        lock (_gate) models = [.. _models];

        string? value = source.Value;

        foreach (BarModel model in models) model.SetValue(source.Name, value);
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
            _models.Clear();
        }

        foreach (ISource source in sources)
        {
            source.Changed -= OnSourceChanged;
            source.Dispose();
        }
    }
}
