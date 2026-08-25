using Windows.Win32;
using Windows.Win32.Foundation;

namespace Dalil;

/// <summary>
/// Takes the foreground, against Windows' wishes.
/// </summary>
/// <remarks>
/// <para>
/// <c>SetForegroundWindow</c> is refused unless the calling thread already owns the
/// foreground or has just received the user's input. Neither is true here, and the
/// reason is worth stating plainly because it is not obvious: the window manager
/// <em>swallowed</em> the keystroke that asked for the palette. A low-level hook
/// consuming a key does not make the hooking process the recipient of that input
/// event, so nobody involved has the right the keypress ought to have conferred.
/// </para>
/// <para>
/// The daemon calls <c>AllowSetForegroundWindow</c> to hand its right over, and that
/// is worth doing, but it cannot be relied on: a process may only give away a right
/// it holds, and a background daemon that never has the foreground usually holds
/// nothing to give. When the grant fails the call is refused silently - the return
/// value is still TRUE - and the window is shown without ever being activated. It
/// appears on screen, looks completely normal, and every key goes to whatever had
/// focus before. That is the intermittent "I can't type into it" this exists to fix.
/// </para>
/// <para>
/// So the input queues are attached instead, which is the documented workaround and
/// the same one <c>WindowActions.Focus</c> has always used on the window manager's
/// side. The attachment is always undone, including on failure: leaving two input
/// queues joined couples their input state and produces symptoms that look like
/// random keyboard freezes across the whole desktop.
/// </para>
/// </remarks>
internal static class Foreground
{
    /// <summary>Makes a window the active, foreground, focused window.</summary>
    /// <returns>Whether it actually ended up in front.</returns>
    public static unsafe bool Take(HWND target)
    {
        if (target.IsNull || !PInvoke.IsWindow(target)) return false;

        HWND foreground = PInvoke.GetForegroundWindow();

        // Already in front. Focus inside the window can still be wrong - a window can
        // be foreground with no focused window at all - so this is asserted rather
        // than assumed, which is what makes calling Open on an open palette a repair
        // rather than a no-op.
        if (foreground == target)
        {
            PInvoke.SetFocus(target);
            return true;
        }

        uint ours = PInvoke.GetCurrentThreadId();
        uint theirs = foreground.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(foreground, null);

        bool attached = false;

        try
        {
            if (theirs != 0 && theirs != ours)
                attached = PInvoke.AttachThreadInput(ours, theirs, true);

            PInvoke.BringWindowToTop(target);
            PInvoke.SetForegroundWindow(target);

            // Both, and in this order. SetForegroundWindow decides which window the
            // system considers in front; SetActiveWindow and SetFocus decide where
            // keys go within the input queue this thread is now attached to. Doing
            // only the first is what produced a palette that was visibly in front and
            // still received nothing.
            PInvoke.SetActiveWindow(target);
            PInvoke.SetFocus(target);
        }
        finally
        {
            if (attached) PInvoke.AttachThreadInput(ours, theirs, false);
        }

        return PInvoke.GetForegroundWindow() == target;
    }
}
