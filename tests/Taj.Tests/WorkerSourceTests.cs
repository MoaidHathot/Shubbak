using System.Diagnostics;
using Shubbak.Native;
using Taj.Core.Sources;

namespace Taj.Tests;

public sealed class WorkerSourceTests
{
    [Fact]
    public async Task AJobOwnedSourcePublishesAndStopsItsGrandchild()
    {
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = new ProcessSource("worker",
            "cmd.exe /c powershell.exe -NoProfile -NonInteractive -Command \"$PID; Start-Sleep -Seconds 90\"",
            startProcess: Win32WorkerProcess.Start);
        source.Changed += value => { if (int.TryParse(value.Value, out int pid)) ready.TrySetResult(pid); };
        source.Start();
        int childId = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using Process child = Process.GetProcessById(childId);
        source.Dispose();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(child.HasExited);
    }
}
