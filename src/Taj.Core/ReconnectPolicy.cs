using System.Diagnostics;

namespace Taj.Core;

/// <summary>
/// Whether a bar should keep waiting for a window manager it cannot find.
/// </summary>
/// <remarks>
/// <para>
/// The bar used to retry for ever. That is right while it is starting - it is
/// normally launched by the window manager's own startup command, so for a moment
/// there is nothing to connect to, and a bar that gave up during that race would
/// simply never appear.
/// </para>
/// <para>
/// It is wrong an hour later. Killing the window manager left a bar attached to
/// nothing, redrawing a stale world once a second, with no way to close it but Task
/// Manager - and terminating it that way skips the appbar being unregistered, so the
/// shell can be left holding a strip of screen for a bar that no longer exists.
/// </para>
/// <para>
/// Here rather than beside the connection that uses it because it is a policy of
/// three values and no I/O, and the connection is a pump full of pipes and tasks.
/// </para>
/// </remarks>
public static class ReconnectPolicy
{
    /// <summary>
    /// Whether to stop waiting and close the bar.
    /// </summary>
    /// <param name="everConnected">
    /// Whether a window manager has ever been reached. Until one has, the bar waits
    /// indefinitely whatever the timeout says - that is the startup race, and losing
    /// it must not cost the bar.
    /// </param>
    /// <param name="lostAtTicks">
    /// When the connection was lost, or zero while connected.
    /// </param>
    /// <param name="now">The current timestamp.</param>
    /// <param name="timeout">How long to wait, or null to wait for ever.</param>
    public static bool ShouldGiveUp(bool everConnected, long lostAtTicks, long now, TimeSpan? timeout)
    {
        if (timeout is not { } limit) return false;
        if (!everConnected || lostAtTicks == 0) return false;

        return now - lostAtTicks >= limit.TotalSeconds * Stopwatch.Frequency;
    }
}
