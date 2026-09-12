using System.Diagnostics;
using Taj.Core.Sources;

namespace Taj.Core.Tests;

/// <summary>
/// The process source and the program it runs.
/// </summary>
/// <remarks>
/// The program is the bar's own worker, not a peer: nothing but the source knows it is
/// there, so nothing else can stop it. Disposing the source - which happens on every
/// configuration reload as well as at exit - used to cancel the reader and drop the
/// handle, and leave the program running. A bar reloaded five times had six copies of
/// each script.
/// </remarks>
public sealed class ProcessSourceTests
{
    /// <summary>
    /// cmd.exe runs PowerShell, which prints its own pid and then sleeps.
    /// </summary>
    /// <remarks>
    /// Two levels deliberately. A <c>kind="command"</c> script is nearly always
    /// <c>pwsh -File x.ps1</c> or <c>cmd /c something</c>, and on Windows ending a
    /// parent leaves its children running - so it is the grandchild's survival that
    /// says whether the source stops what it started, not the child's.
    /// </remarks>
    private const string Sleeper =
        "cmd.exe /c powershell.exe -NoProfile -NonInteractive -Command \"$PID; Start-Sleep -Seconds 120\"";

    [Fact]
    public void DisposingStopsTheProgramItStarted()
    {
        int pid = 0;
        using var reported = new ManualResetEventSlim();

        var source = new ProcessSource("probe", Sleeper);
        source.Changed += s =>
        {
            if (int.TryParse(s.Value, out int parsed))
            {
                pid = parsed;
                reported.Set();
            }
        };

        try
        {
            source.Start();

            Assert.True(reported.Wait(TimeSpan.FromSeconds(30)), "the program never reported its pid");
            Assert.True(IsRunning(pid), $"pid {pid} should be running before the source is disposed");

            var elapsed = Stopwatch.StartNew();
            source.Dispose();
            elapsed.Stop();

            Assert.True(WaitUntilGone(pid, TimeSpan.FromSeconds(10)),
                $"pid {pid} is still running {WaitUntilGoneTimeout()} after the source was disposed");

            // Not a benchmark; a guard. Dispose runs on the bar's thread, on every
            // reload, and a source that waits out its program would be a bar that hangs.
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
                $"dispose took {elapsed.Elapsed.TotalMilliseconds:F0} ms");
        }
        finally
        {
            // A failing run must not leave the sleeper behind on the developer's machine.
            if (pid != 0) TryKill(pid);
        }
    }

    [Fact]
    public void AProgramThatExitsIsStartedAgain()
    {
        // The documented behaviour, which the stop-on-dispose change must not cost:
        // the common failure is a script with a bug, and a permanently blank widget
        // gives the user nothing to go on. %TIME% differs between runs a tenth of a
        // second apart, so each restart is a new value and a new Changed.
        int changes = 0;
        using var twice = new ManualResetEventSlim();

        using var source = new ProcessSource("probe", "cmd.exe /c echo %TIME%", TimeSpan.FromMilliseconds(100));
        source.Changed += _ =>
        {
            if (Interlocked.Increment(ref changes) >= 2) twice.Set();
        };

        source.Start();

        Assert.True(twice.Wait(TimeSpan.FromSeconds(30)), $"the program was started {changes} time(s); expected a restart");
    }

    private static string WaitUntilGoneTimeout() => "10 s";

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool WaitUntilGone(int pid, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();

        while (IsRunning(pid))
        {
            if (elapsed.Elapsed > timeout) return false;
            Thread.Sleep(50);
        }

        return true;
    }

    private static void TryKill(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
