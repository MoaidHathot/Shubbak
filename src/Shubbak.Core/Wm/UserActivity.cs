namespace Shubbak.Core.Wm;

/// <summary>
/// What the shell believes the user is doing, as far as it can tell.
/// </summary>
/// <remarks>
/// <para>
/// Named for what each state means to a window manager rather than for the constant it
/// came from, because the constants are named for notifications. The platform layer
/// reads it from <c>SHQueryUserNotificationState</c>; what that call can and cannot
/// see is documented there.
/// </para>
/// <para>
/// Lives here rather than in the platform layer because it is <i>reported</i>: it
/// travels in a <see cref="WmEvent"/> and in the state snapshot, so every process
/// with no Win32 in it - the bar's model, the tests - has to be able to name it. The
/// bar used to keep a narrowed copy for exactly that reason.
/// </para>
/// </remarks>
public enum UserActivity
{
    /// <summary>The call failed, or the answer is not one we recognise.</summary>
    Unknown,

    /// <summary>Nothing special: an ordinary desktop.</summary>
    Ordinary,

    /// <summary>A Direct3D exclusive-fullscreen application - a game, reliably.</summary>
    FullScreenGame,

    /// <summary>A full-screen window that is not Direct3D exclusive.</summary>
    FullScreenApp,

    /// <summary>Presentation mode: the user has asked not to be interrupted.</summary>
    Presenting,

    /// <summary>The first hour after a new user logs in for the first time.</summary>
    QuietTime,
}

/// <summary>
/// The names a <see cref="UserActivity"/> goes by outside the process.
/// </summary>
/// <remarks>
/// Spelt out rather than derived from the member names, so that renaming a member
/// cannot silently change what a subscriber reads, and so the same words can be
/// written in a config file later without anyone having to guess how an enum
/// lower-cases. Kebab-case, like every other word the config accepts.
/// </remarks>
public static class UserActivityNames
{
    /// <summary>The wire name of <paramref name="activity"/>.</summary>
    public static string Wire(this UserActivity activity) => activity switch
    {
        UserActivity.Ordinary => "ordinary",
        UserActivity.FullScreenGame => "fullscreen-game",
        UserActivity.FullScreenApp => "fullscreen-app",
        UserActivity.Presenting => "presenting",
        UserActivity.QuietTime => "quiet-time",
        _ => "unknown",
    };

    /// <summary>The activity a wire name stands for, or null if it is not one.</summary>
    public static UserActivity? Parse(string? name) => name switch
    {
        "ordinary" => UserActivity.Ordinary,
        "fullscreen-game" => UserActivity.FullScreenGame,
        "fullscreen-app" => UserActivity.FullScreenApp,
        "presenting" => UserActivity.Presenting,
        "quiet-time" => UserActivity.QuietTime,
        "unknown" => UserActivity.Unknown,
        _ => null,
    };
}
