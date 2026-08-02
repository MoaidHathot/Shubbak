using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;

namespace Shubbak.Native;

/// <summary>
/// Adds and removes taskbar buttons.
/// </summary>
/// <remarks>
/// <para>
/// Needed because cloaking and hiding differ here. A window hidden with <c>SW_HIDE</c>
/// leaves the taskbar and Alt+Tab automatically; a cloaked window does not - the shell
/// keeps showing it, since cloaking is what it uses for windows on other virtual
/// desktops, which remain switchable by design.
/// </para>
/// <para>
/// Without this, moving from hiding to cloaking would trade one visible bug for
/// another: every window from every inactive workspace crowding the taskbar. GlazeWM
/// deals with the same problem the same way.
/// </para>
/// <para>
/// <c>ITaskbarList</c> is documented and stable, unlike
/// <see cref="Win32ApplicationView"/>. It is hand-rolled here only because CsWin32
/// emits <c>ComImport</c> interfaces, which NativeAOT cannot use.
/// </para>
/// </remarks>
public static unsafe partial class Win32Taskbar
{
    private static readonly Guid TaskbarListClsid =
        new(0x56FDF344, 0xFD6D, 0x11D0, 0x95, 0x8A, 0x00, 0x60, 0x97, 0xC9, 0xA0, 0x90);

    private static readonly Guid TaskbarListIid =
        new(0x56FDF342, 0xFD6D, 0x11D0, 0x95, 0x8A, 0x00, 0x60, 0x97, 0xC9, 0xA0, 0x90);

    // IUnknown occupies 0-2.
    private const int HrInitSlot = 3;
    private const int AddTabSlot = 4;
    private const int DeleteTabSlot = 5;
    private const int ReleaseSlot = 2;

    [ThreadStatic] private static nint t_taskbar;
    [ThreadStatic] private static bool t_unavailable;

    private static int s_failureReported;

    /// <summary>Shows or removes a window's taskbar button.</summary>
    /// <remarks>
    /// Best effort. A missing taskbar button is a cosmetic problem; refusing to
    /// conceal the window because of one would not be.
    /// </remarks>
    public static void SetVisible(nint handle, bool visible)
    {
        if (handle == 0) return;

        nint taskbar = GetTaskbar();
        if (taskbar == 0) return;

        try
        {
            var call =
                (delegate* unmanaged[Stdcall]<nint, nint, int>)
                (*(void***)taskbar)[visible ? AddTabSlot : DeleteTabSlot];

            int hr = call(taskbar, handle);

            if (hr < 0)
                ReportOnce($"could not update the taskbar button for 0x{handle:X} (hr 0x{hr:X8})");
        }
        catch (Exception ex)
        {
            ReportOnce($"updating the taskbar button threw: {ex.Message}");
            t_unavailable = true;
        }
    }

    private static nint GetTaskbar()
    {
        if (t_taskbar != 0) return t_taskbar;
        if (t_unavailable) return 0;

        try
        {
            Guid clsid = TaskbarListClsid;
            Guid iid = TaskbarListIid;
            nint taskbar = 0;

            int hr = CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, &taskbar);

            if (hr < 0 || taskbar == 0)
            {
                ReportOnce($"could not create the taskbar list (hr 0x{hr:X8})");
                t_unavailable = true;
                return 0;
            }

            // HrInit must run before any other method, and once per instance.
            var hrInit = (delegate* unmanaged[Stdcall]<nint, int>)(*(void***)taskbar)[HrInitSlot];
            hrInit(taskbar);

            t_taskbar = taskbar;
            return taskbar;
        }
        catch (Exception ex)
        {
            ReportOnce($"creating the taskbar list threw: {ex.Message}");
            t_unavailable = true;
            return 0;
        }
    }

    /// <summary>Releases the cached interface for the calling thread.</summary>
    public static void Release()
    {
        if (t_taskbar == 0) return;

        try
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)t_taskbar)[ReleaseSlot];
            release(t_taskbar);
        }
        catch (Exception)
        {
            // Leaking one interface pointer during teardown beats faulting on the way out.
        }

        t_taskbar = 0;
    }

    private static void ReportOnce(string message)
    {
        if (Interlocked.Exchange(ref s_failureReported, 1) != 0) return;

        Log.Warn(LogCategory.Window,
            $"{message}. Concealed windows may keep their taskbar buttons.");
    }

    private const int ClsctxInprocServer = 0x1;

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        Guid* rclsid, nint pUnkOuter, int dwClsContext, Guid* riid, nint* ppv);
}
