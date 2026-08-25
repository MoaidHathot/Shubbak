using System.Diagnostics;
using Shubbak.Native;

namespace Shubbak.Native.Tests;

/// <summary>
/// The per-process identity cache behind the window filter.
/// </summary>
/// <remarks>
/// <para>
/// Reading a window's executable path and integrity level costs two
/// <c>OpenProcess</c> calls, and both answers describe the process rather than the
/// window. The filter asks per window, so a browser with twenty windows was paying
/// twenty times for one answer.
/// </para>
/// <para>
/// Caching by process id is normally unsafe, because Windows reuses ids. The cache
/// avoids that by keeping a handle open: an id cannot be reused while a handle to
/// the process exists, so an entry cannot come to describe a different process than
/// the one it was built from. These tests pin that property, because it is the only
/// thing making the cache correct and it is invisible from the outside.
/// </para>
/// </remarks>
public sealed class ProcessIdentityTests
{
    [Fact]
    public void TheIdentityOfThisProcessIsReadable()
    {
        string? path = Win32Window.GetProcessPath((uint)Environment.ProcessId);

        Assert.NotNull(path);
        Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path), $"reported a path that is not on disk: {path}");
    }

    [Fact]
    public void RepeatedLookupsReturnTheVerySameString()
    {
        Win32Window.ForgetProcessIdentities();

        string? first = Win32Window.GetProcessPath((uint)Environment.ProcessId);
        string? second = Win32Window.GetProcessPath((uint)Environment.ProcessId);

        // Reference equality is the assertion, not value equality. Each uncached read
        // builds a fresh string from a stack buffer, so two reads of the same process
        // can only be the same instance if the second one never happened.
        Assert.Same(first, second);
    }

    [Fact]
    public void TheAnswerOutlivesTheProcessItDescribes()
    {
        Win32Window.ForgetProcessIdentities();

        using Process child = StartAWaitingProcess();
        var processId = (uint)child.Id;

        string? whileAlive = Win32Window.GetProcessPath(processId);
        Assert.NotNull(whileAlive);

        child.Kill();
        Assert.True(child.WaitForExit(10_000), "the spawned process did not exit");

        // The demonstration. Asking again after the process is gone still answers,
        // and answers with the identical instance - so the entry was kept, and the id
        // it is filed under cannot meanwhile have been handed to something else.
        //
        // Without the retained handle this would either return null, because
        // OpenProcess on a dead id fails, or eventually return a different
        // executable, because the id was recycled.
        string? afterDeath = Win32Window.GetProcessPath(processId);

        Assert.Same(whileAlive, afterDeath);
    }

    [Fact]
    public void ForgettingReleasesEveryProcess()
    {
        _ = Win32Window.GetProcessPath((uint)Environment.ProcessId);
        Assert.True(Win32Window.RememberedProcessCount > 0);

        Win32Window.ForgetProcessIdentities();

        // Not merely tidiness. Every entry holds a live process handle, so failing to
        // release them leaks handles and keeps dead process ids reserved for as long
        // as the daemon runs.
        Assert.Equal(0, Win32Window.RememberedProcessCount);
    }

    [Fact]
    public void ProcessZeroIsNeverRemembered()
    {
        Win32Window.ForgetProcessIdentities();

        Assert.Null(Win32Window.GetProcessPath(0));
        Assert.False(Win32Window.IsElevated(0));

        // GetWindowThreadProcessId reports zero for a window that has just died, and
        // that value must not consume a cache entry or, worse, acquire one that a
        // real process later matches.
        Assert.Equal(0, Win32Window.RememberedProcessCount);
    }

    [Fact]
    public void AProcessThatCannotBeOpenedIsNotRemembered()
    {
        Win32Window.ForgetProcessIdentities();

        // Process 4 is System. It is protected, so PROCESS_QUERY_LIMITED_INFORMATION
        // is refused and there is no handle to pin the id with - which means the
        // answer must not be cached, because an uncacheable answer is the one case
        // where a later id reuse could not be ruled out.
        _ = Win32Window.GetProcessPath(4);

        Assert.Equal(0, Win32Window.RememberedProcessCount);
    }

    [Fact]
    public void ThisProcessIsNotAboveItself()
    {
        // Elevation is asked as "is this process above me?", so the answer about
        // ourselves is false however the test host was launched. A true here would
        // mean the filter refuses to manage Shubbak's own windows.
        Assert.False(Win32Window.IsElevated((uint)Environment.ProcessId));
    }

    /// <summary>Starts a process that will wait until it is killed.</summary>
    /// <remarks>
    /// A console process with its input redirected and never written to. Chosen over
    /// spawning a GUI application because this test cares only about process
    /// identity, and needs no window, no message loop and no desktop.
    /// </remarks>
    private static Process StartAWaitingProcess()
    {
        Process? child = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        });

        Assert.NotNull(child);
        return child;
    }
}
