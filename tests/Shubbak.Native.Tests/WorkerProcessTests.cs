using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Shubbak.Core.Processes;

namespace Shubbak.Native.Tests;

public sealed class WorkerProcessTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);
    private static ProcessStartInfo Sleeper() => new("cmd.exe",
        "/c powershell.exe -NoProfile -NonInteractive -Command \"$PID; Start-Sleep -Seconds 90\"")
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingOrDisposingAWorkerEndsItsGrandchild(bool dispose)
    {
        using WorkerProcess worker = Win32WorkerProcess.Start(Sleeper());
        using Process root = Process.GetProcessById(worker.Process.Id);
        int pid = int.Parse((await worker.Process.StandardOutput.ReadLineAsync().WaitAsync(Budget))!, CultureInfo.InvariantCulture);
        using Process grandchild = Process.GetProcessById(pid);
        if (dispose) worker.Dispose();
        else worker.Stop();
        await root.WaitForExitAsync().WaitAsync(Budget);
        await grandchild.WaitForExitAsync().WaitAsync(Budget);
        Assert.True(root.HasExited);
        Assert.True(grandchild.HasExited);
    }

    [Fact]
    public async Task IndependentWorkersDoNotShareAKillScope()
    {
        using WorkerProcess first = Win32WorkerProcess.Start(Sleeper());
        using WorkerProcess second = Win32WorkerProcess.Start(Sleeper());
        await first.Process.StandardOutput.ReadLineAsync().WaitAsync(Budget);
        await second.Process.StandardOutput.ReadLineAsync().WaitAsync(Budget);
        first.Stop();
        await first.Process.WaitForExitAsync().WaitAsync(Budget);
        Assert.False(second.Process.HasExited);
        second.Stop();
        await second.Process.WaitForExitAsync().WaitAsync(Budget);
    }

    [Fact]
    public async Task ARequestedSuspendedStartDoesNotExecuteUntilResumed()
    {
        ProcessStartInfo info = Sleeper();
        info.StartSuspended = true;
        using WorkerProcess worker = Win32WorkerProcess.Start(info);
        Task<string?> output = worker.Process.StandardOutput.ReadLineAsync();
        await Task.Delay(100);
        Assert.False(output.IsCompleted);
        worker.Process.SafeHandle.Resume();
        Assert.True(int.TryParse(await output.WaitAsync(Budget), out _));
        worker.Stop();
        await worker.Process.WaitForExitAsync().WaitAsync(Budget);
    }

    [Fact]
    public void FailedLaunchesReleaseTheirJobHandles()
    {
        var info = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), $"missing-worker-{Guid.NewGuid():N}.exe")) { UseShellExecute = false };
        for (int i = 0; i < 3; i++) Assert.Throws<Win32Exception>(() => Win32WorkerProcess.Start(info));
        using Process self = Process.GetCurrentProcess();
        int before = self.HandleCount;
        for (int i = 0; i < 20; i++) Assert.Throws<Win32Exception>(() => Win32WorkerProcess.Start(info));
        self.Refresh();
        Assert.InRange(self.HandleCount - before, -10, 2);
        Assert.False(info.StartSuspended);
    }

    [Fact]
    public async Task AbruptOwnerDeathAlsoEndsItsWorker()
    {
        using Process owner = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Shubbak.TestWindow.exe"), "--worker-owner")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        Task<string> errors = owner.StandardError.ReadToEndAsync();
        Process? child = null;
        try
        {
            string? line = await owner.StandardOutput.ReadLineAsync().WaitAsync(Budget);
            Assert.True(int.TryParse(line, out int pid), $"worker parent did not become ready: {line}");
            child = Process.GetProcessById(pid);
            owner.Kill(); // Deliberately only the parent, with no managed cleanup.
            await owner.WaitForExitAsync().WaitAsync(Budget);
            await child.WaitForExitAsync().WaitAsync(Budget);
            Assert.True(child.HasExited, "the OS did not release the parent's worker family");
        }
        finally
        {
            if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync().WaitAsync(Budget); }
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().WaitAsync(Budget); }
                child.Dispose();
            }
            await errors.WaitAsync(Budget);
        }
    }
}
