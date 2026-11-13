using System.ComponentModel;
using System.Diagnostics;

namespace Shubbak.TestWindow;

/// <summary>Refuses desktop tests while a window manager can interfere with them.</summary>
public static class WindowManagerGuard
{
    /// <param name="reason">What the caller needs the desktop for.</param>
    /// <param name="snapshot">An optional already-enumerated snapshot; its caller retains ownership.</param>
    public static void RequireStopped(string reason, IReadOnlyList<Process>? snapshot = null)
    {
        bool ownsSnapshot = snapshot is null;
        snapshot ??= Process.GetProcessesByName("shubbak-wm");
        try
        {
            List<string> running = [];
            foreach (Process process in snapshot)
            {
                // Enumeration is a snapshot, not proof that the process can still
                // manage windows. Another test can have finished its daemon before
                // this guard reaches an entry that still names it.
                string? uncertainty = null;
                try { if (process.HasExited) continue; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // An inaccessible process is still a reason to refuse the test.
                    uncertainty = $"; exit state unavailable: {ex.Message}";
                }

                running.Add(Describe(process) + uncertainty);
            }

            if (running.Count > 0)
            {
                using Process self = Process.GetCurrentProcess();
                throw new InvalidOperationException($"{reason} Test host pid {self.Id}, session {self.SessionId}. " +
                    $"Found: {string.Join("; ", running)}");
            }
        }
        finally
        {
            if (ownsSnapshot)
                foreach (Process process in snapshot) process.Dispose();
        }
    }

    private static string Describe(Process process)
    {
        string identity = $"pid {process.Id}";
        try { identity += $", session {process.SessionId}, started {process.StartTime:O}"; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            identity += $", lifetime details unavailable: {ex.Message}";
        }

        try { return $"{identity}, from {process.MainModule?.FileName ?? "(unknown path)"}"; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return $"{identity}, path unavailable: {ex.Message}";
        }
    }
}
