using System.Diagnostics;

namespace Shubbak.Native.Tests;

/// <summary>What the desktop these tests run on is like, for the messages they leave.</summary>
internal static class Desktop
{
    /// <summary>
    /// The trait on tests that can only be measured where this process can be given
    /// the foreground.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The focus sink is about which window Windows hands the foreground to, and to
    /// observe that a test has to take the foreground first. On the hosted
    /// <c>windows-11-arm</c> runner it cannot: the image comes up with a "Microsoft
    /// account" sign-in surface - a <c>Windows.UI.Core.CoreWindow</c> hosted by
    /// <c>WWAHost</c> - in front, which refuses <c>AttachThreadInput</c> and is not
    /// moved by an injected input event either. Every test here failed at its first
    /// step there, for a cause that has nothing to do with the code under test, while
    /// the x64 runner ran them all.
    /// </para>
    /// <para>
    /// So the ARM64 job filters this trait out, saying why, and the x64 job keeps the
    /// coverage. A trait rather than a skip because xunit 2 has no way to skip at run
    /// time, and a skip decided at discovery would have to take the foreground to find
    /// out - which is the very thing being tested.
    /// </para>
    /// </remarks>
    public const string RequiresForeground = "Foreground";

    /// <summary>
    /// Explains a refusal to take the foreground: who has it, and what kind of session
    /// this is.
    /// </summary>
    /// <remarks>
    /// Written for the CI log. A failure on a hosted runner can only be diagnosed from
    /// what the assertion says, and "could not take the foreground" on its own does not
    /// distinguish a locked-out session from a window in front that refuses to give
    /// it up - the two have different fixes. This is what named the sign-in surface
    /// above.
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
