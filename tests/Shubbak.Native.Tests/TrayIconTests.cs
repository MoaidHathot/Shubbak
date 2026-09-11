using Windows.Win32;
using Windows.Win32.Foundation;

namespace Shubbak.Native.Tests;

/// <summary>
/// The tray icon, and the one property it must never lose.
/// </summary>
/// <remarks>
/// <para>
/// The daemon owned no window at all before this. <c>Shell_NotifyIcon</c> needs one to
/// send its callback message to, so a tray icon means giving a window manager its first
/// HWND - and a window manager is, specifically, the program that enumerates windows
/// and decides which of them to arrange. If it ever managed its own tray window, the
/// symptom would be a mystery tile in the layout with no application behind it.
/// </para>
/// <para>
/// The window used to be message-only, which kept it out of <c>EnumWindows</c>
/// altogether. That also kept it out of reach of <c>WM_ENDSESSION</c>, so the daemon
/// was killed at logoff and by every installer with the session unsaved. It is now a
/// hidden top-level window, and the property to pin is the one that actually matters:
/// the window filter refuses it, under every setting the filter has.
/// </para>
/// </remarks>
public sealed class TrayIconTests
{
    /// <summary>The class name used for the tray window.</summary>
    private const string TrayClass = "ShubbakTray";

    /// <summary>
    /// The whole reason this class of test exists.
    /// </summary>
    [Fact]
    public void TheTrayWindowIsNeverManageable()
    {
        using var tray = new TrayIcon();

        // Created whether or not the shell accepted the icon: the window exists first,
        // and it is the window that would be tiled.
        tray.Create("Shubbak test");

        List<nint> windows = TrayWindows();
        Assert.NotEmpty(windows);

        foreach (nint handle in windows)
        {
            // Hidden, so the ordinary path never sees it.
            Assert.False(PInvoke.IsWindowVisible(new HWND(handle)));

            // And refused on every other path too - including recovery, which
            // considers concealed windows, and the inspector, which does not require
            // a title. The filter says why, and the answer must never be "yes".
            Assert.False(WindowFilter.Evaluate(handle).Manageable);
            Assert.False(WindowFilter.Evaluate(handle, requireTitle: false, concealedAreEligible: true).Manageable);
        }
    }

    /// <summary>
    /// Excluded by name as well, so the guarantee does not rest on styles that a
    /// future change to the window might alter.
    /// </summary>
    [Fact]
    public void TheTrayWindowClassIsExcludedByName()
    {
        Assert.True(WindowFilter.IsExcludedClassName(TrayClass));
    }

    /// <summary>
    /// Creating and disposing repeatedly must not leave anything behind.
    /// </summary>
    /// <remarks>
    /// The window class is registered once per process and deliberately not
    /// unregistered, so a second create has to tolerate finding it already there -
    /// which is <c>ERROR_CLASS_ALREADY_EXISTS</c>, and is success.
    /// </remarks>
    [Fact]
    public void ItCanBeCreatedMoreThanOnce()
    {
        for (int i = 0; i < 3; i++)
        {
            using var tray = new TrayIcon();
            tray.Create($"Shubbak test {i}");
        }

        // Nothing survives any of them.
        Assert.Empty(TrayWindows());
    }

    /// <summary>Disposing twice is harmless.</summary>
    /// <remarks>
    /// The daemon disposes it on the way out and again if Dispose is reached twice,
    /// which it can be after an exception unwinds differently.
    /// </remarks>
    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var tray = new TrayIcon();
        tray.Create("Shubbak test");

        tray.Dispose();
        tray.Dispose();

        Assert.False(tray.IsShown);
        Assert.Empty(TrayWindows());
    }

    /// <summary>
    /// The menu is asked for each time rather than stored.
    /// </summary>
    /// <remarks>
    /// The difference between "Suspend" and "Resume" is why anyone opens it, so a menu
    /// built once at startup would be wrong exactly when it mattered. This pins the
    /// shape of the contract - that a callback exists and is what is consulted -
    /// rather than trying to drive a modal menu loop from a test.
    /// </remarks>
    [Fact]
    public void TheMenuIsSuppliedByCallback()
    {
        using var tray = new TrayIcon();

        int asked = 0;
        tray.MenuItems = () => { asked++; return [new TrayMenuItem(1, "Something")]; };

        Assert.Equal(0, asked);
        Assert.NotNull(tray.MenuItems);
        Assert.Single(tray.MenuItems!());
        Assert.Equal(1, asked);
    }

    /// <summary>A separator is the reserved id, and no real item may use it.</summary>
    [Fact]
    public void SeparatorsAreDistinguishable()
    {
        Assert.Equal(0, TrayMenuItem.Separator.Id);
        Assert.NotEqual(0, new TrayMenuItem(1, "Real").Id);
    }

    /// <summary>The tray windows owned by this process, by class.</summary>
    /// <remarks>
    /// By process as well as by class, because a running Shubbak on the machine the
    /// tests run on has one of these too, and the guard that stops these tests running
    /// beside a window manager is elsewhere.
    /// </remarks>
    private static List<nint> TrayWindows()
    {
        uint self = (uint)Environment.ProcessId;
        List<nint> found = [];

        foreach (nint handle in Win32Window.EnumerateTopLevel())
        {
            if (!string.Equals(Win32Window.GetClassName(handle), TrayClass, StringComparison.Ordinal))
                continue;

            uint owner = 0;
            unsafe { _ = PInvoke.GetWindowThreadProcessId(new HWND(handle), &owner); }
            if (owner == self) found.Add(handle);
        }

        return found;
    }
}
