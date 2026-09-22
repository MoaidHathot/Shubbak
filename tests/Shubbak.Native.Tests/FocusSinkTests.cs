using Shubbak.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native.Tests;

/// <summary>
/// The window that holds the keyboard while an empty workspace is displayed, measured
/// against what Windows actually does when the active window goes away.
/// </summary>
/// <remarks>
/// <para>
/// The bug this exists for: switch to an empty workspace on one monitor, open a
/// launcher, start an application, and find it on the other monitor. The launcher
/// hides itself, Windows activates the window that was active before it, and that
/// window is on the other display. Parking the foreground on the desktop first was
/// the previous fix and does not help, because the desktop is never the window Windows
/// chooses - <see cref="TheDesktopIsNotHandedTheForegroundBack"/> pins that down so
/// nobody rediscovers it.
/// </para>
/// <para>
/// Real windows on real threads, one queue each, activated and hidden from their own
/// threads the way applications do it: the choice of fallback is made from the queue
/// of the thread whose window is going away, so a stand-in that did everything from
/// the test thread would be measuring something else. The sink itself lives on a
/// pumping thread of its own, as it does in the daemon.
/// </para>
/// <para>
/// These take the foreground for real, briefly, and give it back at the end of each
/// test. Like every test in this project they refuse to run beside a live window
/// manager; and they are skipped, saying so, on a window station where no process can
/// be given the foreground at all - see <see cref="FactOnAnInteractiveDesktopAttribute"/>.
/// </para>
/// </remarks>
public sealed class FocusSinkTests
{
    /// <summary>Where the sink is put: the top-left of the primary display.</summary>
    private static readonly Rect Primary = new(0, 0, 0, 0);

    private const WINDOW_STYLE AppStyle =
        WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_SYSMENU;

    /// <summary>
    /// The reported bug, and the reason for the class: a launcher that took the
    /// foreground from the sink hands it back to the sink when it closes.
    /// </summary>
    /// <remarks>
    /// "Other" stands for the window displayed on the other monitor: visible, and
    /// the window that had the foreground before the workspace was switched away
    /// from it. Without the sink, this is where the foreground would go.
    /// </remarks>
    [TheoryOnAnInteractiveDesktop]
    [InlineData(false)]
    [InlineData(true)]
    public void TheLauncherHandsTheForegroundBackToTheSink(bool destroyed)
    {
        using var restore = new ForegroundGuard();
        using var other = new TestWindow("Other", style: AppStyle);
        Assert.True(other.Activate(), Desktop.WhyNotInFront("\"Other\""));

        using var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        using var launcher = new TestWindow("Launcher", style: AppStyle);
        Assert.True(launcher.Activate(), Desktop.WhyNotInFront("the launcher"));
        Assert.False(host.HoldsForeground);

        if (destroyed) launcher.Destroy(); else launcher.Hide();

        TestWindow.PumpUntil(() => host.HoldsForeground, 1000);
        Assert.True(host.HoldsForeground, "the foreground went to " + Describe(Win32Window.GetForeground(), other));
    }

    /// <summary>
    /// The tool-window shaped launcher - the command palette is one - is no different.
    /// </summary>
    [FactOnAnInteractiveDesktop]
    public void AToolWindowLauncherHandsItBackToo()
    {
        using var restore = new ForegroundGuard();
        using var other = new TestWindow("Other", style: AppStyle);
        Assert.True(other.Activate(), Desktop.WhyNotInFront("\"Other\""));

        using var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        using var launcher = new TestWindow(
            "Palette", style: AppStyle,
            exStyle: WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST);
        Assert.True(launcher.Activate(), Desktop.WhyNotInFront("the launcher"));

        launcher.Hide();

        TestWindow.PumpUntil(() => host.HoldsForeground, 1000);
        Assert.True(host.HoldsForeground, "the foreground went to " + Describe(Win32Window.GetForeground(), other));
    }

