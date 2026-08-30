using Shubbak.Core.Diagnostics;

namespace Taj.Core.Sources;

/// <summary>
/// A value the bar displays, which changes over time.
/// </summary>
/// <remarks>
/// <para>
/// The reactive layer. A source produces values; widgets bind to sources and
/// re-render only when the value they depend on actually changes. That last part
/// matters: a bar that redraws on a timer burns battery for nothing, and one that
/// redraws on every event flickers.
/// </para>
/// <para>
/// Sources are either <b>push</b> - window manager events, process output - or
/// <b>pull</b> - polled on an interval. Both look the same to a widget, which is
/// what makes them interchangeable in config.
/// </para>
/// </remarks>
public interface ISource : IDisposable
{
    /// <summary>Identifier used by widget templates.</summary>
    string Name { get; }

    /// <summary>The current value, or null when nothing has been produced yet.</summary>
    string? Value { get; }

    /// <summary>Raised when <see cref="Value"/> changes.</summary>
    event Action<ISource>? Changed;

    /// <summary>Begins producing values.</summary>
    void Start();

    /// <summary>
    /// Stops producing values, because nothing is on screen to show them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called when the bar stands down: the window manager has been suspended, or a
    /// full-screen application is covering the bar entirely. A clock polled twice a
    /// second behind a full-screen game is work nobody can see the result of.
    /// </para>
    /// <para>
    /// Only <b>pull</b> sources have anything to do here. A push source must keep
    /// running: the window manager's own state arrives that way, and it is what draws
    /// the indicator saying the bar is stood down and offering the way back.
    /// </para>
    /// <para>
    /// Given a default of doing nothing, so that adding this did not break every
    /// existing implementation and so that a source written without knowing about it
    /// goes on working. Standing down is an optimisation; a source that ignores it is
    /// wasteful, and one that failed to compile would be worse.
    /// </para>
    /// </remarks>
    void StandDown() { }

    /// <summary>
    /// Begins producing values again, starting immediately.
    /// </summary>
    /// <remarks>
    /// Immediately, not on the next interval, and that is the whole subtlety. A clock
    /// resumed on its ordinary schedule shows the time it stopped at until its
    /// interval next elapses, so a bar coming back from a long game would reappear
    /// showing an hour-old clock for half a second. Whether anyone would catch it is
    /// not the point; it would be wrong on screen.
    /// <para>
    /// Named as a pair with <see cref="StandDown"/> rather than suspend and resume,
    /// because <c>Resume</c> is a reserved word in other .NET languages and this is a
    /// public interface.
    /// </para>
    /// </remarks>
    void StandUp() { }
}

/// <summary>Shared plumbing for sources.</summary>
public abstract class SourceBase : ISource
{
    private string? _value;
    private bool _disposed;

    protected SourceBase(string name) => Name = name;

    public string Name { get; }

    public string? Value => _value;

    public event Action<ISource>? Changed;

    public abstract void Start();

    /// <summary>Does nothing. A source with no timer has nothing to stop.</summary>
    /// <remarks>
    /// The default rather than an abstract method on purpose: standing down is an
    /// optimisation, and a source that has not been taught about it must go on
    /// working rather than fail to compile or, worse, silently stop.
    /// </remarks>
    public virtual void StandDown() { }

    /// <inheritdoc cref="ISource.StandUp"/>
    public virtual void StandUp() { }

