using System.Diagnostics;
using Shubbak.Native;

namespace Shubbak.TestWindow;

/// <summary>A disposable parent used to verify kernel cleanup after abrupt exit.</summary>
internal static class WorkerOwner
{
    internal static int Run()
    {
        using var child = Win32WorkerProcess.Start(new ProcessStartInfo(Environment.ProcessPath!, "--worker-leaf")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });
        string? pid = child.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        if (pid is null) return 1;
        Console.WriteLine(pid);
        // EOF also cleans up if the test host disappears. The test deliberately
        // kills this parent before Dispose can run, to exercise job-handle closure.
        Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        return 0;
    }
}
