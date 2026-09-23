using Shubbak.Ui.Layout;

namespace Taj.Core.Widgets;

/// <summary>
/// What the pointer does to a widget: a command per gesture, or nothing.
/// </summary>
/// <param name="Click">The left button.</param>
/// <param name="RightClick">The right button.</param>
/// <param name="MiddleClick">The middle button, or the wheel pressed.</param>
/// <param name="ScrollUp">The wheel turned away from the user.</param>
/// <param name="ScrollDown">The wheel turned towards the user.</param>
/// <remarks>
/// Commands rather than callbacks, so a gesture goes through exactly the same path
/// as a keybinding and the two cannot behave differently. A widget with any of these
/// is a control and gets the hand cursor; one with none is a readout.
/// </remarks>
public sealed record PointerActions(
    string? Click = null,
    string? RightClick = null,
    string? MiddleClick = null,
    string? ScrollUp = null,
    string? ScrollDown = null)
{
    /// <summary>A readout: nothing happens.</summary>
    public static PointerActions None { get; } = new();

    /// <summary>Whether any gesture does anything.</summary>
    public bool Any =>
        Click is { Length: > 0 } || RightClick is { Length: > 0 } || MiddleClick is { Length: > 0 } ||
        ScrollUp is { Length: > 0 } || ScrollDown is { Length: > 0 };

    /// <summary>Every command named, for validation.</summary>
    public IEnumerable<(string Key, string Command)> Commands()
    {
        if (Click is { Length: > 0 }) yield return ("on-click", Click);
        if (RightClick is { Length: > 0 }) yield return ("on-right-click", RightClick);
        if (MiddleClick is { Length: > 0 }) yield return ("on-middle-click", MiddleClick);
        if (ScrollUp is { Length: > 0 }) yield return ("on-scroll-up", ScrollUp);
        if (ScrollDown is { Length: > 0 }) yield return ("on-scroll-down", ScrollDown);
    }

    /// <summary>Writes the actions onto a node.</summary>
    public void ApplyTo(VisualNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        node.OnClick = Click;
        node.OnRightClick = RightClick;
        node.OnMiddleClick = MiddleClick;
        node.OnScrollUp = ScrollUp;
        node.OnScrollDown = ScrollDown;
    }

    /// <summary>The settings a widget may carry, for the unknown-setting warning.</summary>
    public static IReadOnlyList<string> Keys { get; } =
        ["on-click", "on-right-click", "on-middle-click", "on-scroll-up", "on-scroll-down"];
}