    /// <summary>
    /// Publishes a value, notifying subscribers only if it actually differs.
    /// </summary>
    /// <remarks>
    /// The equality check is the whole point. A clock source polled every 200 ms
    /// still only fires once a second when its format has second resolution, and the
    /// window-title source fires only on a genuine title change despite
    /// EVENT_OBJECT_NAMECHANGE arriving far more often (ADR 0001, S4).
    /// </remarks>
    protected void Publish(string? value)
    {
        if (string.Equals(_value, value, StringComparison.Ordinal)) return;

        _value = value;
        Changed?.Invoke(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A value recomputed on a fixed interval.
/// </summary>
/// <remarks>
/// The interval is how often the value is <i>checked</i>, not how often the bar
/// redraws - <see cref="SourceBase.Publish"/> suppresses unchanged values. A clock
/// showing seconds can therefore be polled at 200 ms for prompt ticks without
/// causing five redraws a second.
/// </remarks>
public sealed class IntervalSource : SourceBase
{
    private readonly Func<string> _produce;
    private readonly TimeSpan _interval;
    private Timer? _timer;

    public IntervalSource(string name, TimeSpan interval, Func<string> produce)
        : base(name)
    {
        _interval = interval < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : interval;
        _produce = produce ?? throw new ArgumentNullException(nameof(produce));
    }

    public override void Start()
    {
        Tick(null);
        _timer = new Timer(Tick, null, _interval, _interval);
    }

    /// <inheritdoc/>
    public override void StandDown() => _timer?.Change(Timeout.Infinite, Timeout.Infinite);

    /// <inheritdoc/>
    public override void StandUp() => _timer?.Change(TimeSpan.Zero, _interval);

    private void Tick(object? _)
    {
        try
        {
            Publish(_produce());
        }
        catch (Exception ex)
        {
            // A misbehaving source must not take the bar down with it.
            Log.Error(LogCategory.Wm, $"source '{Name}' failed", ex);
            Publish("!");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A clock, optionally in another timezone.
/// </summary>
/// <remarks>
/// <para>
/// Split out from the general interval source because a second clock showing a
/// colleague's or a datacentre's local time is one of the most common things anyone
/// puts on a bar, and expressing it as "run a script every second" would be a poor
/// answer to something so ordinary.
/// </para>
/// <para>
/// The interval is how often the value is <i>checked</i>, not how often the bar
/// redraws - <see cref="SourceBase.Publish"/> suppresses unchanged values, so a clock
/// showing minutes polled twice a second still causes one redraw a minute.
/// </para>
/// </remarks>
public sealed class ClockSource : SourceBase
{
    private readonly string _format;
    private readonly TimeZoneInfo? _timeZone;
    private readonly TimeSpan _interval;

    private Timer? _timer;

    /// <param name="name">Name templates refer to.</param>
    /// <param name="format">A .NET date and time format string.</param>
    /// <param name="interval">How often to re-evaluate.</param>
    /// <param name="timeZoneId">
    /// A Windows or IANA timezone identifier, or null for local time. Both are
    /// accepted because people copy identifiers from wherever they find them, and
    /// "America/Los_Angeles" failing on Windows while "Pacific Standard Time" works
    /// is an unhelpful distinction to impose.
    /// </param>
    public ClockSource(string name, string format, TimeSpan interval, string? timeZoneId = null)
        : base(name)
    {
        _format = string.IsNullOrWhiteSpace(format) ? "HH:mm" : format;
        _interval = interval < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : interval;
        _timeZone = ResolveTimeZone(timeZoneId, name);
    }

    public override void Start()
    {
        Tick(null);
        _timer = new Timer(Tick, null, _interval, _interval);
    }

    /// <inheritdoc/>
    public override void StandDown() => _timer?.Change(Timeout.Infinite, Timeout.Infinite);

    /// <inheritdoc/>
    /// <remarks>
    /// The zero due-time matters most here of anywhere: a clock is the one widget
    /// whose stale value is obviously wrong to look at.
    /// </remarks>
    public override void StandUp() => _timer?.Change(TimeSpan.Zero, _interval);

    private void Tick(object? _)
    {
        try
        {
            DateTimeOffset now = _timeZone is null
                ? DateTimeOffset.Now
                : TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone);

            Publish(now.ToString(_format, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (FormatException ex)
        {
            Log.Error(LogCategory.Config, $"clock '{Name}' has an invalid format '{_format}'", ex);
            Publish("!");
        }
    }

    private static TimeZoneInfo? ResolveTimeZone(string? id, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        // Falling back to local time keeps the widget showing something useful,
        // which beats a blank space the user has to investigate.
        Log.Warn(LogCategory.Config,
            $"clock '{sourceName}': unknown timezone '{id}'; using local time");

        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// A value set from outside, typically by the window manager event stream.
/// </summary>
public sealed class PushSource : SourceBase
{
    public PushSource(string name) : base(name) { }

    public override void Start() { }

    /// <summary>Sets the value.</summary>
    public void Set(string? value) => Publish(value);
}

/// <summary>
/// A value produced by an external program that writes lines to stdout.
/// </summary>
/// <remarks>
/// <para>
/// The extension point that means Taj never has to grow a widget for everything.
/// This is the i3blocks and waybar model: a script in any language prints a line,
/// the bar shows it. Adding a widget for a private API, a work tool or a hobby
/// project needs no Taj source code at all.
/// </para>
/// <para>
/// The process is restarted if it exits, because the common failure is a script with
/// a bug rather than a script that meant to stop, and a permanently blank widget
/// gives the user nothing to go on.
/// </para>
/// </remarks>
public sealed class ProcessSource : SourceBase
{
    private readonly string _fileName;
    private readonly string _arguments;
    private readonly TimeSpan _restartDelay;
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _reader;

    public ProcessSource(string name, string commandLine, TimeSpan? restartDelay = null)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandLine);

        (_fileName, _arguments) = Split(commandLine);
        _restartDelay = restartDelay ?? TimeSpan.FromSeconds(5);
    }

    public override void Start() => _reader = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var process = new System.Diagnostics.Process();

                process.StartInfo = new System.Diagnostics.ProcessStartInfo(_fileName, _arguments)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                process.Start();

                // ReadLineAsync returning null is the end-of-stream signal here;
                // checking EndOfStream would block the async path.
                while (!_shutdown.IsCancellationRequested)
                {
                    string? line = await process.StandardOutput.ReadLineAsync(_shutdown.Token)
                        .ConfigureAwait(false);

                    if (line is null) break;

                    Publish(line.TrimEnd());
                }

                if (_shutdown.IsCancellationRequested) return;

                Log.Warn(LogCategory.Wm, $"source '{Name}' exited; restarting in {_restartDelay.TotalSeconds:F0}s");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.Wm, $"source '{Name}' failed", ex);
                Publish("!");
            }

            try
            {
                await Task.Delay(_restartDelay, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static (string File, string Arguments) Split(string commandLine)
    {
        commandLine = commandLine.Trim();

        if (commandLine.StartsWith('"'))
        {
            int close = commandLine.IndexOf('"', 1);
            if (close > 0) return (commandLine[1..close], commandLine[(close + 1)..].Trim());
        }

        int space = commandLine.IndexOf(' ', StringComparison.Ordinal);

        return space < 0
            ? (commandLine, string.Empty)
            : (commandLine[..space], commandLine[(space + 1)..]);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shutdown.Cancel();

            try { _reader?.Wait(TimeSpan.FromSeconds(1)); }
            catch (AggregateException) { }

            _shutdown.Dispose();
        }

        base.Dispose(disposing);
    }
}
