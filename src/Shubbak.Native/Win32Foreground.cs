using Windows.Win32;
using Windows.Win32.Foundation;

namespace Shubbak.Native;

/// <summary>
/// Hands the right to take the foreground to another process.
/// </summary>
/// <remarks>
/// <para>
/// Windows will not let an arbitrary process raise its own window. Only the process
/// that owns the foreground window, or one that has just received the user's input,
/// may call <c>SetForegroundWindow</c> and have it obeyed; everyone else gets a
/// return value of true and a flashing taskbar button.
/// </para>
/// <para>
/// That rule is a problem for a window manager specifically, because the daemon
/// <em>swallows</em> the keystroke that was meant to raise something. The user
/// pressed a key, the input was consumed here, and the process that should now put a
/// window on screen never saw it - so it is refused for lacking exactly the
/// permission the keypress conferred, on the grounds that it did not receive a
/// keypress it was never allowed to receive.
/// </para>
/// <para>
/// <c>AllowSetForegroundWindow</c> is the documented way to pass that right on, and
/// this is the case it was designed for. The grant is narrow: it names one process,
/// it is consumed by the next foreground change, and it expires on its own if unused.
/// </para>
/// </remarks>
public static class Win32Foreground
{
    /// <summary>
    /// Permits one process to bring a window to the foreground, once.
    /// </summary>
    /// <returns>
    /// Whether Windows accepted the grant. False is not worth acting on: the usual
    /// cause is that this process is not itself in a position to give the right away,
    /// and the client's own attempt may still succeed for its own reasons.
    /// </returns>
    public static bool AllowForeground(uint processId) =>
        processId != 0 && PInvoke.AllowSetForegroundWindow(processId);

    /// <summary>Which process owns the other end of a connected named pipe.</summary>
    /// <remarks>
    /// Used to hand foreground rights to the client that is about to need them,
    /// rather than to every process on the machine. <c>ASFW_ANY</c> would be one call
    /// instead of a lookup, and would grant the right to whatever happened to ask
    /// first.
    /// </remarks>
    public static uint ProcessIdOfPipeClient(nint pipeHandle)
    {
        if (pipeHandle == 0) return 0;

        uint processId = 0;

        unsafe
        {
            return PInvoke.GetNamedPipeClientProcessId(new HANDLE(pipeHandle), &processId)
                ? processId
                : 0;
        }
    }
}
