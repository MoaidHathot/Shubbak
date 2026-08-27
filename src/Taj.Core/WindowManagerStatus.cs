namespace Taj.Core;

/// <summary>
/// What the bar says about the window manager's own state.
/// </summary>
/// <remarks>
/// <para>
/// Two states change what Shubbak does without changing anything on screen, and both
/// are invisible without something like this. Paused means windows are no longer being
/// arranged. Suspended means the keyboard hook has been released, which is what
/// somebody does before a game.
/// </para>
/// <para>
/// Suspended is the one that most needs saying. A suspended window manager is
/// indistinguishable from a crashed one by looking: the windows stay where they are,
/// and no key does anything. Somebody who suspended it an hour ago and forgot has no
/// way to tell the difference without trying a command and reasoning about the answer.
/// </para>
/// <para>
/// Pure and separate from the connection that feeds it, so the precedence can be held
/// to account without a window manager, a pipe or a bar.
/// </para>
/// </remarks>
public static class WindowManagerStatus
{
    /// <summary>Shown when the keyboard has been released.</summary>
    public const string Suspended = "suspended";

    /// <summary>Shown when windows are no longer being arranged.</summary>
    public const string Paused = "paused";

    /// <summary>
    /// The single label for a bar with room for one.
    /// </summary>
    /// <remarks>
    /// Suspended wins when both hold. A window manager that is not arranging windows
    /// is inconvenient; one that has let go of the keyboard is why none of your keys
    /// work, and that is the more urgent thing to be told.
    /// </remarks>
    /// <param name="suspended">Whether the keyboard hook has been released.</param>
    /// <param name="paused">Whether windows are no longer being arranged.</param>
    /// <returns>The label, or empty when there is nothing to say.</returns>
    public static string Combined(bool suspended, bool paused) =>
        suspended ? Suspended
            : paused ? Paused
            : string.Empty;

    /// <summary>The label for the suspended pill on its own.</summary>
    /// <remarks>
    /// Separate from <see cref="Combined"/> so a bar can show two pills and give each
    /// the click that undoes it - a pill saying "suspended" that resumes when clicked
    /// is a way back that does not need the keyboard, which is the one thing
    /// suspending took away.
    /// </remarks>
    public static string SuspendedLabel(bool suspended) => suspended ? Suspended : string.Empty;

    /// <summary>The label for the paused pill on its own.</summary>
    public static string PausedLabel(bool paused) => paused ? Paused : string.Empty;
}
