namespace Shubbak.Native.Tests;

/// <summary>
/// The one desktop, held by whichever test assembly is using it.
/// </summary>
/// <remarks>
/// <para>
/// Two test projects create real windows on the desktop and assert what Windows does
/// with them: this one, whose windows must not be touched by any window manager, and
/// the end-to-end project, which starts a window manager of its own that manages
/// every <c>winver</c> it can see. <c>dotnet test</c> runs the two projects' hosts at
/// the same time, so one in three full runs found the end-to-end daemon up when a
/// test here looked - and refused, as it should when a window manager is running, with
/// a message blaming the user's. Nine to twenty-five failures a run, all of them the
/// guard doing its job against the wrong daemon.
/// </para>
/// <para>
/// A named mutex, system-wide, held by this assembly from its first window to the end
/// of the process and by the end-to-end test around each daemon it starts. Whichever
/// host reaches the desktop second waits. Nothing is skipped and nothing is retried:
/// the suites still fail loudly against a window manager that is really the user's,
/// because this says nothing about those - only about each other.
/// </para>
/// <para>
/// A mutex rather than a semaphore because a host that dies holding a mutex abandons
/// it, and the next waiter is told and gets it - whereas a semaphore's count is simply
/// gone with the process, and the other suite would wait out its patience on every
/// run after a crash. A mutex belongs to the thread that took it, so it is taken and
/// given back on a thread of its own that lives as long as the hold.
/// </para>
/// <para>
/// Held for the whole run rather than per window, because this assembly's windows are
/// many and short-lived and a daemon that got in between two of them would be exactly
/// the race this exists to end. The end-to-end project's holds are the long ones, a
/// few seconds each, and there are four of them.
/// </para>
/// </remarks>
internal static class DesktopLease
{
    /// <summary>Shared with <c>Shubbak.EndToEnd.Tests</c>; the same string in both.</summary>
    private const string Name = @"Local\shubbak-tests-the-desktop";

    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);
    private static readonly Lock s_gate = new();
    private static ManualResetEventSlim? s_letGo;

    /// <summary>Takes the desktop for this process, waiting for the other assembly if it has it. Idempotent.</summary>
    public static void Acquire()
    {
        lock (s_gate)
        {
            if (s_letGo is not null) return;

            var taken = new ManualResetEventSlim(false);
            var letGo = new ManualResetEventSlim(false);
            Exception? failure = null;

            // The holder: takes the mutex and keeps its thread alive until the process
            // is leaving, then gives it back from the thread that owns it.
            var holder = new Thread(() =>
            {
                try
                {
                    using var mutex = new Mutex(initiallyOwned: false, Name);

                    try
                    {
                        if (!mutex.WaitOne(Patience))
                        {
                            failure = new TimeoutException(
                                $"waited {Patience.TotalMinutes:F0} min for the end-to-end tests to give the desktop back and they did not.");
                            return;
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        // The other host died holding it. It is ours now, which is what
                        // was asked for; the desktop it left is whatever it left.
                    }

                    taken.Set();
                    letGo.Wait();
                    mutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    taken.Set();
                }
            })
            {
                Name = "Shubbak test desktop lease",
                IsBackground = true,
            };

            holder.Start();
            taken.Wait();

            if (failure is not null) throw failure;

            s_letGo = letGo;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => letGo.Set();
        }
    }
}
