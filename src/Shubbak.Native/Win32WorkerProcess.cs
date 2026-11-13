using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Shubbak.Core.Processes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.JobObjects;

namespace Shubbak.Native;

/// <summary>Starts a script worker inside its own kill-on-close Windows job.</summary>
public static class Win32WorkerProcess
{
    public static unsafe WorkerProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute) throw new ArgumentException("A worker must be started directly.", nameof(startInfo));

        var job = PInvoke.CreateJobObject(null, null);
        if (job.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error);
        }

        var process = new Process { StartInfo = startInfo };
        bool started = false;
        bool suspended = startInfo.StartSuspended;
        try
        {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = default;
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (!PInvoke.SetInformationJobObject(new HANDLE(job.DangerousGetHandle()), JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION)))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // .NET 11 preserves the initial thread handle for Resume. Assignment
            // must precede the child's first instruction, or an early grandchild can
            // escape ownership and survive a reload or a crashed companion.
            startInfo.StartSuspended = true;
            started = process.Start();
            if (!started) throw new InvalidOperationException("The worker did not start.");
            if (!PInvoke.AssignProcessToJobObject(job, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!suspended) process.SafeHandle.Resume();

            // The handle is non-inheritable. Only this owner keeps the job alive,
            // and each worker has a separate job so stopping one leaves others alone.
            return new WorkerProcess(process, job);
        }
        catch
        {
            if (started)
            {
                try { process.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            }
            process.Dispose();
            job.Dispose();
            throw;
        }
        finally { startInfo.StartSuspended = suspended; }
    }
}
