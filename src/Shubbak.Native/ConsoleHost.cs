using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;

namespace Shubbak.Native;

/// <summary>
/// Gets this process a console, for the two occasions a GUI-subsystem daemon needs one.
/// </summary>
/// <remarks>
/// <para>
/// <c>shubbak-wm</c> is built as a GUI-subsystem binary. That is not because it has a
/// window - it does not - but because of what the loader does before <c>Main</c> runs:
/// a console-subsystem process started by anything that has no console of its own
/// (Explorer, a shortcut, Task Scheduler, the <c>Run</c> key) gets a console window
/// allocated for it, and that window stays for the life of the process. For a daemon
/// started at logon that is a black rectangle on the desktop of every session, for
/// ever. There is no flag to suppress it; the subsystem is a field in the PE header
/// and the decision is made before any code of ours executes.
/// </para>
/// <para>
/// The cost of that choice is that the two cases where a console genuinely is wanted
/// have to ask for one, which is what this class is for:
/// </para>
/// <list type="number">
/// <item><c>--foreground</c>, where somebody is watching trace output.</item>
/// <item>
/// A startup failure. A daemon that cannot load its config must say so; without a
/// console it would exit silently, which is the single worst outcome of the switch
/// and the reason <see cref="EnsureForError"/> exists.
/// </item>
/// </list>
/// </remarks>
public static class ConsoleHost
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private static bool s_allocated;

    /// <summary>Whether this process currently has a console attached.</summary>
    public static bool HasConsole => PInvoke.GetConsoleWindow() != HWND.Null;

    /// <summary>
    /// Whether standard output already leads somewhere - a console, a file or a pipe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This, rather than <see cref="HasConsole"/>, is what decides whether a console
    /// needs conjuring, and the difference is not academic:
    /// <c>shubbak-wm --version &gt; version.txt</c> has no console but a perfectly good
    /// place to write. Attaching one would replace the redirected handle, so the
    /// window would open, the text would go into it, and the file would be empty.
    /// </para>
    /// <para>
    /// <see cref="Console.IsOutputRedirected"/> cannot answer this. It asks whether
    /// the handle is a character device, and an <em>absent</em> handle is not one - so
    /// a GUI process with no console at all reports that its output is redirected,
    /// which is the opposite of the truth. The handle itself is the only honest
    /// source.
    /// </para>
    /// </remarks>
    public static bool HasOutput
    {
        get
        {
            HANDLE handle = PInvoke.GetStdHandle(STD_HANDLE.STD_OUTPUT_HANDLE);
            return handle != HANDLE.Null && handle != new HANDLE(-1);
        }
    }

    /// <summary>
    /// Attaches to the launching terminal's console, or creates one.
    /// </summary>
    /// <returns>Whether output has somewhere to go afterwards.</returns>
    /// <remarks>
    /// <para>
    /// The parent's console is preferred over a new one, so that
    /// <c>shubbak-wm --foreground</c> typed into a terminal writes into that terminal
    /// rather than opening a second window the user then has to find.
    /// </para>
    /// <para>
    /// One consequence is worth stating because it surprises people and cannot be
    /// fixed from this side: the shell does not wait for a GUI-subsystem process, so
    /// it has already printed its next prompt by the time this attaches. Output is
    /// therefore interleaved with the prompt. It is cosmetic - the text all arrives -
    /// but it looks wrong, and it is why the usage text says to expect it.
    /// </para>
    /// </remarks>
    public static bool Ensure()
    {
        // Already redirected, or already has a console. Either way, leave it alone.
        if (HasOutput) return true;

        if (PInvoke.AttachConsole(AttachParentProcess))
        {
            Rebind();
            return true;
        }

        // Started from something with no console at all - Explorer, or a shortcut.
        // A window of our own is better than discarding the output.
        if (PInvoke.AllocConsole())
        {
            s_allocated = true;
            Rebind();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets a console for reporting a fatal startup problem, if one can be had.
    /// </summary>
    /// <remarks>
    /// Identical to <see cref="Ensure"/> except that it never throws: it is called
    /// from the failure path, where the caller has nothing better to do and losing the
    /// original error to a secondary one would be perverse.
    /// </remarks>
    public static bool EnsureForError()
    {
        try
        {
            return Ensure();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a console this process created is about to disappear with it.
    /// </summary>
    /// <remarks>
    /// A console allocated by <see cref="PInvoke.AllocConsole"/> is destroyed when the
    /// process exits, taking the last thing written to it off the screen before anyone
    /// can read it. A caller reporting a fatal error uses this to decide whether it
    /// owes the user a pause. An attached parent console is not ours and must never be
    /// held open.
    /// </remarks>
    public static bool OwnsConsole => s_allocated;

    /// <summary>
    /// Points <see cref="Console"/> at the handles the new console just supplied.
    /// </summary>
    /// <remarks>
    /// <see cref="Console.Out"/> is created on first use and cached. If anything has
    /// written before the console existed it holds a stream that goes nowhere, and
    /// every later write goes nowhere too. Reopening the standard handles re-reads
    /// them from the operating system, which is the only way back.
    /// </remarks>
    private static void Rebind()
    {
        try
        {
            var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };

            Console.SetOut(output);
            Console.SetError(error);
        }
        catch (IOException)
        {
            // A console exists but its handles are not usable. Nothing further to try.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
