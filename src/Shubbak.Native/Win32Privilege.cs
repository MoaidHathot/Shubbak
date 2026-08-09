using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace Shubbak.Native;

/// <summary>
/// What this process is allowed to do to windows it does not own.
/// </summary>
/// <remarks>
/// <para>
/// Windows will not let a process move, resize or send messages to a window belonging
/// to a process at a higher integrity level. That is UIPI, and it is the reason an
/// ordinary window manager cannot tile Task Manager: <c>SetWindowPos</c> returns
/// <c>ERROR_ACCESS_DENIED</c> and the window does not move. Measured, on Windows 11:
/// Task Manager runs at high integrity, an ordinary application at medium, and the
/// call fails between them and succeeds within them.
/// </para>
/// <para>
/// There are exactly two ways past it, and both are properties of how this process
/// was built and started rather than of any window it is looking at - which is why
/// the answer is computed once here instead of per window.
/// </para>
/// </remarks>
public static class Win32Privilege
{
    /// <summary>
    /// Whether this process may drive windows above its own integrity level.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cached, because it cannot change while the process runs: both inputs are fixed
    /// at creation. A window manager asks this per window it evaluates, and neither
    /// answer is worth a system call each time.
    /// </para>
    /// </remarks>
    public static bool CanDriveHigherIntegrity => s_canDrive.Value;

    /// <summary>
    /// Whether this process was granted UI access.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Granted only to a binary whose manifest asks for it, that carries an
    /// Authenticode signature the machine trusts, and that lives under
    /// <c>%ProgramFiles%</c> or <c>%SystemRoot%\System32</c>. All three, or the
    /// process does not start at all - it does not quietly run without the privilege.
    /// </para>
    /// <para>
    /// This is how a window manager tiles elevated windows without itself being
    /// elevated, and it is what GlazeWM does. Reported separately from elevation
    /// because they are different answers to "why can this build not move that
    /// window", with different remedies.
    /// </para>
    /// </remarks>
    public static bool HasUiAccess => s_uiAccess.Value;

    /// <summary>Whether this process is running elevated.</summary>
    public static bool IsElevated => s_elevated.Value;

    private static readonly Lazy<bool> s_uiAccess = new(() => Query(
        TOKEN_INFORMATION_CLASS.TokenUIAccess));

    private static readonly Lazy<bool> s_elevated = new(() => Query(
        TOKEN_INFORMATION_CLASS.TokenElevation));

    private static readonly Lazy<bool> s_canDrive = new(() => HasUiAccess || IsElevated);

    /// <summary>
    /// Reads a token field that is a single non-zero-means-true integer.
    /// </summary>
    /// <remarks>
    /// <c>TokenUIAccess</c> and <c>TokenElevation</c> are both a single <c>DWORD</c>,
    /// which is why one reader serves both. A failure is reported as false: not
    /// knowing whether a privilege was granted has to mean acting as though it was
    /// not, or the window manager would try things the system will refuse and leave
    /// tiles reserved for windows it cannot move.
    /// </remarks>
    private static unsafe bool Query(TOKEN_INFORMATION_CLASS field)
    {
        if (!PInvoke.OpenProcessToken(
                PInvoke.GetCurrentProcess_SafeHandle(),
                TOKEN_ACCESS_MASK.TOKEN_QUERY,
                out SafeFileHandle token))
        {
            return false;
        }

        using (token)
        {
            uint value = 0;
            uint returned = 0;

            return PInvoke.GetTokenInformation(
                (HANDLE)token.DangerousGetHandle(), field, &value, sizeof(uint), &returned)
                && value != 0;
        }
    }
}
