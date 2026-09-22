using Shubbak.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
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
/// manager. And they carry the <c>Requires=Foreground</c> trait, which the ARM64 job in CI
/// filters out: that runner's image has a sign-in surface in front that no process can
/// take the foreground from - see <see cref="Desktop.RequiresForeground"/>.
/// </para>
/// </remarks>
[Trait("Requires", Desktop.RequiresForeground)]
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
    [Theory]
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
        host.PreviousForeground = launcher.Handle;
        Assert.False(host.HoldsForeground);

        if (destroyed) launcher.Destroy(); else launcher.Hide();

        TestWindow.PumpUntil(() => host.HoldsForeground, 1000);
        Assert.True(host.HoldsForeground, "the foreground went to " + Describe(Win32Window.GetForeground(), other));
    }

    /// <summary>
    /// The tool-window shaped launcher - the command palette is one - is no different.
    /// </summary>
    [Fact]
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
        host.PreviousForeground = launcher.Handle;

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
    [Fact]
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
    [Theory]
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
        host.PreviousForeground = window.Handle;
        Assert.False(host.HoldsForeground);

        switch (how)
        {
            case "destroy": window.Destroy(); break;
            case "hide": window.Hide(); break;
            default: window.Minimise(); break;
        }

        TestWindow.PumpUntil(() => host.HoldsForeground, 1000);
        Assert.True(host.HoldsForeground, "the foreground went to " + Describe(Win32Window.GetForeground(), other));

        // And it stays there: a fallback is not a cycle, and nothing is passed on.
        TestWindow.PumpOnce();
        Assert.True(host.HoldsForeground, "the sink passed a fallback on to " + Describe(Win32Window.GetForeground(), other));
        Assert.Equal(0, host.PassedOn);
    }

    /// <summary>
    /// Alt+F4 is a fallback with Alt held: the window closed, so nothing is passed on.
    /// </summary>
    /// <remarks>
    /// The case that rules out judging on the keyboard alone. Closing the last window
    /// on a workspace with Alt+F4 lands the foreground on the sink with Alt still down,
    /// exactly as Alt+Esc does - and passing it on would send the keyboard to the
    /// other monitor, which is the bug the sink exists to fix.
    /// </remarks>
    [Fact]
    public void AltF4OnTheLastWindowIsNotPassedOn()
    {
        using var restore = new ForegroundGuard();
        using var other = new TestWindow("Other", style: AppStyle);
        Assert.True(other.Activate(), Desktop.WhyNotInFront("\"Other\""));

        using var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        using var window = new TestWindow("Closed with Alt+F4", style: AppStyle);
        Assert.True(window.Activate(), Desktop.WhyNotInFront("the window"));
        host.PreviousForeground = window.Handle;

        Keyboard.Press(VIRTUAL_KEY.VK_MENU);

        try
        {
            window.Destroy();
            TestWindow.PumpUntil(() => host.HoldsForeground, 1000);
            TestWindow.PumpOnce();
        }
        finally
        {
            Keyboard.Release(VIRTUAL_KEY.VK_MENU);
        }

        Assert.True(host.HoldsForeground, "the foreground went to " + Describe(Win32Window.GetForeground(), other));
        Assert.Equal(0, host.PassedOn);
    }

    /// <summary>
    /// Alt+Esc reaching the sink is passed on to the window it was headed for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stacking order is W, sink, Other, top to bottom - the shape the desktop is
    /// in after a window opens on a formerly empty workspace. A real Alt+Esc from W
    /// lands on the sink, since Windows' own walk does not skip an owned window; the
    /// sink then hands the keyboard on to Other and goes to the bottom of the order.
    /// The user sees one press move from W to Other, which is what Alt+Esc means.
    /// </para>
    /// <para>
    /// A real keystroke, because the judgement reads the keyboard: Alt is held for the
    /// switch as a person holds it, and released after.
    /// </para>
    /// </remarks>
    [Fact]
    public void AltEscReachingItIsPassedOn()
    {
        using var restore = new ForegroundGuard();
        using var other = new TestWindow("Other", style: AppStyle);
        Assert.True(other.Activate(), Desktop.WhyNotInFront("\"Other\""));

        using var host = new SinkHost();
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        using var window = new TestWindow("W", style: AppStyle);
        Assert.True(window.Activate(), Desktop.WhyNotInFront("W"));
        host.PreviousForeground = window.Handle;

        Keyboard.AltEsc();

        TestWindow.PumpUntil(() => other.HoldsForeground, 1500);

        Assert.True(other.HoldsForeground, "the foreground stayed on " + Describe(Win32Window.GetForeground(), other));
        Assert.Equal(1, host.PassedOn);
    }

    /// <summary>
    /// The two conditions of the judgement, and why both are needed.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]     // Alt+Esc: Alt down, the window it came from still on screen
    [InlineData(true, false, false)]   // Alt+F4: Alt down, the window gone - however many siblings its thread or process still shows
    [InlineData(false, true, false)]   // the window still on screen but no Alt: nothing Windows is known to do, and not read as a cycle
    [InlineData(false, false, false)]  // a plain close, hide or minimise, or a launcher putting itself away
    public void ACycleNeedsAltDownAndThePreviousWindowStillShowing(bool alt, bool stillShowing, bool cycled)
    {
        Assert.Equal(cycled, FocusSink.WasCycledOnto(alt, stillShowing));
    }

    /// <summary>
    /// Nobody told the sink who had the foreground: an activation is then never a cycle.
    /// </summary>
    [Fact]
    public void WithNothingKnownAboutThePreviousForegroundAltEscIsKept()
    {
        using var restore = new ForegroundGuard();
        using var other = new TestWindow("Other", style: AppStyle);
        Assert.True(other.Activate(), Desktop.WhyNotInFront("\"Other\""));

        using var host = new SinkHost { TellsPreviousForeground = false };
        Assert.True(host.Take(Primary), Desktop.WhyNotInFront("the sink"));

        using var window = new TestWindow("W", style: AppStyle);
        Assert.True(window.Activate(), Desktop.WhyNotInFront("W"));
        host.PreviousForeground = window.Handle;

        Keyboard.AltEsc();

        TestWindow.PumpUntil(() => host.HoldsForeground, 1000);
        TestWindow.PumpOnce();

        Assert.True(host.HoldsForeground, "the foreground went to " + Describe(Win32Window.GetForeground(), other));
        Assert.Equal(0, host.PassedOn);
    }

    /// <summary>What Alt+Esc stops on, as far as it could be measured.</summary>
    [Fact]
    public void ACycleStopIsOnScreenAndAbleToTakeTheKeyboard()
    {
        using var restore = new ForegroundGuard();

        using var plain = new TestWindow("Plain", style: AppStyle);
        Assert.True(FocusSink.IsCycleStop(plain.Handle));

        using var hidden = new TestWindow("Hidden", visible: false, style: AppStyle);
        Assert.False(FocusSink.IsCycleStop(hidden.Handle));

        using var tool = new TestWindow("Tool", style: AppStyle, exStyle: WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
        Assert.False(FocusSink.IsCycleStop(tool.Handle));

        using var toolButApp = new TestWindow(
            "Tool that asks to be an app", style: AppStyle,
            exStyle: WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_APPWINDOW);
        Assert.True(FocusSink.IsCycleStop(toolButApp.Handle));

        using var noActivate = new TestWindow("Declines the keyboard", style: AppStyle, exStyle: WINDOW_EX_STYLE.WS_EX_NOACTIVATE);
        Assert.False(FocusSink.IsCycleStop(noActivate.Handle));

        using var minimised = new TestWindow("Minimised", style: AppStyle);
        minimised.Minimise();
        Assert.False(FocusSink.IsCycleStop(minimised.Handle));

        // Cloaked: on another virtual desktop, or on a workspace Shubbak has concealed.
        // Windows' own Alt+Esc skips these - measured - and so must the walk, or a
        // cycle passed on from the sink would switch the desktop to a hidden workspace.
        using var cloaked = new TestWindow("Cloaked", style: AppStyle);
        Assert.True(Win32Window.Cloak(cloaked.Handle));
        Assert.False(FocusSink.IsCycleStop(cloaked.Handle));

        // Owned by a window that is on screen: the owner is the stop, as it is for a
        // dialog, and activating it brings the owned window forward.
        using var owner = new TestWindow("Owner", style: AppStyle);
        using var dialog = new TestWindow("Dialog", style: AppStyle, owner: owner);
        Assert.False(FocusSink.IsCycleStop(dialog.Handle));
        Assert.True(FocusSink.IsCycleStop(owner.Handle));

        // Gone: the window Alt+F4 closed. And nothing, for a manager that does not know.
        var closed = new TestWindow("Closed", style: AppStyle);
        nint closedHandle = closed.Handle;
        closed.Destroy();
        Assert.False(FocusSink.IsCycleStop(closedHandle));
        Assert.False(FocusSink.IsCycleStop(0));
    }

    /// <summary>Taking what is already held is a no-op that still says yes.</summary>
    [Fact]
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
    [Fact]
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
    [Fact]
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
    [Fact]
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
    [Fact]
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
    [Fact]
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
    [Fact]
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
    [Fact]
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
    /// <para>
    /// The sink's window has to be pumped by the thread that created it, and taking
    /// the foreground sends messages to that thread synchronously. The test thread does
    /// not pump, so the sink cannot live there.
    /// </para>
    /// <para>
    /// It also stands in for the window manager as the sink's source of "who had the
    /// foreground before me". The daemon answers from the last foreground event the
    /// system reported; here the test says so after each activation it performs, which
    /// is the same information by a shorter route - a hook in this process would skip
    /// this process's windows.
    /// </para>
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
        private nint _previousForeground;

        public SinkHost()
        {
            _thread = new Thread(Run) { Name = "Shubbak focus sink host", IsBackground = true };
            _thread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("the sink host never started");

            _sink.PreviousForeground = () => TellsPreviousForeground ? Volatile.Read(ref _previousForeground) : 0;
        }

        /// <summary>The window the manager last saw take the foreground, as the test reports it.</summary>
        public nint PreviousForeground
        {
            get => Volatile.Read(ref _previousForeground);
            set => Volatile.Write(ref _previousForeground, value);
        }

        /// <summary>Whether the sink is told anything at all; off models a manager that does not know.</summary>
        public bool TellsPreviousForeground { get; init; } = true;

        public nint Handle => _sink.Handle;

        public (int X, int Y) Position => _sink.Position;

        public int PassedOn => _sink.PassedOn;

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
