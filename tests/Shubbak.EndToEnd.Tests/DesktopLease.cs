namespace Shubbak.EndToEnd.Tests;

/// <summary>
/// The one desktop, held while a window manager of this test's own is on it.
/// </summary>
/// <remarks>
/// <para>
/// The other half of <c>Shubbak.Native.Tests</c>' <c>DesktopLease</c>, and the reason it
/// exists: this test starts a daemon that manages every <c>winver</c> it can see, the
/// native tests spawn a <c>winver</c> and assert what the shell does with it, and
/// <c>dotnet test</c> runs both hosts at once. Their guard against a running window
/// manager found this one, one full run in three, and refused with a message blaming
/// the user's.
/// </para>
/// <para>
/// Taken per test, from before the daemon starts until it has gone, on a thread of
/// its own because a mutex belongs to the thread that took it and xunit's is not
/// promised to be the same one at the end. A host that dies holding it abandons it,
/// and the next waiter is told and gets it, which is why this is a mutex and not a
/// semaphore. The same name string is in both projects, by hand; they share no
/// library and forty lines twice is cheaper than a project for them.
/// </para>
/// </remarks>
internal sealed class DesktopLease : IDisposable
{
    private const string Name = @"Local\shubbak-tests-the-desktop";
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private readonly ManualResetEventSlim _letGo = new(false);
    private readonly Thread _holder;

    private DesktopLease()
    {
        var taken = new ManualResetEventSlim(false);
        Exception? failure = null;

        _holder = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, Name);

                try
                {
                    if (!mutex.WaitOne(Patience))
                    {
                        failure = new TimeoutException(
                            $"waited {Patience.TotalMinutes:F0} min for the native tests to give the desktop back and they did not.");
                        return;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The other host died holding it; it is ours now.
                }

                taken.Set();
                _letGo.Wait();
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

        _holder.Start();
        taken.Wait();
        taken.Dispose();

        if (failure is not null)
        {
            _letGo.Dispose();
            throw failure;
        }
    }

    /// <summary>Takes the desktop, waiting for the native tests to give it back if they have it.</summary>
    public static DesktopLease Take() => new();

    /// <summary>Gives the desktop back.</summary>
    public void Dispose()
    {
        _letGo.Set();
        _holder.Join(TimeSpan.FromSeconds(5));
        _letGo.Dispose();
    }
}
