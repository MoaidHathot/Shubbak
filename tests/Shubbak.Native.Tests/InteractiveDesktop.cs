using System.Diagnostics;

namespace Shubbak.Native.Tests;

/// <summary>
/// A fact that can only be measured where a window can be given the foreground.
/// </summary>
/// <remarks>
/// <para>
/// The focus sink is about which window Windows hands the foreground to, and that
/// can only be observed on an interactive window station - one with an input desktop
/// for the foreground to belong to. A process running as a service, or under a CI
/// runner started in a non-interactive session, can create windows and see them, but
/// no window of its can ever be in front, and every one of these tests would fail at
/// its first step for a reason that has nothing to do with the code under test.
/// </para>
/// <para>
/// Skipped there, with the reason in the test output, rather than failed. Everywhere
/// else they run, and a refusal to take the foreground is a real failure with the
/// window in front named in the message - see <see cref="Desktop.WhyNotInFront"/>.
/// </para>
/// </remarks>
internal sealed class FactOnAnInteractiveDesktopAttribute : FactAttribute
{
    public FactOnAnInteractiveDesktopAttribute()
    {
        if (!Environment.UserInteractive) Skip = Desktop.NotInteractive;
    }
}

/// <summary>The same, for a theory.</summary>
internal sealed class TheoryOnAnInteractiveDesktopAttribute : TheoryAttribute
{
    public TheoryOnAnInteractiveDesktopAttribute()
    {
        if (!Environment.UserInteractive) Skip = Desktop.NotInteractive;
    }
}

/// <summary>What the desktop these tests run on is like, for the messages they leave.</summary>
internal static class Desktop
{
    public const string NotInteractive =
        "no interactive window station: nothing in this process can be given the foreground, " +
        "so which window Windows hands it to cannot be measured here";

    /// <summary>
    /// Explains a refusal to take the foreground: who has it, and what kind of session
    /// this is.
    /// </summary>
    /// <remarks>
    /// Written for the CI log. A failure on a hosted runner can only be diagnosed from
    /// what the assertion says, and "could not take the foreground" on its own does not
    /// distinguish a locked-out session from a window in front that refuses to give
    /// it up - the two have different fixes.
    /// </remarks>
    public static string WhyNotInFront(string what)
    {
        nint foreground = Win32Window.GetForeground();

        string holder = foreground == 0 || !Win32Window.Exists(foreground)
            ? "nothing"
            : $"0x{foreground:X} \"{Win32Window.GetTitle(foreground)}\" [{Win32Window.GetClassName(foreground)}] " +
              $"pid {Win32Window.GetProcessId(foreground)} " +
              $"({Path.GetFileNameWithoutExtension(Win32Window.GetProcessPath(Win32Window.GetProcessId(foreground))) ?? "?"})";

        return $"{what} could not take the foreground; in front: {holder}; " +
               $"interactive {Environment.UserInteractive}, session {Process.GetCurrentProcess().SessionId}";
    }
}
