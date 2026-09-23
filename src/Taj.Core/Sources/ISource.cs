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
    private readonly Lock _publishGate = new();
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
        // Compared and set under a lock: two timer callbacks overlapping - a slow
        // producer on a short interval - could otherwise both see the old value and
        // both publish, or lose an edge between them. Raised outside it, since a
        // subscriber may take locks of its own.
        lock (_publishGate)
        {
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value;
        }

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
    private readonly System.Globalization.CultureInfo _culture;

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
    /// <param name="culture">
    /// A BCP 47 name such as <c>de-DE</c> or <c>ar-SA</c> that decides how day and
    /// month names come out, or null for the invariant culture - which is English,
    /// and was the only choice: a bar in Berlin said "Wednesday" whatever its owner
    /// spoke, and a format with <c>dddd</c> in it had no way to say otherwise.
    /// </param>
    public ClockSource(
        string name, string format, TimeSpan interval, string? timeZoneId = null, string? culture = null)
        : base(name)
    {
        _format = string.IsNullOrWhiteSpace(format) ? "HH:mm" : format;
        _interval = interval < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : interval;
        _timeZone = ResolveTimeZone(timeZoneId, name);
        _culture = ResolveCulture(culture, name);
    }

    /// <summary>The culture the clock speaks; invariant unless one was named and known.</summary>
    public System.Globalization.CultureInfo Culture => _culture;

    /// <summary>
    /// Whether the name is a culture this machine knows, for the loader to say so
    /// where the file can be pointed at rather than in the log after the fact.
    /// </summary>
    /// <remarks>
    /// A process built without globalization data - the command-line tool is one, so
    /// <c>check-config</c> runs as one - knows no culture but the invariant, and would
    /// call every real name unknown. There the answer is whether the name is shaped
    /// like a BCP 47 tag, which still catches <c>culture="German"</c>; the bar itself
    /// has the data and says the rest in its log.
    /// </remarks>
    public static bool IsKnownCulture(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant) && invariant)
            return IsWellFormedCultureName(name);

        try
        {
            _ = System.Globalization.CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return true;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Two to eight letters, then dash-separated alphanumeric subtags: <c>de</c>, <c>en-GB</c>, <c>zh-Hant-TW</c>.</summary>
    private static bool IsWellFormedCultureName(string name)
    {
        string[] parts = name.Split('-');

        if (parts[0].Length is < 2 or > 8 || !parts[0].All(char.IsAsciiLetter)) return false;

        return parts.Skip(1).All(part => part.Length is >= 1 and <= 8 && part.All(char.IsAsciiLetterOrDigit));
    }

    private static System.Globalization.CultureInfo ResolveCulture(string? name, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(name)) return System.Globalization.CultureInfo.InvariantCulture;

        try
        {
            // Predefined only. Without it .NET manufactures a culture for any
            // well-formed name, so `culture="klingon-KL"` produced English with no
            // word about why.
            return System.Globalization.CultureInfo.GetCultureInfo(name, predefinedOnly: true);
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            Log.Warn(LogCategory.Config,
                $"clock '{sourceName}': unknown culture '{name}'; using the invariant culture");

            return System.Globalization.CultureInfo.InvariantCulture;
        }
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

            Publish(now.ToString(_format, _culture));
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
/// <para>
/// And it is stopped when the source is disposed. The program is the bar's own
/// worker, not a peer like the palette or the watcher: nothing but this source knows
/// it is there, so nothing else can stop it, and a source that only let go of its
/// handle left the script running - once per configuration reload, since a reload
/// replaces every source, so a bar reloaded five times had six copies of each. The
/// whole tree goes, because the script is nearly always <c>pwsh -File x.ps1</c> or
/// <c>cmd /c ...</c> and on Windows ending a parent leaves its children running. Best
/// effort: a bar that is killed rather than closed cannot do this, and does not try to
/// own the process through a job object - the cost is one stray script after a crash,
/// which is the same bargain the window manager makes with its startup commands.
/// </para>
/// </remarks>
public sealed class ProcessSource : SourceBase
{
    private readonly string _fileName;
    private readonly string _arguments;
    private readonly TimeSpan _restartDelay;
    private readonly TimeSpan? _interval;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();

    /// <summary>Released by <see cref="StandUp"/>, for a poll waiting out a stand-down.</summary>
    private readonly SemaphoreSlim _standUp = new(0);
    private volatile bool _stoodDown;

    /// <summary>
    /// How long to wait before the next start after an exit nobody asked for, doubling
    /// each time to a minute and reset by a run that produced something.
    /// </summary>
    /// <remarks>
    /// A program that exits at once - a mistyped path, a script with a syntax error -
    /// used to be started every five seconds for ever, with a warning each time:
    /// seventeen thousand lines a day in the log for one typo. The first exit is said;
    /// after that only a delay that has grown is, so the log records the shape of the
    /// failure rather than every instance of it.
    /// </remarks>
    private TimeSpan _backoff;

    private Task? _reader;

    /// <summary>The program that is running now, or null between runs.</summary>
    /// <remarks>
    /// <para>
    /// Read and written under <see cref="_gate"/>, and started under it too, so that a
    /// program cannot start in the gap between <see cref="Dispose(bool)"/> looking for
    /// one and the reader noticing it has been cancelled - and so that the reader does
    /// not dispose the <see cref="System.Diagnostics.Process"/> while dispose is in the
    /// middle of stopping it.
    /// </para>
    /// <para>
    /// Whoever takes it out of this field is the one who stops it, if it needs
    /// stopping. Cancellation wakes the reader, so it is a race between the reader's
    /// finally and dispose, and either may win; the loser finds the field empty and
    /// does nothing. One of them, never both: stopping a tree walks the process
    /// table, which is tens of milliseconds on the bar's thread, and paying it twice
    /// per source per reload would show.
    /// </para>
    /// </remarks>
    private System.Diagnostics.Process? _process;

    /// <param name="name">The value's name.</param>
    /// <param name="commandLine">The program and its arguments.</param>
    /// <param name="restartDelay">How long to wait after a resident program exits before starting it again.</param>
    /// <param name="interval">
    /// When given, the program is expected to print and exit, and is run again this
    /// long after it exits - the i3blocks shape, where a script is a function of the
    /// moment it runs. Without it the program is expected to stay and keep printing.
    /// </param>
    public ProcessSource(string name, string commandLine, TimeSpan? restartDelay = null, TimeSpan? interval = null)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandLine);

        (_fileName, _arguments) = Split(commandLine);
        _restartDelay = restartDelay ?? TimeSpan.FromSeconds(5);
        _interval = interval is { } every && every > TimeSpan.Zero ? every : null;
        _backoff = _restartDelay;
    }

    /// <summary>Whether the program is run on a schedule rather than kept running.</summary>
    public bool Polls => _interval is not null;

    public override void Start() => _reader = Task.Run(RunAsync);

    /// <summary>
    /// Stops starting the program while nothing shows its value. A program already
    /// running is left to finish; a resident one is left alone, since it is the
    /// program's own schedule.
    /// </summary>
    public override void StandDown() => _stoodDown = true;

    /// <summary>Lets a waiting poll run at once, so the value is fresh when the bar returns.</summary>
    public override void StandUp()
    {
        if (!_stoodDown) return;

        _stoodDown = false;
        _standUp.Release();
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            bool produced = false;

            try
            {
                // A poll waits out a stand-down before it starts; there is nobody to
                // show the answer to. Woken by StandUp so the value is fresh at once.
                while (Polls && _stoodDown && !_shutdown.IsCancellationRequested)
                    await _standUp.WaitAsync(_shutdown.Token).ConfigureAwait(false);

                using var process = new System.Diagnostics.Process();

                process.StartInfo = new System.Diagnostics.ProcessStartInfo(_fileName, _arguments)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                lock (_gate)
                {
                    if (_shutdown.IsCancellationRequested) return;

                    process.Start();
                    _process = process;
                }

                try
                {
                    // ReadLineAsync returning null is the end-of-stream signal here;
                    // checking EndOfStream would block the async path.
                    while (!_shutdown.IsCancellationRequested)
                    {
                        string? line = await process.StandardOutput.ReadLineAsync(_shutdown.Token)
                            .ConfigureAwait(false);

                        if (line is null) break;

                        // A poll's value is the last line it prints, so a script that
                        // prints a heading and then the answer shows the answer.
                        if (line.Trim().Length > 0 || !Polls)
                        {
                            Publish(line.TrimEnd());
                            produced = true;
                        }
                    }
                }
                finally
                {
                    // Still here means dispose has not stopped it. Then either the
                    // program exited on its own - the ordinary way out of the loop, and
                    // there is nothing to stop - or cancellation ended the read (or a
                    // line arrived just after it) with the program alive, and this is
                    // the last moment anyone holds it.
                    lock (_gate)
                    {
                        if (_process is not null)
                        {
                            _process = null;
                            if (_shutdown.IsCancellationRequested) Terminate(process);
                        }
                    }
                }

                if (_shutdown.IsCancellationRequested) return;

                if (Polls)
                {
                    // Exiting is what a polled program does. Nothing to say, unless it
                    // said nothing: an empty run is worth one line, since the widget it
                    // feeds will be showing whatever the last run said.
                    if (!produced) Log.Warn(LogCategory.Wm, $"source '{Name}' printed nothing this run");
                }
                else
                {
                    Log.Warn(LogCategory.Wm, $"source '{Name}' exited; restarting in {NextBackoff(produced).TotalSeconds:F0}s");
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Once per failure that is new, or that has grown; see _backoff. A
                // program that cannot start fails identically every time.
                TimeSpan wait = NextBackoff(produced);

                if (wait == _restartDelay || wait != _lastReportedBackoff)
                {
                    _lastReportedBackoff = wait;
                    Log.Error(LogCategory.Wm, $"source '{Name}' failed; trying again in {wait.TotalSeconds:F0}s", ex);
                }

                Publish("!");
            }

            try
            {
                await Task.Delay(Polls ? _interval!.Value : _backoff, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan _lastReportedBackoff;

    /// <summary>The next wait after an exit nobody asked for, and the new backoff.</summary>
    private TimeSpan NextBackoff(bool produced)
    {
        _backoff = produced
            ? _restartDelay
            : TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));

        return _backoff;
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

    /// <summary>
    /// Stops a program this source started, and everything it started in turn.
    /// </summary>
    /// <remarks>
    /// Nothing here throws. Stopping is best effort by nature - the program may have
    /// exited a moment ago, which <c>Kill</c> treats as done, or be one this account
    /// may not end, which it reports - and the caller is either the reader on its way
    /// out or dispose on the bar's thread, and neither has anywhere for an exception to
    /// go but the log.
    /// </remarks>
    private void Terminate(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                    or System.ComponentModel.Win32Exception
                                    or AggregateException)
        {
            Log.Warn(LogCategory.Wm, $"source '{Name}': could not stop the program it started; it may still be running: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shutdown.Cancel();

            // Cancelling wakes the reader - the runtime cancels a pending pipe read,
            // measured at well under 100 ms - but wakes it into a loop that used to
            // simply return, leaving the program to run on. Stopping it is a separate
            // act, and it happens here or in the reader's finally, whichever gets to
            // the field first; the other finds it empty. Taken out of the field before
            // stopping, so the reader knows it has been done.
            lock (_gate)
            {
                if (_process is { } process)
                {
                    _process = null;
                    Terminate(process);
                }
            }

            try { _reader?.Wait(TimeSpan.FromSeconds(1)); }
            catch (AggregateException) { }

            _shutdown.Dispose();
            _standUp.Dispose();
        }

        base.Dispose(disposing);
    }
}
