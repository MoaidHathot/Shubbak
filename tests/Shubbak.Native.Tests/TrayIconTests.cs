namespace Shubbak.Native.Tests;

/// <summary>
/// The tray icon, and the one property it must never lose.
/// </summary>
/// <remarks>
/// <para>
/// The daemon owned no window at all before this. <c>Shell_NotifyIcon</c> needs one to
/// send its callback message to, so a tray icon means giving a window manager its first
/// HWND - and a window manager is, specifically, the program that enumerates windows
/// and decides which of them to arrange.
/// </para>
/// <para>
/// So the window is message-only: created with <c>HWND_MESSAGE</c> as its parent,
/// which keeps it out of <c>EnumWindows</c> entirely. If that ever stopped being true,
/// Shubbak would find its own tray plumbing and try to tile it, and the symptom would
/// be a mystery window appearing in the layout with no application behind it.
/// </para>
/// </remarks>
public sealed class TrayIconTests
{
    /// <summary>The class name used for the tray window.</summary>
    private const string TrayClass = "ShubbakTray";

    /// <summary>
    /// The whole reason the window is message-only.
    /// </summary>
    [Fact]
    public void TheTrayWindowCannotBeFoundByTheWindowEnumerator()
    {
        using var tray = new TrayIcon();

        // Created whether or not the shell accepted the icon: the window exists first,
        // and it is the window that would be enumerable.
        tray.Create("Shubbak test");

        IReadOnlyList<nint> windows = Win32Window.EnumerateTopLevel();

        foreach (nint handle in windows)
        {
            Assert.NotEqual(
                TrayClass,
                Win32Window.GetClassName(handle));
        }
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

        // Nothing enumerable survives any of them.
        foreach (nint handle in Win32Window.EnumerateTopLevel())
            Assert.NotEqual(TrayClass, Win32Window.GetClassName(handle));
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
}
