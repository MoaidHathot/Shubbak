using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Shubbak.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>One entry in the tray icon's menu.</summary>
/// <param name="Id">
/// What is reported when it is chosen. Zero means a separator, which is why no real
/// item may use it.
/// </param>
/// <param name="Text">The label.</param>
/// <param name="Checked">Whether to draw a tick beside it.</param>
/// <param name="Default">
/// Whether this is what a plain click does. Drawn in bold, and there may be only one.
/// </param>
public readonly record struct TrayMenuItem(
    int Id, string Text, bool Checked = false, bool Default = false)
{
    /// <summary>A dividing line.</summary>
    public static TrayMenuItem Separator => new(0, string.Empty);
}

/// <summary>
/// An icon beside the clock, and a menu behind it.
/// </summary>
/// <remarks>
/// <para>
/// The daemon had no window at all before this - deliberately, and it is worth
/// keeping that in view. <c>Shell_NotifyIcon</c> requires one to send its callback
/// message to, so a tray icon means introducing the first HWND this process has ever
/// owned.
/// </para>
/// <para>
/// It is message-only: created with <c>HWND_MESSAGE</c> as its parent, which keeps it
/// out of <c>EnumWindows</c> entirely. That matters more here than it would anywhere
/// else, because the program enumerating windows and deciding which to tile is this
/// one. A tray window that could be found would be a window manager trying to arrange
/// its own plumbing.
/// </para>
/// <para>
/// It also belongs on the daemon's thread and not the keyboard hook's.
/// <c>TrackPopupMenu</c> runs a modal message loop for as long as the menu is open;
/// on the hook thread that would put every keystroke on the machine behind an open
/// menu, against a 300 ms deadline. On this thread it stalls the layout loop, which
/// is not on the input path and is a cost worth paying.
/// </para>
/// </remarks>
public sealed unsafe class TrayIcon : IDisposable
{
    private const string WindowClass = "ShubbakTray";

    /// <summary>The message Windows sends us about the icon.</summary>
    /// <remarks>
    /// Any value from <c>WM_APP</c> upward is ours to define; the shell simply echoes
    /// whatever it was given.
    /// </remarks>
    private const uint CallbackMessage = PInvoke.WM_APP + 1;

    /// <summary>Distinguishes our icon from anyone else's on the same window.</summary>
    private const uint IconId = 1;

    private static readonly Dictionary<nint, TrayIcon> s_windows = [];
    private static bool s_classRegistered;

    /// <summary>
    /// Told to every top-level window when Explorer restarts.
    /// </summary>
    /// <remarks>
    /// Registered once. The value is allocated by the system and is the same for every
    /// process that asks, which is how the broadcast reaches everyone.
    /// </remarks>
    private static uint s_taskbarCreated;

    private HWND _window;
    private HICON _icon;
    private bool _ownsIcon;
    private bool _shown;
    private string _tooltip = "Shubbak";

    /// <summary>Asked for the menu each time it is about to be shown.</summary>
    /// <remarks>
    /// Asked rather than stored, so the labels can describe the current state - the
    /// difference between "Suspend" and "Resume" is the whole reason anyone opens it.
    /// </remarks>
    public Func<IReadOnlyList<TrayMenuItem>>? MenuItems { get; set; }

    /// <summary>Called with the id of whatever was chosen.</summary>
    public Action<int>? ItemChosen { get; set; }

    /// <summary>Whether the icon is currently in the tray.</summary>
    public bool IsShown => _shown;

