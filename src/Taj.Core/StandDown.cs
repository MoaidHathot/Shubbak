namespace Taj.Core;

/// <summary>
/// Whether the bar has anything to be doing.
/// </summary>
/// <remarks>
/// <para>
/// The bar polls its sources and rebuilds its tree on a loop. Both are wasted
/// whenever nobody can see the result, and there are two ways for that to be true: a
/// full-screen application covering the bar, and a suspended window manager, which
/// is what someone does before playing a game.
/// </para>
/// <para>
/// Measured before this existed: the bar spent 46.9 ms of CPU over 25 seconds on an
/// idle desktop - more than the window manager it reports on - and it spent exactly
/// the same behind a full-screen game.
/// </para>
/// <para>
/// Pure, and here rather than in the executable, because the executable has no test
/// project. A rule that decides when the bar stops updating is the last place to
/// accept "it looked right when I tried it".
/// </para>
/// </remarks>
public static class StandDown
{
    /// <summary>
    /// Whether the bar should stop polling and rebuilding.
    /// </summary>
    /// <param name="windowManagerSuspended">
    /// Whether Shubbak has released its hooks. Arrives over the pipe as a push, which
    /// is why push sources go on running while stood down - this is the signal that
    /// ends it.
    /// </param>
    /// <param name="fullScreenApp">
    /// Whether the shell has said a full-screen application is up. This is the edge:
    /// <c>ABN_FULLSCREENAPP</c> is delivered when one opens and when one closes.
    /// </param>
    /// <param name="confirmed">
    /// Whether a full-screen application is <i>still</i> believed to be up, asked
    /// independently. See <see cref="StillCovered"/> for why the edge is not trusted
    /// on its own.
    /// </param>
    public static bool ShouldStandDown(
        bool windowManagerSuspended, bool fullScreenApp, bool confirmed) =>
        windowManagerSuspended || (fullScreenApp && confirmed);

    /// <summary>
    /// Whether the reported activity still means the bar is covered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ABN_FULLSCREENAPP</c> says a full-screen application opened or closed. It
    /// does not say one is still in front, and the documentation does not promise it
    /// tracks the foreground. Trusting the edge alone would mean a bar frozen on a
    /// stale clock because the shell's idea of "a full-screen application" outlived
    /// what the user was actually looking at - which is a visible bug traded for an
    /// invisible saving.
    /// </para>
    /// <para>
    /// So the edge starts the stand-down and this ends it, asked again on every slow
    /// pass. Any mistake therefore lasts one pass rather than until the application
    /// closes.
    /// </para>
    /// <para>
    /// Takes the activity as a plain value rather than calling
    /// <c>SHQueryUserNotificationState</c> here, so this stays testable and
    /// <c>Taj.Core</c> stays free of Win32.
    /// </para>
    /// </remarks>
    /// <param name="activity">What the shell says the user is doing.</param>
    public static bool StillCovered(UserActivityKind activity) => activity switch
    {
        // A game with the display to itself, and an ordinary window that has taken
        // the whole screen - a browser playing a video full-screen is this one.
        UserActivityKind.FullScreenGame or UserActivityKind.FullScreenApp => true,

        // Presenting covers the screen too, and is the case where a bar appearing over
        // the slides would be worse than merely wasteful.
        UserActivityKind.Presenting => true,

        // Anything else, including "could not tell". Standing back up on an unknown
        // answer is the safe direction: the cost is the polling this avoids, and the
        // alternative is a bar that has stopped for a reason nobody can see.
        _ => false,
    };
}

/// <summary>
/// What the shell says the user is doing, as far as the bar needs to care.
/// </summary>
/// <remarks>
/// A narrowed copy of the platform layer's <c>UserActivity</c>. <c>Taj.Core</c> is
/// deliberately free of Win32 so it can be tested without a desktop, and the
/// executable maps one to the other.
/// </remarks>
public enum UserActivityKind
{
    /// <summary>Could not be determined.</summary>
    Unknown,

    /// <summary>Ordinary use. The bar is visible.</summary>
    Ordinary,

    /// <summary>A game with exclusive use of the display.</summary>
    FullScreenGame,

    /// <summary>Some other window covering the whole screen.</summary>
    FullScreenApp,

    /// <summary>Presentation mode.</summary>
    Presenting,
}
