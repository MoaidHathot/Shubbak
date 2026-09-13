namespace Taj.Core;

/// <summary>What a <see cref="KeyboardCommand"/> asks for.</summary>
public enum KeyboardChange
{
    /// <summary>The layout after the current one in the installed list, wrapping.</summary>
    Next,

    /// <summary>The layout before the current one, wrapping.</summary>
    Previous,

    /// <summary>The first installed layout whose language matches a two-letter code.</summary>
    Language,
}

/// <summary>
/// The one click command the bar performs itself: switching the input language of
/// the window in front.
/// </summary>
/// <remarks>
/// <para>
/// Every other <c>on-click</c> is sent to the window manager, and that is right:
/// clicking a workspace should do exactly what its keybinding does. The keyboard
/// layout is different in kind. Windows keeps it per window, the bar already reads
/// it - the <c>keyboard</c> source is the indicator this makes clickable - and
/// changing it is a message posted to the window in front, which the bar can do as
/// well as anyone and without a round trip through the pipe. So the verb is the
/// bar's, spelled with the same word as the source: <c>keyboard next</c>,
/// <c>keyboard previous</c>, or <c>keyboard he</c> for a particular language.
/// </para>
/// <para>
/// Parsed here, without Win32, so the loader can say at load time that
/// <c>keyboard nxt</c> is not a thing - the symptom otherwise is a control that
/// does nothing, which looks exactly like a control that was never wired up.
/// </para>
/// </remarks>
/// <param name="Change">Which layout to move to.</param>
/// <param name="Language">
/// The two-letter language code, upper case, when <paramref name="Change"/> is
/// <see cref="KeyboardChange.Language"/>; otherwise null.
/// </param>
public sealed record KeyboardCommand(KeyboardChange Change, string? Language = null)
{
    /// <summary>The word a click command starts with to be this rather than the window manager's.</summary>
    public const string Verb = "keyboard";

    /// <summary>The spellings that are accepted, for a hint.</summary>
    public const string Accepted = "keyboard next, keyboard previous, or keyboard followed by a two-letter language code such as he";

    /// <summary>
    /// Whether a click command is addressed to the bar rather than the window manager.
    /// </summary>
    /// <remarks>
    /// Decided on the first word alone, so a malformed keyboard command is still
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
    public static bool TryParse(string? command, out KeyboardCommand? parsed, out string? problem)
    {
        parsed = null;
        problem = null;

        string[] words = (command ?? string.Empty).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0 || !string.Equals(words[0], Verb, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"not a keyboard command; write {Accepted}.";
            return false;
        }

        if (words.Length == 1)
        {
            problem = $"keyboard needs to say which layout; write {Accepted}.";
            return false;
        }

        if (words.Length > 2)
        {
            problem = $"keyboard takes one word after it, not '{string.Join(' ', words[1..])}'; write {Accepted}.";
            return false;
        }

        string what = words[1];

        switch (what.ToLowerInvariant())
        {
            case "next":
                parsed = new KeyboardCommand(KeyboardChange.Next);
                return true;

            case "previous" or "prev":
                parsed = new KeyboardCommand(KeyboardChange.Previous);
                return true;

            default:
                if (what.Length == 2 && char.IsAsciiLetter(what[0]) && char.IsAsciiLetter(what[1]))
                {
                    parsed = new KeyboardCommand(KeyboardChange.Language, what.ToUpperInvariant());
                    return true;
                }

                problem = $"'{what}' is not a layout to switch to; write {Accepted}.";
                return false;
        }
    }

    public override string ToString() => Change switch
    {
        KeyboardChange.Next => $"{Verb} next",
        KeyboardChange.Previous => $"{Verb} previous",
        _ => $"{Verb} {Language?.ToLowerInvariant()}",
    };
}