    /// <summary>
    /// Why the desktop was the wrong place: Windows never hands the foreground back
    /// to it, so parking it there only chooses which application window gets it.
    /// </summary>
    /// <remarks>
    /// A characterisation of Windows rather than of Shubbak, kept because it is the
    /// measurement the design rests on. Should a future Windows start returning the
    /// foreground to the desktop, this fails and the sink becomes optional.
    /// </remarks>
    [FactOnAnInteractiveDesktop]
    public void TheDesktopIsNotHandedTheForegroundBack()
    {
        using var restore = new ForegroundGuard();
        using var other = new TestWindow("Other", style: AppStyle);
        Assert.True(other.Activate(), Desktop.WhyNotInFront("\"Other\""));

        // Parked from a thread of ours, as the daemon did.
        using var host = new SinkHost();
        Assert.True(host.Invoke(WindowActions.FocusDesktop), "the desktop could not be given the foreground");

        using var launcher = new TestWindow("Launcher", style: AppStyle);
        Assert.True(launcher.Activate(), Desktop.WhyNotInFront("the launcher"));

        launcher.Hide();

        TestWindow.PumpUntil(() => other.HoldsForeground, 1000);

        nint shell;
        unsafe { shell = (nint)PInvoke.GetShellWindow().Value; }
        Assert.NotEqual(shell, Win32Window.GetForeground());
        Assert.True(other.HoldsForeground, "the foreground went to " + Describe(Win32Window.GetForeground(), other));
    }

    /// <summary>
    /// The sibling case: a window that opened on the empty workspace and then closes,
    /// hides, or minimises hands the foreground back to the sink rather than to the
    /// other monitor.
    /// </summary>
    /// <remarks>
    /// Minimising is the case that decides the sink's shape. Closing and hiding go to
    /// the previously active window, whatever it is; minimising walks the stacking
    /// order and skips tool windows, so a tool-window sink loses exactly this case.
    /// The sink is an owned popup instead, and this is the test that would notice a
    /// change of mind.
    /// </remarks>
    [TheoryOnAnInteractiveDesktop]
    [InlineData("destroy")]
    [InlineData("hide")]
    [InlineData("minimise")]
    public void TheLastWindowLeavingHandsTheForegroundBackToTheSink(string how)
    {
        using var restore = new ForegroundGuard();
        using var other = new TestWindow("Other", style: AppStyle);
        Assert.True(other.Activate(), Desktop.WhyNotInFront("\"Other\""));

        using var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        using var window = new TestWindow("Opened on the empty workspace", style: AppStyle);
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the window"));
        Assert.False(host.HoldsForeground);

        switch (how)
        {
            case "destroy": window.Destroy(); break;
            case "hide": window.Hide(); break;
            default: window.Minimise(); break;
        }

        TestWindow.PumpUntil(() => host.HoldsForeground, 1000);
        Assert.True(host.HoldsForeground, "the foreground went to " + Describe(Win32Window.GetForeground(), other));
    }

