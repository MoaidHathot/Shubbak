using System.Diagnostics;
using Shubbak.Core.Processes;
using Shubbak.TestWindow;

namespace Shubbak.Native.Tests;

public sealed class WindowManagerGuardTests
{
    [Fact]
    public async Task AProcessThatExitedAfterEnumerationNoLongerBlocksTheDesktop()
    {
        using WorkerProcess worker = Win32WorkerProcess.Start(new ProcessStartInfo(
            Path.Combine(AppContext.BaseDirectory, "Shubbak.TestWindow.exe"), "--worker-leaf")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        });
        await worker.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Keep the enumerated component alive across termination, exactly the stale
        // snapshot that a guard based on GetProcessesByName(...).Length misclassifies.
        using Process observed = Process.GetProcessById(worker.Process.Id);
        _ = observed.SafeHandle;
        Process[] snapshot = [observed];
        Assert.Throws<InvalidOperationException>(() => WindowManagerGuard.RequireStopped("still running", snapshot));

        worker.Stop();
        await worker.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(observed.HasExited);
        WindowManagerGuard.RequireStopped("already exited", snapshot);
    }
}
