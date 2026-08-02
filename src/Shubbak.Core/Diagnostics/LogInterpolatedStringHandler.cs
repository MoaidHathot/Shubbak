using System.Runtime.CompilerServices;

namespace Shubbak.Core.Diagnostics;

/// <summary>
/// Builds a log message only when the level is enabled.
/// </summary>
/// <remarks>
/// <para>
/// <c>Log.Debug(category, $"{key} -> {command}")</c> looks free when debug logging is
/// off. It is not: the interpolated string is built by the caller, before the call, so
/// the formatting and the allocation happen whether or not anything will read the
/// result. On the window manager's tick that meant a string per keystroke and a string
/// per window event, permanently, for nothing.
/// </para>
/// <para>
/// That matters more than the wasted work. Allocation on the message loop means
/// garbage collections on the message loop, and a collection suspends every thread in
/// the process - including the one servicing the keyboard hook, which is holding a
/// keystroke the user is waiting on.
/// </para>
/// <para>
/// With this the compiler skips every append until the level has been checked.
/// Guarding by hand with <see cref="Log.IsEnabled"/> does the same and is easy to
/// forget, which is the point: this cannot be.
/// </para>
/// <para>
/// One type per level rather than one taking a level, because the handler's
/// constructor has to decide before any argument is evaluated.
/// </para>
/// </remarks>
[InterpolatedStringHandler]
public ref struct TraceLogHandler
{
    private DefaultInterpolatedStringHandler _inner;

    public TraceLogHandler(int literalLength, int formattedCount, out bool enabled)
    {
        enabled = Log.IsEnabled(LogLevel.Trace);
        IsEnabled = enabled;

        _inner = enabled
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    /// <summary>Whether anything was built.</summary>
    public bool IsEnabled { get; }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);

    public void AppendFormatted<T>(T value, int alignment, string? format) =>
        _inner.AppendFormatted(value, alignment, format);

    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

    /// <summary>The finished message. Only meaningful when <see cref="IsEnabled"/>.</summary>
    public string ToStringAndClear() => IsEnabled ? _inner.ToStringAndClear() : string.Empty;
}

/// <summary>
/// Builds a debug message only when debug logging is enabled.
/// </summary>
/// <remarks>See <see cref="TraceLogHandler"/> for why this exists.</remarks>
[InterpolatedStringHandler]
public ref struct DebugLogHandler
{
    private DefaultInterpolatedStringHandler _inner;

    public DebugLogHandler(int literalLength, int formattedCount, out bool enabled)
    {
        enabled = Log.IsEnabled(LogLevel.Debug);
        IsEnabled = enabled;

        _inner = enabled
            ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
            : default;
    }

    /// <summary>Whether anything was built.</summary>
    public bool IsEnabled { get; }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);

    public void AppendFormatted<T>(T value, int alignment) => _inner.AppendFormatted(value, alignment);

    public void AppendFormatted<T>(T value, int alignment, string? format) =>
        _inner.AppendFormatted(value, alignment, format);

    public void AppendFormatted(string? value) => _inner.AppendFormatted(value);

    /// <summary>The finished message. Only meaningful when <see cref="IsEnabled"/>.</summary>
    public string ToStringAndClear() => IsEnabled ? _inner.ToStringAndClear() : string.Empty;
}
