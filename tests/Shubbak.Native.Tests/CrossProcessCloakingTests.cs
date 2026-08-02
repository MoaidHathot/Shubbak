using System.Diagnostics;
using Xunit.Abstractions;

namespace Shubbak.Native.Tests;

/// <summary>
/// Tests cloaking against a window owned by <em>another process</em>.
/// </summary>
/// <remarks>
/// <para>
/// These exist because <see cref="CloakingTests"/> did not. Every test there cloaks a
/// window created by the test process itself, and all ten pass - but Shubbak never
/// manages its own windows. It manages Firefox, Explorer and terminals, all owned by
/// other processes, and <c>DWMWA_CLOAK</c> is a per-process attribute: the compositor
/// returns <c>E_ACCESSDENIED</c> when a process sets it on a window it does not own.
/// </para>
/// <para>
/// The result was ten green tests certifying a feature that has never once worked in
/// production. Cloaking always failed, the fallback to <c>SW_HIDE</c> always ran, and
/// every concealed window was left unrecoverable. The tests measured the harness rather
/// than the product.
/// </para>
/// <para>
/// So these assert the behaviour that actually governs Shubbak: cross-process cloaking
/// is refused. Should a future Windows release relax that, these fail loudly and the
/// concealment strategy can be revisited - which is the point of writing them down.
/// </para>
/// </remarks>
public sealed class CrossProcessCloakingTests(ITestOutputHelper output)
{
    [Fact]
    public void CloakingAWindowOwnedByAnotherProcessIsRefused()
    {
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        Assert.False(
            Win32Window.Cloak(foreign.Handle),
            "cross-process cloaking succeeded. Windows may have changed its rules - " +
            "if so, revisit the concealment strategy, which assumes this fails.");
    }

    [Fact]
    public void ARefusedCloakLeavesTheWindowUntouched()
    {
        // The refusal is total, not partial: the attribute is never written. That is
        // what makes SW_HIDE the only concealment path that has ever run in production.
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        Win32Window.Cloak(foreign.Handle);

        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(foreign.Handle));
        Assert.True(Win32Window.IsVisible(foreign.Handle));
    }

    [Fact]
    public void TheSameCloakSucceedsOnAWindowWeOwn()
    {
        // The control. Without it a refusal could equally mean the test passed a bad
        // handle - exactly the ambiguity that let the same-process tests look like
        // evidence for something they never touched.
        using var ours = new TestWindow();

        Assert.True(Win32Window.Cloak(ours.Handle));
        Assert.Equal(Win32Window.CloakState.App, Win32Window.GetCloakState(ours.Handle));
    }

    [Fact]
    public void TheShellCanCloakAWindowTheCompositorWouldNot()
    {
        // The entire justification for talking to an undocumented interface. If this
        // ever fails, cloaking is not available and concealment must fall back.
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        Assert.False(Win32Window.Cloak(foreign.Handle), "precondition: DWMWA_CLOAK should be refused");

        Assert.True(
            Win32ApplicationView.Cloak(foreign.Handle),
            "the shell refused to cloak a foreign window; concealment has no working strategy");
    }

    [Fact]
    public void AShellCloakedWindowRemainsVisibleToWin32()
    {
        // The property that makes the window recoverable. A cloaked window still
        // passes IsWindowVisible, so a restarted Shubbak can still enumerate, adopt
        // and un-cloak it. SW_HIDE is what forfeits this.
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        Assert.True(Win32ApplicationView.Cloak(foreign.Handle), "the shell refused to cloak");

        Assert.True(Win32Window.IsVisible(foreign.Handle));
    }

    [Fact]
    public void AShellCloakReportsAsShellNotApp()
    {
        // Recorded because it drives the window filter. The shell performs the cloak,
        // so DWMWA_CLOAKED reads Shell - indistinguishable from a window on another
        // virtual desktop. Adoption therefore cannot treat "shell-cloaked" as "not
        // ours" outright; it has to reconcile against the session instead.
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        Assert.True(Win32ApplicationView.Cloak(foreign.Handle), "the shell refused to cloak");

        WaitForCloakState(foreign.Handle, Win32Window.CloakState.Shell);

        Assert.Equal(Win32Window.CloakState.Shell, Win32Window.GetCloakState(foreign.Handle));
    }

    [Fact]
    public void TheShellCanUncloakWhatItCloaked()
    {
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        Assert.True(Win32ApplicationView.Cloak(foreign.Handle), "the shell refused to cloak");

        Assert.True(Win32ApplicationView.Uncloak(foreign.Handle));

        WaitForCloakState(foreign.Handle, Win32Window.CloakState.None);

        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(foreign.Handle));
    }

    [Fact]
    public void RestoringOnShutdownUncloaksAWindowWeDoNotOwn()
    {
        // The committer's shutdown path used the per-process un-cloak, which cannot
        // touch a foreign window - so it counted windows as restored while leaving
        // every one of them cloaked. The count made the log look correct, which is
        // why it survived a clean exit going unnoticed.
        using var foreign = ForeignWindow.Acquire(output);
        if (foreign is null) return;

        var committer = new WindowCommitter();

        committer.Conceal(foreign.Handle);

        // Polled, because the compositor publishes DWMWA_CLOAKED asynchronously: the
        // shell has accepted the request by the time SetCloak returns, but the
        // attribute a reader sees lags it slightly.
        WaitForCloakState(foreign.Handle, Win32Window.CloakState.Shell);

        Assert.Equal(Win32Window.CloakState.Shell, Win32Window.GetCloakState(foreign.Handle));

        Assert.Equal(1, committer.RestoreAll());

        WaitForCloakState(foreign.Handle, Win32Window.CloakState.None);

        Assert.Equal(Win32Window.CloakState.None, Win32Window.GetCloakState(foreign.Handle));
    }

    private static void WaitForCloakState(nint handle, Win32Window.CloakState expected)
    {
        long deadline = Environment.TickCount64 + 3000;

        while (Environment.TickCount64 < deadline)
        {
            if (Win32Window.GetCloakState(handle) == expected) return;

            Thread.Sleep(25);
        }
    }
}

