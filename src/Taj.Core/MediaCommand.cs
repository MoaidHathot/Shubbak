namespace Taj.Core;

/// <summary>What a <see cref="MediaCommand"/> presses.</summary>
public enum MediaKey
{
    /// <summary>Play if paused, pause if playing: the one key a media pill most wants.</summary>
    PlayPause,

    /// <summary>The next track.</summary>
    Next,

    /// <summary>The previous track, or the start of this one, as the player decides.</summary>
    Previous,

    /// <summary>Stop.</summary>
    Stop,

    /// <summary>Mute, or unmute.</summary>
    Mute,

    /// <summary>The system volume up a notch.</summary>
    VolumeUp,

    /// <summary>The system volume down a notch.</summary>
    VolumeDown,
}

/// <summary>
/// The second click command the bar performs itself: a press of one of the keyboard's
/// media keys.
/// </summary>
/// <remarks>
/// <para>
/// A media pill wants play, pause, next and previous, and a volume pill wants the
/// wheel to turn the volume - and every one of those is a key most keyboards already
/// have. Windows routes a media key to whichever player is current without anyone
/// having to know which that is, the same as the key on the keyboard would, which is
/// what makes this a verb rather than a program: <c>exec</c> of a player's command
/// line would mean one pill per player. The bar presses the key the way
/// <c>keyboard</c> posts the layout change, with no round trip through the pipe for
/// something the window manager has no view on.
/// </para>
/// <para>
/// Parsed here, without Win32, so the loader can say at load time that
/// <c>media pause</c> is not a thing - the symptom otherwise is a control that does
/// nothing, which looks exactly like a control that was never wired up.
/// </para>
/// </remarks>
/// <param name="Key">Which key to press.</param>
public sealed record MediaCommand(MediaKey Key)
{
    /// <summary>The word a click command starts with to be this rather than the window manager's.</summary>
    public const string Verb = "media";

    /// <summary>The spellings that are accepted, for a hint.</summary>
    public const string Accepted =
        "media play-pause, media next, media previous, media stop, media mute, media volume-up or media volume-down";

    /// <summary>
    /// Whether a click command is addressed to the bar rather than the window manager.
    /// </summary>
    /// <remarks>
    /// Decided on the first word alone, so a malformed media command is still
    /// recognised as one - and refused with a reason - rather than being sent to a
    /// window manager that has never heard of the verb.
    /// </remarks>
    public static bool Recognises(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        ReadOnlySpan<char> trimmed = command.AsSpan().Trim();
        int end = trimmed.IndexOfAny(' ', '\t');
        ReadOnlySpan<char> verb = end < 0 ? trimmed : trimmed[..end];

        return verb.Equals(Verb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses a click command, saying why when it cannot.</summary>
    /// <param name="command">The <c>on-click</c> text.</param>
    /// <param name="parsed">The command, when the text is one.</param>
    /// <param name="problem">What is wrong with the text, when it is not.</param>
    public static bool TryParse(string? command, out MediaCommand? parsed, out string? problem)
    {
        parsed = null;
        problem = null;

        string[] words = (command ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0 || !string.Equals(words[0], Verb, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"not a media command; write {Accepted}.";
            return false;
        }

        if (words.Length == 1)
        {
            problem = $"media needs to say which key; write {Accepted}.";
            return false;
        }

        if (words.Length > 2)
        {
            problem = $"media takes one word after it, not '{string.Join(' ', words[1..])}'; write {Accepted}.";
            return false;
        }

        // Several spellings for each, since nobody remembers whether it was the
        // dash or the underscore, and the keybinding names - media_play_pause,
        // volume_up - are already in people's files.
        MediaKey? key = words[1].ToLowerInvariant().Replace('_', '-') switch
        {
            "play-pause" or "playpause" or "play" or "pause" or "toggle" or "media-play-pause" => MediaKey.PlayPause,
            "next" or "media-next" or "forward" => MediaKey.Next,
            "previous" or "prev" or "media-prev" or "back" => MediaKey.Previous,
            "stop" or "media-stop" => MediaKey.Stop,
            "mute" or "volume-mute" => MediaKey.Mute,
            "volume-up" or "up" or "louder" => MediaKey.VolumeUp,
            "volume-down" or "down" or "quieter" => MediaKey.VolumeDown,
            _ => null,
        };

        if (key is not { } found)
        {
            problem = $"'{words[1]}' is not a media key; write {Accepted}.";
            return false;
        }

        parsed = new MediaCommand(found);
        return true;
    }

    public override string ToString() => $"{Verb} " + Key switch
    {
        MediaKey.PlayPause => "play-pause",
        MediaKey.Next => "next",
        MediaKey.Previous => "previous",
        MediaKey.Stop => "stop",
        MediaKey.Mute => "mute",
        MediaKey.VolumeUp => "volume-up",
        _ => "volume-down",
    };
}
