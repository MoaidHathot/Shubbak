using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using System.Text.Json;

namespace Taj.Core;

/// <summary>
/// What the bar shows about the focused window.
/// </summary>
/// <param name="Title">The window title.</param>
/// <param name="Process">The owning process name.</param>
/// <param name="State">
/// The window's state, lower-cased: <c>tiling</c>, <c>floating</c>,
/// <c>fullscreen</c>, <c>monitorfullscreen</c>, <c>maximised</c> or
/// <c>minimised</c>. Published so a widget can style itself by it.
/// </param>
public readonly record struct FocusedWindowValues(string Title, string Process, string State)
{
    /// <summary>Nothing focused: every value blank.</summary>
    public static FocusedWindowValues None => new(string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// Turns a window event payload into the values the bar displays.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the bar host so it can be tested. The host has no test project,
/// and the rule below is not obvious enough to leave uncovered.
/// </para>
/// </remarks>
public static class FocusedWindow
{
    /// <summary>Source name carrying the focused window's title.</summary>
    public const string TitleKey = "window.title";

    /// <summary>Source name carrying the focused window's process.</summary>
    public const string ProcessKey = "window.process";

    /// <summary>Source name carrying the focused window's state.</summary>
    public const string StateKey = "window.state";

    /// <summary>
    /// The values a payload implies, or null when the bar should not change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three outcomes, and the middle one is the point. A payload of <c>null</c>
    /// means focus went away and the bar should empty. A payload describing a window
    /// that is not the focused one means some other window changed and the bar must
    /// be left alone. Anything else replaces what is shown.
    /// </para>
    /// <para>
    /// That middle case is not hypothetical. <c>window.title_changed</c> and
    /// <c>window.state_changed</c> both fire for any window at all, and the payload
    /// is the window that changed rather than the focused one - so a background tab
    /// retitling itself would put its title in the bar. Applications that update
    /// their title on a timer, which is most chat and browser windows, made the bar
    /// flicker between whatever had most recently changed.
    /// </para>
    /// </remarks>
    public static FocusedWindowValues? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "null") return FocusedWindowValues.None;

        try
        {
            WindowInfo? window = JsonSerializer.Deserialize(json, IpcJsonContext.Default.WindowInfo);

            if (window is null || !window.Focused) return null;

            return new FocusedWindowValues(window.Title, window.ProcessName, window.State);
        }
        catch (JsonException ex)
        {
            Log.Warn(LogCategory.Ipc, $"malformed window payload: {ex.Message}");
            return null;
        }
    }
}