/// <summary>
/// A top-level window owned by some process other than this one.
/// </summary>
/// <remarks>
/// Prefers spawning <c>winver.exe</c>: a classic unpackaged Win32 app on every Windows
/// install, one ordinary top-level window, closes cleanly. A packaged app such as
/// Windows 11's Notepad would be unusable, because the shell cloaks those itself and
/// that is the very state under test.
/// <para>
/// Falls back to borrowing a window that already exists. Borrowing is safe precisely
/// because of the result being asserted - a refused cloak does not touch the window -
/// and the cloak is reversed regardless, so the unexpected case is momentary too.
/// </para>
/// </remarks>
internal sealed class ForeignWindow : IDisposable
{
    /// <summary>How long to let the shell notice a newly created window.</summary>
    private const int SettleForShellMs = 3000;

    private static bool WaitForShellToNotice(nint handle)
    {
        long deadline = Environment.TickCount64 + SettleForShellMs;

        while (Environment.TickCount64 < deadline)
        {
            if (Win32ApplicationView.HasView(handle)) return true;

            Thread.Sleep(25);
        }

        return false;
    }

    private readonly Process? _spawned;

    private ForeignWindow(nint handle, Process? spawned)
    {
        Handle = handle;
        _spawned = spawned;
    }

    public nint Handle { get; }

    /// <summary>Obtains a foreign window, or null if the machine offers none.</summary>
    /// <remarks>
    /// Null rather than an exception, because a test that cannot construct its subject
    /// has proved nothing, and reporting that as a failure would mislead in exactly the
    /// way that caused this file to be written. The reason is always printed.
    /// </remarks>
    public static ForeignWindow? Acquire(ITestOutputHelper output)
    {
        ForeignWindow? spawned = TrySpawn();
        if (spawned is not null)
        {
            output.WriteLine($"spawned winver.exe, window 0x{spawned.Handle:X} " +
                             $"owned by pid {Win32Window.GetProcessId(spawned.Handle)} " +
                             $"(ours is {Environment.ProcessId})");
            return spawned;
        }

        ForeignWindow? borrowed = TryBorrow();
        if (borrowed is not null)
        {
            output.WriteLine($"borrowed existing window 0x{borrowed.Handle:X} " +
                             $"owned by pid {Win32Window.GetProcessId(borrowed.Handle)} " +
                             $"(ours is {Environment.ProcessId})");
            return borrowed;
        }

        output.WriteLine(
            "SKIPPED: no window owned by another process could be found or created, " +
            "so cross-process cloaking was not exercised.");

        return null;
    }

    private static ForeignWindow? TrySpawn()
    {
        Process? process = null;

        try
        {
            process = Process.Start(new ProcessStartInfo("winver.exe") { UseShellExecute = false });
            if (process is null) return null;

            // Polled rather than WaitForInputIdle, which returns once the message loop
            // runs - that can precede the window existing.
            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(50);
                process.Refresh();

                if (process.HasExited) break;

                nint handle = process.MainWindowHandle;
                if (handle != 0 && Win32Window.IsVisible(handle))
                {
                    // The shell catalogues windows into its application-view
                    // collection asynchronously, and a window a few milliseconds old
                    // is often not in it yet - GetViewForHwnd answers
                    // TYPE_E_ELEMENTNOTFOUND, and cloaking would fall back.
                    //
                    // Waited for rather than slept through, because a fixed delay is
                    // a guess that is either too short on a loaded machine or wasted
                    // on an idle one. This is test setup, not a workaround: Shubbak
                    // never conceals a window within milliseconds of it appearing.
                    if (!WaitForShellToNotice(handle)) break;

                    ForeignWindow result = new(handle, process);
                    process = null;
                    return result;
                }
            }

            return null;
        }
        catch (Exception)
        {
            // Absent on Server Core, and blockable by policy. Neither is a failure.
            return null;
        }
        finally
        {
            // Non-null only when we are abandoning it, since success clears the local.
            Kill(process);
        }
    }

    private static ForeignWindow? TryBorrow()
    {
        uint ours = (uint)Environment.ProcessId;

        foreach (nint handle in Win32Window.EnumerateTopLevel())
        {
            if (!Win32Window.IsVisible(handle)) continue;
            if (Win32Window.GetProcessId(handle) == ours) continue;

            // Already cloaked by the shell, so it could not evidence a refusal.
            if (Win32Window.GetCloakState(handle) != Win32Window.CloakState.None) continue;

            return new ForeignWindow(handle, spawned: null);
        }

        return null;
    }

    private static void Kill(Process? process)
    {
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // Already gone, or beyond our reach. Nothing useful either way.
        }

        process.Dispose();
    }

    public void Dispose()
    {
        // Unconditional and by both routes: if either cloak succeeded, this is what
        // keeps a borrowed window from being left invisible on the user's desktop.
        Win32Window.Uncloak(Handle);
        Win32ApplicationView.Uncloak(Handle);

        Kill(_spawned);
    }
}