    /// <summary>
    /// Creates the window and adds the icon.
    /// </summary>
    /// <param name="tooltip">What hovering over it says.</param>
    /// <returns>Whether it worked.</returns>
    /// <remarks>
    /// Failure is reported and survivable. A window manager without a tray icon is a
    /// window manager; one that refuses to start because the shell was not ready is
    /// not an improvement.
    /// </remarks>
    public bool Create(string tooltip)
    {
        _tooltip = tooltip;

        try
        {
            EnsureClassRegistered();

            if (s_taskbarCreated == 0)
                s_taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");

            _window = PInvoke.CreateWindowEx(
                0,
                WindowClass,
                "Shubbak",
                0,
                0, 0, 0, 0,

                // The parent that makes it message-only. Nothing enumerates it, it is
                // never shown, and it cannot be tiled by the program that owns it.
                HWND.HWND_MESSAGE,
                (SafeHandle?)null,
                (SafeHandle?)null,
                null);

            if (_window.IsNull)
            {
                Log.Warn(LogCategory.Wm, $"could not create the tray window: {Marshal.GetLastWin32Error()}");
                return false;
            }

            s_windows[(nint)_window.Value] = this;

            LoadOwnIcon();

            return Add();
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory.Wm, $"could not create the tray icon: {ex.Message}");
            return false;
        }
    }

    /// <summary>Changes the hover text.</summary>
    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;

        if (!_shown) return;

        NOTIFYICONDATAW data = Describe();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_TIP;

        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

    /// <summary>
    /// Handles a message seen by the daemon's pump.
    /// </summary>
    /// <remarks>
    /// Only the Explorer-restart broadcast, which arrives before the window procedure
    /// gets a chance because it is sent to every top-level window rather than posted
    /// to ours specifically.
    /// </remarks>
    public void OnLoopMessage(uint message)
    {
        if (s_taskbarCreated == 0 || message != s_taskbarCreated) return;

        // Explorer has restarted and forgotten every icon it was showing.
        Log.Info(LogCategory.Wm, "the shell restarted; putting the tray icon back");

        _shown = false;
        Add();
    }

    private bool Add()
    {
        NOTIFYICONDATAW data = Describe();

        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data))
        {
            Log.Warn(LogCategory.Wm, $"could not add the tray icon: {Marshal.GetLastWin32Error()}");
            return false;
        }

        _shown = true;
        return true;
    }

    private NOTIFYICONDATAW Describe()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _window,
            uID = IconId,
            uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE
                | NOTIFY_ICON_DATA_FLAGS.NIF_ICON
                | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };

        // Fixed-size buffer in the struct, so it is filled rather than assigned.
        ReadOnlySpan<char> tip = _tooltip.Length > 127 ? _tooltip.AsSpan(0, 127) : _tooltip;
        tip.CopyTo(new Span<char>(data.szTip.AsSpan().ToArray()));

        Span<char> destination = data.szTip.AsSpan();
        destination.Clear();
        tip.CopyTo(destination);

        return data;
    }
    /// <summary>
    /// Takes the icon out of this executable's own file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// So the tray matches Alt-Tab, the taskbar and any shortcut, rather than being a
    /// second image to keep in step with them.
    /// </para>
    /// <para>
    /// By index rather than by resource name. <c>LoadImage</c> with <c>"#1"</c> was
    /// the obvious approach and found nothing: the apphost does not put
    /// <c>ApplicationIcon</c> on a resource id worth relying on.
    /// <c>ExtractIconEx</c> with index zero means "the icon this file shows", which is
    /// the question actually being asked.
    /// </para>
    /// <para>
    /// The small icon, because that is what the tray displays. Unlike a shared
    /// resource this handle is ours, so it is destroyed on the way out.
    /// </para>
    /// </remarks>
    private void LoadOwnIcon()
    {
        string? path = Environment.ProcessPath;

        if (path is not { Length: > 0 })
        {
            Log.Warn(LogCategory.Wm, "cannot find this executable, so the tray will show a blank");
            return;
        }

        HICON large = default;
        HICON small = default;

        uint found;

        fixed (char* file = path)
            found = PInvoke.ExtractIconEx(file, 0, &large, &small, 1);

        // The large one is not wanted and would leak if left.
        if (!large.IsNull) PInvoke.DestroyIcon(large);

        if (found == 0 || small.IsNull)
        {
            Log.Warn(LogCategory.Wm, "this executable has no icon, so the tray will show a blank");
            return;
        }

        _icon = small;
        _ownsIcon = true;
    }

    /// <summary>
    /// Shows the menu at the cursor and reports what was chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SetForegroundWindow</c> first, and a stray message after, because
    /// <c>TrackPopupMenu</c> has a well-known defect: a menu shown by a window that is
    /// not in the foreground does not dismiss when the user clicks elsewhere. It stays
    /// up, over whatever they were trying to click. The two calls are the documented
    /// workaround and have been since the nineties.
    /// </para>
    /// <para>
    /// <c>TPM_RETURNCMD</c> so the choice comes back as a return value rather than as
    /// a <c>WM_COMMAND</c> we would have to route back out of the window procedure.
    /// </para>
    /// </remarks>
    private void ShowMenu()
    {
        IReadOnlyList<TrayMenuItem> items = MenuItems?.Invoke() ?? [];
        if (items.Count == 0) return;

        HMENU menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull) return;

        try
        {
            foreach (TrayMenuItem item in items)
            {
                if (item.Id == 0)
                {
                    PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
                    continue;
                }

                MENU_ITEM_FLAGS flags = MENU_ITEM_FLAGS.MF_STRING;

                if (item.Checked) flags |= MENU_ITEM_FLAGS.MF_CHECKED;
                if (item.Default) flags |= MENU_ITEM_FLAGS.MF_DEFAULT;

                fixed (char* text = item.Text)
                    PInvoke.AppendMenu(menu, flags, (nuint)item.Id, text);
            }

            PInvoke.GetCursorPos(out System.Drawing.Point cursor);

            // Without this the menu will not close when the user clicks away.
            PInvoke.SetForegroundWindow(_window);

            var chosen = (int)PInvoke.TrackPopupMenu(
                menu,
                TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON,
                cursor.X,
                cursor.Y,
                0,
                _window,
                null);

            // The other half of the workaround: gives the menu a message to consume so
            // it tidies up properly.
            PInvoke.PostMessage(_window, PInvoke.WM_NULL, default, default);

            if (chosen != 0) ItemChosen?.Invoke(chosen);
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    private static void EnsureClassRegistered()
    {
        if (s_classRegistered) return;

        fixed (char* className = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = HINSTANCE.Null,
                lpszClassName = className,
            };

            // 1410 is ERROR_CLASS_ALREADY_EXISTS, which is success for our purposes.
            if (PInvoke.RegisterClassEx(in wc) == 0 && Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException(
                    $"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            }
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (message == CallbackMessage && s_windows.TryGetValue((nint)hwnd.Value, out TrayIcon? tray))
            {
                // The low word of lParam is what happened to the icon. Both buttons
                // open the menu: a tray icon with no window to show has nothing else
                // a left click could usefully do, and people try both.
                uint what = (uint)(lParam.Value & 0xFFFF);

                if (what is PInvoke.WM_LBUTTONUP or PInvoke.WM_RBUTTONUP)
                {
                    tray.ShowMenu();
                    return new LRESULT(0);
                }
            }
        }
        catch (Exception)
        {
            // An exception escaping an UnmanagedCallersOnly callback tears down the
            // process. Nothing here is worth that.
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_shown)
        {
            NOTIFYICONDATAW data = Describe();
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
            _shown = false;
        }

        if (!_window.IsNull)
        {
            s_windows.Remove((nint)_window.Value);
            PInvoke.DestroyWindow(_window);
            _window = HWND.Null;
        }

        // Not destroyed when it came from a shared resource, but ExtractIconEx hands
        // over ownership, so ours is released.
        if (_ownsIcon && !_icon.IsNull) PInvoke.DestroyIcon(_icon);

        _icon = HICON.Null;
        _ownsIcon = false;
    }
}