    /// <summary>Taking what is already held is a no-op that still says yes.</summary>
    [FactOnAnInteractiveDesktop]
    public void TakingTwiceIsHarmless()
    {
        using var restore = new ForegroundGuard();
        using var host = new SinkHost();

        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));
        nint first = host.Handle;

        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));
        Assert.Equal(first, host.Handle);
        Assert.True(host.HoldsForeground);
    }

    /// <summary>
    /// Taken again for another monitor while it already has the foreground, it moves
    /// there without changing hands.
    /// </summary>
    /// <remarks>
    /// The case: the last window on the other display closes and Windows hands the
    /// foreground back to the sink, still sitting where the previous empty workspace
    /// was. A launcher that opens on the foreground window's monitor would open on the
    /// wrong one until the sink follows the point of action.
    /// </remarks>
    [FactOnAnInteractiveDesktop]
    public void TakingForAnotherMonitorMovesItThere()
    {
        using var restore = new ForegroundGuard();
        using var host = new SinkHost();

        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));
        Assert.Equal((0, 0), host.Position);

        // Another monitor, or the same one at a different origin: the origin is all the
        // sink reads, so the assertion does not need a second display to be present.
        Assert.True(host.Take(new Rect(40, 60, 0, 0)));

        Assert.Equal((40, 60), host.Position);
        Assert.True(host.HoldsForeground);

        Rect bounds = Win32Window.GetBounds(host.Handle);
        Assert.Equal(40, bounds.X);
        Assert.Equal(60, bounds.Y);
    }

    /// <summary>
    /// Nothing about it is ever a window to tile, under every setting the filter has.
    /// </summary>
    [FactOnAnInteractiveDesktop]
    public void ItIsNeverManageable()
    {
        using var restore = new ForegroundGuard();
        using var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        nint handle = host.Handle;

        Assert.False(WindowFilter.Evaluate(handle).Manageable);
        Assert.False(WindowFilter.Evaluate(handle, requireTitle: false, concealedAreEligible: true).Manageable);
        Assert.True(WindowFilter.IsExcludedClassName(FocusSink.WindowClass));
    }

    /// <summary>
    /// Owned and not a tool window - the shape that keeps it off the taskbar and out of
    /// Alt+Tab while leaving it eligible for the walk a minimise performs.
    /// </summary>
    /// <remarks>
    /// The filter names the shape: an owned window without a title bar is refused as
    /// an owned popup, before its class is even looked at.
    /// </remarks>
    [FactOnAnInteractiveDesktop]
    public void ItIsAnOwnedPopupRatherThanAToolWindow()
    {
        using var restore = new ForegroundGuard();
        using var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        nint handle = host.Handle;

        Assert.Equal(ExclusionReason.OwnedPopup, WindowFilter.Evaluate(handle).Reason);

        uint exStyle = Win32Window.GetExStyleBits(handle);
        Assert.Equal(0u, exStyle & (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
        Assert.Equal(0u, exStyle & (uint)WINDOW_EX_STYLE.WS_EX_APPWINDOW);
        Assert.True(Win32Window.GetBounds(handle).IsEmpty);
    }

    /// <summary>
    /// Alt+F4 with the keyboard on the sink asks it to close. It declines: destroyed,
    /// it would be gone for the rest of the session with nothing to notice.
    /// </summary>
    [FactOnAnInteractiveDesktop]
    public void ItDeclinesToClose()
    {
        using var restore = new ForegroundGuard();
        using var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        nint handle = host.Handle;
        PInvoke.SendMessage(new HWND(handle), PInvoke.WM_CLOSE, default, default);

        Assert.True(Win32Window.Exists(handle));
        Assert.True(host.HoldsForeground);
    }

    /// <summary>
    /// Retiring hides it and does not leave the keyboard on a window that is about to
    /// vanish: the foreground is handed to the desktop first.
    /// </summary>
    [FactOnAnInteractiveDesktop]
    public void RetiringHidesItAndHandsTheForegroundOn()
    {
        using var restore = new ForegroundGuard();
        using var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        host.Retire();

        Assert.False(Win32Window.IsVisible(host.Handle));
        Assert.False(host.HoldsForeground);
        Assert.True(Win32Window.Exists(host.Handle), "retiring is hiding, not destroying");
    }

    /// <summary>A retired sink can be taken again.</summary>
    [FactOnAnInteractiveDesktop]
    public void ItCanBeTakenAgainAfterRetiring()
    {
        using var restore = new ForegroundGuard();
        using var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));
        host.Retire();

        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));
        Assert.True(host.HoldsForeground);
        Assert.True(Win32Window.IsVisible(host.Handle));
    }

    /// <summary>Disposing removes both windows, and disposing twice is harmless.</summary>
    [FactOnAnInteractiveDesktop]
    public void DisposingLeavesNothingBehind()
    {
        using var restore = new ForegroundGuard();
        var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        host.Dispose();
        host.Dispose();

        Assert.Empty(SinkWindows());
    }

    /// <summary>Not created until it is needed, so a config that never asks never has one.</summary>
    [Fact]
    public void ItIsNotCreatedUntilTaken()
    {
        using var sink = new FocusSink();

        Assert.False(sink.IsCreated);
        Assert.Equal(0, sink.Handle);
        Assert.False(sink.Is(0));
        Assert.Empty(SinkWindows());

        // Retiring one that was never created is nothing at all.
        sink.Retire();
    }

    private static string Describe(nint handle, TestWindow other) =>
        handle == other.Handle
            ? "\"Other\" - the window on the other monitor"
            : $"0x{handle:X} \"{Win32Window.GetTitle(handle)}\" [{Win32Window.GetClassName(handle)}]";

    /// <summary>The sink windows owned by this process, by class.</summary>
    private static List<nint> SinkWindows()
    {
        uint self = (uint)Environment.ProcessId;
        List<nint> found = [];

        foreach (nint handle in Win32Window.EnumerateTopLevel())
        {
            if (!string.Equals(Win32Window.GetClassName(handle), FocusSink.WindowClass, StringComparison.Ordinal))
                continue;

            uint owner = 0;
            unsafe { _ = PInvoke.GetWindowThreadProcessId(new HWND(handle), &owner); }
            if (owner == self) found.Add(handle);
        }

        return found;
    }

    /// <summary>
    /// Hosts a <see cref="FocusSink"/> on a pumping thread of its own, as the daemon
    /// does, and runs every call to it there.
    /// </summary>
    /// <remarks>
    /// The sink's window has to be pumped by the thread that created it, and taking
    /// the foreground sends messages to that thread synchronously. The test thread does
    /// not pump, so the sink cannot live there.
    /// </remarks>
    private sealed class SinkHost : IDisposable
    {
        private const uint InvokeMessage = PInvoke.WM_APP + 0x52;

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Queue<Action> _pending = new();
        private readonly FocusSink _sink = new();
        private uint _threadId;
        private bool _disposed;

        public SinkHost()
        {
            _thread = new Thread(Run) { Name = "Shubbak focus sink host", IsBackground = true };
            _thread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("the sink host never started");
        }

        public nint Handle => _sink.Handle;

        public (int X, int Y) Position => _sink.Position;

        public bool HoldsForeground => _sink.HoldsForeground;

        public bool Take(Rect workArea) => Invoke(() => _sink.Take(workArea));

        public void Retire() => Invoke(() => { _sink.Retire(); return true; });

        public bool Invoke(Func<bool> action)
        {
            bool result = false;
            Exception? failure = null;
            using var done = new ManualResetEventSlim(false);

            lock (_pending)
            {
                _pending.Enqueue(() =>
                {
                    try { result = action(); }
                    catch (Exception ex) { failure = ex; }
                    finally { done.Set(); }
                });
            }

            if (!PInvoke.PostThreadMessage(_threadId, InvokeMessage, default, default))
                throw new InvalidOperationException("the sink host's thread is not accepting work");

            if (!done.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("the sink host did not run the action");

            if (failure is not null) throw new InvalidOperationException("the action failed on the sink's thread", failure);

            return result;
        }

        private void Run()
        {
            _threadId = PInvoke.GetCurrentThreadId();

            // Forces the thread to have a message queue before anyone posts to it.
            PInvoke.PeekMessage(out _, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
            _ready.Set();

            while (PInvoke.GetMessage(out MSG message, default, 0, 0))
            {
                if (message.message == PInvoke.WM_QUIT) break;

                if (message.message == InvokeMessage && message.hwnd.IsNull)
                {
                    Action? work;
                    lock (_pending) work = _pending.Count > 0 ? _pending.Dequeue() : null;

                    work?.Invoke();
                    continue;
                }

                PInvoke.TranslateMessage(in message);
                PInvoke.DispatchMessage(in message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Invoke(() => { _sink.Dispose(); return true; });

            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
            _thread.Join(TimeSpan.FromSeconds(5));
            _ready.Dispose();
        }
    }

    /// <summary>
    /// Remembers what had the foreground when a test began and gives it back after.
    /// </summary>
    /// <remarks>
    /// These tests take the keyboard for real. Whoever was typing when the run started
    /// gets it back when each test is done - best effort, since the window may be gone.
    /// </remarks>
    private sealed class ForegroundGuard : IDisposable
    {
        private readonly nint _original = Win32Window.GetForeground();

        public void Dispose()
        {
            if (_original == 0 || !Win32Window.Exists(_original)) return;
            if (Win32Window.GetProcessId(_original) == (uint)Environment.ProcessId) return;

            WindowActions.Focus(_original);
        }
    }
}
