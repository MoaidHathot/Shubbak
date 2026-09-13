using System.Runtime.InteropServices;
using Shubbak.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dalil;

/// <summary>
/// How one attempt to take the foreground went.
/// </summary>
/// <param name="InFront">Whether the target was the foreground window afterwards.</param>
/// <param name="Attached">
/// Whether the input queues were joined for the attempt. False when there was nothing
/// to join - no foreground window, or the target's own thread - and when Windows
/// refused, which it does for a UWP application's frame and across an integrity
/// boundary.
/// </param>
/// <param name="Accepted">
/// What <c>SetForegroundWindow</c> answered on the first ask. True with
/// <paramref name="InFront"/> false is the interesting combination: the request was
/// taken and the switch had not landed by the time it was looked for, which is what a
/// retry a moment later is for.
/// </param>
/// <param name="Nudged">
/// Whether the second route was taken and Windows accepted the injected event. The
/// route is tried whenever the first was refused; Windows refuses the event itself
/// when the window in front runs higher than this process.
/// </param>
/// <param name="Holder">The window in front afterwards, when it is not the target.</param>
internal readonly record struct ForegroundAttempt(bool InFront, bool Attached, bool Accepted, bool Nudged, nint Holder)
{
    /// <summary>Whether the second route was tried at all - it is, whenever the first was refused.</summary>
    public bool NudgeTried => !Attached || !Accepted;

    /// <summary>The attempt in a few words, for the log.</summary>
    public string Describe()
    {
        if (InFront) return Nudged ? "in front, after a nudge" : "in front";

        string nudge = !NudgeTried ? string.Empty
            : Nudged ? ", nudged and still refused"
            : ", nudge refused - is the window in front running higher than Dalil?";

        return $"{Foreground.Describe(Holder)} kept the foreground " +
               $"(attach {(Attached ? "ok" : "refused")}, request {(Accepted ? "accepted" : "refused")}{nudge})";
    }
}

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
/// <para>
/// And when that is refused, one more thing. <c>AttachThreadInput</c> to the thread
/// behind a UWP application's frame - Settings, the Store, Media Player, whatever
/// <c>ApplicationFrameHost</c> is showing - fails with access denied, and so, then,
/// does every <c>SetForegroundWindow</c>. On a desktop straight after logon the window
/// in front is very often exactly such a frame, which is why the palette's first open
/// of a session was the one that failed and the second - by which time Windows had
/// moved the foreground on - the one that worked. The way past it is the other clause
/// of the rule: a process that has just <em>provided</em> input may take the
/// foreground. So this process provides one: a key-up, through <c>SendInput</c>, of a
/// key that nobody has bound and nobody is holding. It reaches the window in front as
/// a release of a key it never saw pressed, which every window ignores, and the
/// window manager's hook lets it through for the same reason. Measured: refused
/// twice, then taken on the very next call.
/// </para>
/// <para>
/// The answer is a report rather than a bool, because the one bit was misleading. A
/// switch the system has accepted lands when the thread losing the foreground gets
/// round to being told, and on a busy desktop that is a few milliseconds after this
/// returns - so "not in front yet" and "refused" read the same to a caller that only
/// had the bit, and the caller closed the palette over the first. Whoever asks now
/// knows which it was, and who is in the way.
/// </para>
/// </remarks>
internal static class Foreground
{
    /// <summary>
    /// The key whose release is injected: <c>VK_NONAME</c>, which the keyboard layout
    /// maps to nothing and no application handles.
    /// </summary>
    private const VIRTUAL_KEY Neutral = (VIRTUAL_KEY)0xFC;

    /// <summary>Makes a window the active, foreground, focused window.</summary>
    public static unsafe ForegroundAttempt Take(HWND target)
    {
        if (target.IsNull || !PInvoke.IsWindow(target)) return new ForegroundAttempt(false, false, false, false, 0);

        HWND foreground = PInvoke.GetForegroundWindow();

        // Already in front. Focus inside the window can still be wrong - a window can
        // be foreground with no focused window at all - so this is asserted rather
        // than assumed, which is what makes calling Open on an open palette a repair
        // rather than a no-op.
        if (foreground == target)
        {
            PInvoke.SetFocus(target);
            return new ForegroundAttempt(true, false, true, false, 0);
        }

        uint ours = PInvoke.GetCurrentThreadId();
        uint theirs = foreground.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(foreground, null);

        bool attached = false;
        bool accepted;

        try
        {
            if (theirs != 0 && theirs != ours)
                attached = PInvoke.AttachThreadInput(ours, theirs, true);

            accepted = Ask(target);
        }
        finally
        {
            if (attached) PInvoke.AttachThreadInput(ours, theirs, false);
        }

        HWND after = PInvoke.GetForegroundWindow();

        if (after == target) return new ForegroundAttempt(true, attached, accepted, false, 0);

        // Refused outright, rather than accepted and not yet landed: the second route.
        // Not when the request was taken, because then a retry a moment later is the
        // right answer and an injected key is a stranger thing to do than to wait.
        // The report keeps the first answer, so that NudgeTried reads the same
        // condition this does.
        bool nudged = false;

        if (!attached || !accepted)
        {
            nudged = Nudge();
            if (nudged) _ = Ask(target);
            after = PInvoke.GetForegroundWindow();
        }

        return new ForegroundAttempt(after == target, attached, accepted, nudged, after == target ? 0 : (nint)after.Value);
    }

    /// <summary>Asks for the foreground, the active window and the focus, in that order.</summary>
    /// <remarks>
    /// All three, and in this order. SetForegroundWindow decides which window the system
    /// considers in front; SetActiveWindow and SetFocus decide where keys go within the
    /// input queue this thread is now attached to. Doing only the first is what produced
    /// a palette that was visibly in front and still received nothing.
    /// </remarks>
    private static bool Ask(HWND target)
    {
        PInvoke.BringWindowToTop(target);
        bool accepted = PInvoke.SetForegroundWindow(target);
        PInvoke.SetActiveWindow(target);
        PInvoke.SetFocus(target);
        return accepted;
    }

    /// <summary>
    /// Provides one input event, so that this process has provided the last one.
    /// </summary>
    /// <returns>Whether Windows took it. It does not when the window in front runs higher than this process.</returns>
    private static bool Nudge()
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki.wVk = Neutral;
        input.Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;

        Span<INPUT> one = [input];

        return PInvoke.SendInput(one, Marshal.SizeOf<INPUT>()) == 1;
    }

    /// <summary>Whoever has the foreground right now, as a log can name it.</summary>
    public static unsafe string DescribeCurrent() => Describe((nint)PInvoke.GetForegroundWindow().Value);

    /// <summary>A window as a log can name it: class and owning process.</summary>
    /// <remarks>
    /// Not the title. Titles are the user's documents and their correspondents, and a
    /// log line saying which application held the foreground does not need them.
    /// </remarks>
    public static string Describe(nint window)
    {
        if (window == 0 || !Win32Window.Exists(window)) return "nothing";

        string className = Win32Window.GetClassName(window);
        string? path = Win32Window.GetProcessPath(Win32Window.GetProcessId(window));
        string process = path is null ? "?" : Path.GetFileNameWithoutExtension(path);

        return $"{className} [{process}]";
    }
}
