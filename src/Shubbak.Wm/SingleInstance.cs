using Shubbak.Core.Diagnostics;
using Shubbak.Ipc;
using Shubbak.Native;

namespace Shubbak.Wm;

/// <summary>
/// Makes sure exactly one window manager runs per account.
/// </summary>
/// <remarks>
/// <para>
/// There was no such guarantee, and nothing detected its absence. A second
/// <c>shubbak-wm</c> started perfectly happily and the two then fought over the same
/// desktop: two <c>WH_KEYBOARD_LL</c> hooks, so every binding ran twice; two layout
/// passes issuing contradictory <c>DeferWindowPos</c> batches; a CLI reaching whichever
/// daemon's accept loop won the race, so consecutive commands could land in different
/// processes; a bar mirroring one of the two; and on exit, one daemon un-concealing
/// windows the other still had recorded as concealed.
/// </para>
/// <para>
/// The named pipe cannot serve as the guard. It is created with
/// <c>MaxAllowedServerInstances</c>, which is precisely the flag that lets any number
/// of processes host the same name - and <c>IpcClient.IsServerRunning</c> only asks
/// whether a pipe of that name exists, which is equally true of one daemon and of two.
/// </para>
/// <para>
/// A mutex is the cheapest thing that actually answers the question: one call at
/// startup, held for the life of the process, never contended because only one holder
/// can exist. Nothing on any hot path.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private Mutex? _mutex;

    private SingleInstance(Mutex? mutex) => _mutex = mutex;

    /// <summary>Whether this process is the one window manager.</summary>
    public bool Held => _mutex is not null;

    /// <summary>
    /// Claims the right to manage this account's desktop.
    /// </summary>
    /// <param name="replace">
    /// Ask a running window manager to stand down first. This is the deliberate route
    /// for the case the guard would otherwise make awkward: an installed copy running
    /// while a build from source is started, which is exactly what someone working on
    /// Shubbak does all day.
    /// </param>
    /// <returns>
    /// A handle that must be disposed. <see cref="Held"/> is false when another window
    /// manager owns the desktop, and the reason has already been reported.
    /// </returns>
    public static SingleInstance TryAcquire(bool replace)
    {
        // Total by construction. This runs before Main installs the unhandled-exception
        // handler, so anything escaping here ends the process with a stack trace and no
        // window manager - a far worse outcome than the double-start it is guarding
        // against. Every failure below resolves to "carry on" or "refuse", never to
        // "throw".
        try
        {
            return Acquire(replace);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Wm, "the single-instance check failed", ex);

            ReportToUser(
                $"shubbak-wm: could not check whether one is already running: {ex.Message}",
                "hint: make sure no other shubbak-wm.exe is running, then try again.");

            return new SingleInstance(null);
        }
    }

    private static SingleInstance Acquire(bool replace)
    {
        if (replace && !AskTheRunningOneToStandDown()) return new SingleInstance(null);

        var mutex = new Mutex(initiallyOwned: false, IpcProtocol.InstanceMutexName);

        if (TryAcquire(mutex)) return new SingleInstance(mutex);

        mutex.Dispose();
        Refuse(replace);

        return new SingleInstance(null);
    }

    /// <summary>Takes the mutex, treating an abandoned one as free.</summary>
    /// <remarks>
    /// A daemon that was killed rather than asked to exit leaves the mutex abandoned,
    /// and waiting on it reports that by throwing rather than returning. Abandoned
    /// means the previous owner is gone, which is the same thing as available - and
    /// treating a crash as "someone else is running" would leave the user unable to
    /// start Shubbak again without a reboot, which is a far worse failure than the one
    /// being guarded against.
    /// </remarks>
    private static bool TryAcquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            Log.Warn(LogCategory.Wm, "the previous window manager did not exit cleanly; taking over");
            return true;
        }
    }

    /// <summary>
    /// Asks a running window manager to exit, and waits for it to let go.
    /// </summary>
    /// <remarks>
    /// Over IPC rather than by terminating anything, so the daemon that stands down
    /// saves its session and brings concealed windows back rather than stranding them.
    /// </remarks>
    private static bool AskTheRunningOneToStandDown()
    {
        if (!IpcClient.IsServerRunning())
        {
            // Nothing to replace. Not an error: --replace means "make sure I am the
            // one running", and if nobody else is, that is already true.
            return true;
        }

        try
        {
            var client = new IpcClient();

            try
            {
                // Connecting is a separate step, and skipping it is not a quiet
                // failure: SendAsync throws "Not connected" straight through, which
                // took --replace down with an unhandled exception rather than starting
                // a window manager.
                client.ConnectAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                client.SendAsync("command", "wm-exit").GetAwaiter().GetResult();
            }
            finally
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (
            ex is IOException or TimeoutException or UnauthorizedAccessException
                or InvalidOperationException or ObjectDisposedException)
        {
            // It may have been going down anyway, or the pipe may have closed as it
            // read the command. The wait below is the real test, so this is reported
            // and not treated as fatal.
            Log.Warn(LogCategory.Wm, $"could not ask the running window manager to exit: {ex.Message}");
        }

        return WaitForItToLetGo();
    }

    /// <summary>
    /// Waits for the outgoing daemon to release the mutex.
    /// </summary>
    /// <remarks>
    /// A clean exit saves the session and un-conceals every hidden window before the
    /// process ends, which takes tens of milliseconds and occasionally more. Starting
    /// before it finishes is how <c>--replace</c> would produce the very pair of
    /// daemons it exists to avoid.
    /// </remarks>
    private static bool WaitForItToLetGo()
    {
        var deadline = TimeSpan.FromSeconds(10);
        long started = Environment.TickCount64;

        while (Environment.TickCount64 - started < deadline.TotalMilliseconds)
        {
            using var probe = new Mutex(initiallyOwned: false, IpcProtocol.InstanceMutexName);

            bool free;

            try
            {
                free = probe.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                free = true;
            }

            if (free)
            {
                probe.ReleaseMutex();
                return true;
            }

            Thread.Sleep(50);
        }

        ReportToUser(
            "shubbak-wm: the running window manager did not exit within 10 seconds.",
            "hint: stop it with `shubbak wm-exit`, or end shubbak-wm.exe in Task Manager.");

        return false;
    }

    /// <summary>Explains that another window manager already owns this desktop.</summary>
    private static void Refuse(bool replace)
    {
        string running = Describe();

        if (replace)
        {
            ReportToUser(
                $"shubbak-wm: another window manager took the desktop first{running}.",
                "hint: this is a race between two starts; try again.");

            return;
        }

        ReportToUser(
            $"shubbak-wm: a window manager is already running{running}.",
            "hint: `shubbak wm-exit` stops it, or start this one with --replace to take over.");
    }

    /// <summary>Names the running daemon, when it can be found.</summary>
    /// <remarks>
    /// Best effort. Knowing the process id turns "it says one is running but I cannot
    /// see it" into something the user can act on, but not knowing it must not stop the
    /// refusal being reported.
    /// </remarks>
    private static string Describe()
    {
        try
        {
            System.Diagnostics.Process current = System.Diagnostics.Process.GetCurrentProcess();

            foreach (System.Diagnostics.Process other in
                     System.Diagnostics.Process.GetProcessesByName("shubbak-wm"))
            {
                using (other)
                {
                    if (other.Id != current.Id) return $" (process {other.Id})";
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }

        return string.Empty;
    }

    /// <summary>
    /// Puts a refusal where it will be read.
    /// </summary>
    /// <remarks>
    /// Through <see cref="ConsoleHost"/> because the daemon is a GUI-subsystem binary
    /// and has no console until it asks for one. A refusal nobody sees is
    /// indistinguishable from a launch that silently did nothing, which is the failure
    /// this whole class exists to end.
    /// </remarks>
    private static void ReportToUser(string message, string hint)
    {
        Log.Error(LogCategory.Wm, message);

        ConsoleHost.EnsureForError();

        try
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine(hint);

            if (ConsoleHost.OwnsConsole)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Press any key to close this window.");
                Console.ReadKey(intercept: true);
            }
        }
        catch (Exception)
        {
            // No console and no way to make one. The log has it.
        }
    }

    public void Dispose()
    {
        if (_mutex is null) return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owner, which can only happen if the mutex was abandoned and
            // reacquired underneath us. Disposing is still correct.
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
