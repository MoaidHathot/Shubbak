using Shubbak.Core.Diagnostics;
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

    /// <summary>
    /// The signal sources by the signal each listens for, so a signal arriving is one
    /// lookup rather than a walk. Rebuilt with the set; several sources may listen for
    /// one signal under different names.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, as <c>SignalPayload.IsFor</c> is: a signal is a word typed
    /// into a keybinding or a script, and <c>Battery</c> and <c>battery</c> are the
    /// same word to the person who typed them.
    /// </remarks>
    private readonly Dictionary<string, SignalSource[]> _bySignal = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any source is fed by a signal; see <see cref="ListensForSignals"/>.</summary>
    private volatile bool _listensForSignals;

    /// <summary>The sources by name.</summary>
    public IReadOnlyCollection<string> Names
    {
        get { lock (_gate) return [.. _sources.Keys]; }
    }

    /// <summary>
    /// Whether any source in the set is a <see cref="SignalSource"/>, which is what
    /// decides whether the bar subscribes to the <c>signal</c> topic at all.
    /// </summary>
    /// <remarks>
    /// Asked without the lock, from the connection's thread as well as the loop's. A
    /// bar that has no signal source must not subscribe: every subscriber costs the
    /// window manager two calls per signal on its own thread, and takes from it the
    /// one line that says a signal was raised with nobody listening - which is how a
    /// palette key that does nothing is diagnosed.
    /// </remarks>
    public bool ListensForSignals => _listensForSignals;

    /// <summary>
    /// A signal arrived; every source listening for it takes the arguments as its
    /// value. Nothing happens for a name no source listens for.
    /// </summary>
    /// <remarks>
    /// Called once per bar per signal, since each bar's connection hears it; the
    /// sources publish through <see cref="SourceBase.Publish"/>, which drops a value
    /// equal to the last, so the second and third arrivals of one signal cost a lookup
    /// and a string comparison and wake nothing.
    /// </remarks>
    public void Signal(string name, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(arguments);

        SignalSource[]? listening;
        lock (_gate) _bySignal.TryGetValue(name, out listening);

        if (listening is null) return;

        Log.Debug(LogCategory.Wm, $"signal \"{name}\" -> {listening.Length} source(s)");

        foreach (SignalSource source in listening) source.Receive(arguments);
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

            IndexSignals();
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

    /// <summary>Rebuilds <see cref="_bySignal"/> from <see cref="_sources"/>. Under the lock.</summary>
    private void IndexSignals()
    {
        _bySignal.Clear();

        Dictionary<string, List<SignalSource>>? grouped = null;

        foreach (ISource source in _sources.Values)
        {
            if (source is not SignalSource signal) continue;

            grouped ??= new Dictionary<string, List<SignalSource>>(StringComparer.OrdinalIgnoreCase);

            if (!grouped.TryGetValue(signal.Signal, out List<SignalSource>? list))
                grouped[signal.Signal] = list = [];

            list.Add(signal);
        }

        if (grouped is not null)
        {
            foreach ((string name, List<SignalSource> list) in grouped)
                _bySignal[name] = [.. list];
        }

        _listensForSignals = _bySignal.Count > 0;
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
            _bySignal.Clear();
            _listensForSignals = false;
        }

        foreach (ISource source in sources)
        {
            source.Changed -= OnSourceChanged;
            source.Dispose();
        }
    }
}
