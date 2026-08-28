using Shubbak.Core.Diagnostics;

namespace Shubbak.Native;

/// <summary>
/// A claim on being the only process of its kind.
/// </summary>
/// <remarks>
/// <para>
/// The mechanics only. What to do when the claim fails is a policy decision that
/// differs by program - two window managers fight over the desktop and must never both
/// run, whereas a bar that cannot tell whether another exists is better off drawing
/// than refusing - so the outcome is reported and the caller decides.
/// </para>
/// <para>
/// A mutex rather than a search for a process by name. A name can be shared with
/// something unrelated, a process can be renamed, and enumerating processes is a
/// racy answer to a question that has an exact one. One call at startup, held for the
/// life of the process, never contended because only one holder can exist.
/// </para>
/// <para>
/// It counts <em>processes</em>, not calls. A Windows mutex is owned by a thread and
/// the owning thread may take it again as often as it likes, so claiming the same name
/// twice on one thread succeeds both times. That is the right behaviour here - every
/// caller claims once, at startup - but it means this cannot be used to make something
/// happen only once within a process.
/// </para>
/// </remarks>
public sealed class SingleInstanceLock : IDisposable
{
    private Mutex? _mutex;

    private SingleInstanceLock(Mutex? mutex, bool certain)
    {
        _mutex = mutex;
        Certain = certain;
    }

    /// <summary>Whether this process holds the claim.</summary>
    public bool Held => _mutex is not null;

    /// <summary>
    /// Whether the answer can be relied on.
    /// </summary>
    /// <remarks>
    /// False when the check itself failed rather than when it came back negative. The
    /// two demand opposite responses and reporting them as one value would force every
    /// caller to guess: "another one is running" is a reason to stop, and "I could not
    /// find out" usually is not.
    /// </remarks>
    public bool Certain { get; }

    /// <summary>Claims the name, or reports why not.</summary>
    /// <param name="name">
    /// A kernel object name, already scoped to an account - see
    /// <c>IpcProtocol.InstanceMutexNameFor</c>.
    /// </param>
    /// <param name="category">Which subsystem to log under.</param>
    public static SingleInstanceLock Claim(string name, LogCategory category = LogCategory.Wm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Total by construction. This runs before most of a program exists, so an
        // exception escaping here ends the process with a stack trace instead of
        // starting - a worse outcome than either answer it could have given.
        try
        {
            var mutex = new Mutex(initiallyOwned: false, name);

            if (Take(mutex, category)) return new SingleInstanceLock(mutex, certain: true);

            mutex.Dispose();
            return new SingleInstanceLock(null, certain: true);
        }
        catch (Exception ex)
        {
            Log.Error(category, $"could not check whether another instance holds {name}", ex);
            return new SingleInstanceLock(null, certain: false);
        }
    }

    /// <summary>Takes the mutex, treating an abandoned one as free.</summary>
    /// <remarks>
    /// A process that was killed rather than asked to exit leaves the mutex abandoned,
    /// and waiting on it reports that by throwing rather than by returning. Abandoned
    /// means the previous owner is gone, which is the same thing as available - and
    /// treating a crash as "someone else is running" would leave the user unable to
    /// start the program again without a reboot, which is a far worse failure than the
    /// one being guarded against.
    /// </remarks>
    private static bool Take(Mutex mutex, LogCategory category)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            Log.Warn(category, "the previous instance did not exit cleanly; taking over");
            return true;
        }
    }

    /// <summary>Whether anything currently holds a claim on the name.</summary>
    /// <remarks>
    /// Asked without keeping it, for the caller that wants to wait for a name to become
    /// free rather than to own it. Null when the question could not be answered, which
    /// is not the same as "no".
    /// </remarks>
    public static bool? IsHeldByAnyone(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            using var probe = new Mutex(initiallyOwned: false, name);

            bool free;

            try
            {
                free = probe.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                free = true;
            }

            if (free) probe.ReleaseMutex();

            return !free;
        }
        catch (Exception)
        {
            return null;
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
