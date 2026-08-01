namespace Shubbak.Core.Diagnostics;

/// <summary>How much detail to record.</summary>
public enum LogLevel
{
    /// <summary>
    /// Every window event and every frame. Extremely verbose - a busy desktop
    /// produces well over a hundred entries a second - but it is what makes a
    /// misbehaviour reproducible from a log alone.
    /// </summary>
    Trace = 0,

    /// <summary>Commands, layout passes, rule matches, IPC requests.</summary>
    Debug = 1,

    /// <summary>Lifecycle: startup, config reload, monitors, windows managed.</summary>
    Information = 2,

    /// <summary>Something looks wrong but the window manager carried on.</summary>
    Warning = 3,

    /// <summary>Something failed.</summary>
    Error = 4,

    /// <summary>Nothing at all.</summary>
    None = 5,
}

/// <summary>
/// Which subsystem an entry came from.
/// </summary>
/// <remarks>
/// Categories exist so a log can be filtered down to the thing being investigated.
/// A window that will not tile is a <see cref="Window"/> and <see cref="Rule"/>
/// question; stuttering motion is <see cref="Animation"/> and <see cref="Layout"/>;
/// a dead keybinding is <see cref="Hook"/> and <see cref="Command"/>. Reading the
/// unfiltered stream at Trace level is close to impossible.
/// </remarks>
public enum LogCategory
{
    /// <summary>Startup, shutdown, general lifecycle.</summary>
    Wm,

    /// <summary>Window events from the operating system.</summary>
    Window,

    /// <summary>Keyboard hook and binding lookup.</summary>
    Hook,

    /// <summary>Command parsing and execution.</summary>
    Command,

    /// <summary>Layout passes and window placement.</summary>
    Layout,

    /// <summary>Animation tracks and frames.</summary>
    Animation,

    /// <summary>Config loading and reloading.</summary>
    Config,

    /// <summary>Window rule evaluation.</summary>
    Rule,

    /// <summary>IPC clients and requests.</summary>
    Ipc,

    /// <summary>Monitors and DPI.</summary>
    Monitor,
}

/// <summary>One log entry.</summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Level">How serious.</param>
/// <param name="Category">Which subsystem.</param>
/// <param name="Message">What happened.</param>
public readonly record struct LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    LogCategory Category,
    string Message)
{
    public string Format() =>
        $"{Timestamp:HH:mm:ss.fff} {Abbreviate(Level)} {Category,-9} {Message}";

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "---",
    };
}
