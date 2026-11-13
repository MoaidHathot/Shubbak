using System.Diagnostics;

namespace Shubbak.Core.Processes;

/// <summary>A script worker and the resource that owns its process family.</summary>
/// <remarks>
/// The portable fallback stops a process tree through System.Diagnostics. A native
/// host can supply an ownership handle whose disposal stops the family without
/// enumerating unrelated processes. Ordinary application launches are not workers.
/// </remarks>
public sealed class WorkerProcess(Process process, IDisposable? ownership = null) : IDisposable
{
    public Process Process { get; } = process ?? throw new ArgumentNullException(nameof(process));

    public static WorkerProcess Start(ProcessStartInfo startInfo) =>
        new(System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("The worker did not start."));

    public void Stop()
    {
        if (ownership is not null) ownership.Dispose();
        else Process.Kill(entireProcessTree: true);
    }

    public void Dispose()
    {
        ownership?.Dispose();
        Process.Dispose();
    }
}
