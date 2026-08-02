using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;

namespace Shubbak.Native;

/// <summary>
/// Conceals windows through the shell's own application-view interface.
/// </summary>
/// <remarks>
/// <para>
/// The only way Shubbak can hide a window it does not own without losing it.
/// </para>
/// <para>
/// The obvious approach, <c>DwmSetWindowAttribute(DWMWA_CLOAK)</c>, cannot work:
/// the attribute is scoped to the owning process, and the compositor answers
/// <c>E_ACCESSDENIED</c> for anything else. Shubbak never manages its own windows, so
/// that call has never once succeeded in production - it silently fell through to
/// <c>SW_HIDE</c>, which the window filter then rejects as invisible forever after.
/// Windows were stranded: gone from Alt+Tab and the taskbar, their processes still
/// running, unrecoverable without restarting the application.
/// </para>
/// <para>
/// The shell cloaks windows on other virtual desktops the same way, through
/// <c>IApplicationView::SetCloak</c>, and being the shell it is not restricted. Asking
/// it to do the work is the standard solution: both GlazeWM and komorebi use this
/// interface, komorebi having marked its <c>SW_HIDE</c> mode end-of-life.
/// </para>
/// <para>
/// <b>This is undocumented.</b> The IIDs are not contractual and have moved between
/// Windows builds before. Every failure is therefore soft - callers fall back to
/// another concealment method rather than breaking - and the interface is exercised by
/// a cross-process test that fails loudly if the shape ever changes.
/// </para>
/// <para>
/// Interface definitions follow Ciantic's AltTabAccessor (MIT), by way of komorebi.
/// Calls are raw vtable dispatch rather than COM interop: it keeps the path
/// allocation-free per ADR 0001, and avoids <c>ComImport</c>, which NativeAOT cannot
/// use.
/// </para>
/// </remarks>
public static unsafe partial class Win32ApplicationView
{
    // CLSID_ImmersiveShell - the shell service host.
    private static readonly Guid ImmersiveShellClsid =
        new(0xC2F03A33, 0x21F5, 0x47FA, 0xB4, 0xBB, 0x15, 0x63, 0x62, 0xA2, 0xF2, 0x39);

    private static readonly Guid ServiceProviderIid =
        new(0x6D5140C1, 0x7436, 0x11CE, 0x80, 0x34, 0x00, 0xAA, 0x00, 0x60, 0x09, 0xFA);

    private static readonly Guid ApplicationViewCollectionIid =
        new(0x1841C6D7, 0x4F9D, 0x42C0, 0xAF, 0x41, 0x87, 0x47, 0x53, 0x8F, 0x10, 0xE5);

    // Vtable slots. IUnknown occupies 0-2 throughout.
    private const int QueryServiceSlot = 3;        // IServiceProvider
    private const int GetViewForHwndSlot = 6;      // IApplicationViewCollection
    private const int RefreshCollectionSlot = 11;  // IApplicationViewCollection
    private const int SetCloakSlot = 12;           // IApplicationView, after IInspectable's 3-5
    private const int ReleaseSlot = 2;

    /// <summary>The cloak type the shell uses for virtual-desktop switching.</summary>
    private const uint CloakTypeDefault = 1;

    private const int CloakFlagOn = 2;
    private const int CloakFlagOff = 0;

    // Per-thread, because COM interface pointers belong to the apartment that created
    // them. Shubbak commits from one thread, so in practice this is created once.
    [ThreadStatic] private static nint t_viewCollection;
    [ThreadStatic] private static bool t_unavailable;

    private static int s_failureReported;

    /// <summary>Whether the shell interface could be reached at least once.</summary>
    public static bool IsAvailable => GetViewCollection() != 0;

    /// <summary>Cloaks a window owned by any process.</summary>
    /// <returns>False if the shell refused, so the caller can fall back.</returns>
    public static bool Cloak(nint handle) => SetCloak(handle, CloakFlagOn);

    /// <summary>Reverses <see cref="Cloak"/>.</summary>
    public static bool Uncloak(nint handle) => SetCloak(handle, CloakFlagOff);

    private static bool SetCloak(nint handle, int flag)
    {
        if (handle == 0) return false;

        nint collection = GetViewCollection();
        if (collection == 0) return false;

        nint view = 0;

        try
        {
            var getViewForHwnd =
                (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)
                (*(void***)collection)[GetViewForHwndSlot];

            var refresh =
                (delegate* unmanaged[Stdcall]<nint, int>)
                (*(void***)collection)[RefreshCollectionSlot];

            // The shell caches its view collection, and a window created moments ago is
            // often not in it yet - it answers TYPE_E_ELEMENTNOTFOUND. RefreshCollection
            // exists for exactly this, but one refresh is not always enough: the
            // collection is rebuilt asynchronously, so a brand-new window can need a
            // moment to appear. Failing here means falling back to SW_HIDE, which is
            // the unrecoverable path, so it is worth a few milliseconds to avoid.
            //
            // Only the failure path pays. Concealment happens on workspace switches,
            // not per frame, so this cannot affect the animation budget.
            int hr = getViewForHwnd(collection, handle, &view);

            for (int attempt = 0; (hr < 0 || view == 0) && attempt < CollectionRetries; attempt++)
            {
                if (attempt > 0) Thread.Sleep(RetryDelayMs);

                refresh(collection);

                hr = getViewForHwnd(collection, handle, &view);
            }

            if (hr < 0 || view == 0)
            {
                ReportOnce($"the shell has no application view for 0x{handle:X} (hr 0x{hr:X8})");
                return false;
            }

            var setCloak =
                (delegate* unmanaged[Stdcall]<nint, uint, int, int>)
                (*(void***)view)[SetCloakSlot];

            hr = setCloak(view, CloakTypeDefault, flag);

            if (hr < 0)
            {
                ReportOnce($"the shell refused to cloak 0x{handle:X} (hr 0x{hr:X8})");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // The interface is undocumented, so a shape change would surface as an
            // access violation rather than a failed HRESULT. Catching keeps a broken
            // shell contract from taking the window manager down with it.
            ReportOnce($"cloaking through the shell threw: {ex.Message}");
            t_unavailable = true;
            return false;
        }
        finally
        {
            if (view != 0) Release(view);
        }
    }

    private static nint GetViewCollection()
    {
        if (t_viewCollection != 0) return t_viewCollection;
        if (t_unavailable) return 0;

        nint provider = 0;

        try
        {
            // Multi-threaded, matching komorebi. Shubbak has no message pump on the
            // threads that conceal windows, and an STA without one deadlocks the
            // shell's cross-apartment calls.
            int hr = CoInitializeEx(0, CoinitMultiThreaded | CoinitDisableOle1Dde);
            if (hr < 0 && hr != RpcEChangedMode) { /* proceed; the apartment exists */ }

            Guid clsid = ImmersiveShellClsid;
            Guid providerIid = ServiceProviderIid;

            hr = CoCreateInstance(&clsid, 0, ClsctxLocalServer | ClsctxInprocServer,
                                  &providerIid, &provider);

            if (hr < 0 || provider == 0)
            {
                ReportOnce($"could not reach the immersive shell (hr 0x{hr:X8})");
                t_unavailable = true;
                return 0;
            }

            var queryService =
                (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, nint*, int>)
                (*(void***)provider)[QueryServiceSlot];

            Guid collectionIid = ApplicationViewCollectionIid;
            nint collection = 0;

            hr = queryService(provider, &collectionIid, &collectionIid, &collection);

            if (hr < 0 || collection == 0)
            {
                ReportOnce($"the shell has no application view collection (hr 0x{hr:X8})");
                t_unavailable = true;
                return 0;
            }

            t_viewCollection = collection;
            return collection;
        }
        catch (Exception ex)
        {
            ReportOnce($"reaching the shell threw: {ex.Message}");
            t_unavailable = true;
            return 0;
        }
        finally
        {
            if (provider != 0) Release(provider);
        }
    }

    private static void Release(nint unknown)
    {
        try
        {
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)unknown)[ReleaseSlot];
            release(unknown);
        }
        catch (Exception)
        {
            // Leaking one interface pointer during teardown is strictly better than
            // faulting on the way out.
        }
    }

    /// <summary>
    /// Logs a cloaking failure once.
    /// </summary>
    /// <remarks>
    /// Concealment runs on every workspace switch, so an unconditional warning would
    /// bury the log. One is enough: the condition is a property of the machine, not of
    /// the window.
    /// </remarks>
    private static void ReportOnce(string message)
    {
        if (Interlocked.Exchange(ref s_failureReported, 1) != 0) return;

        Log.Warn(LogCategory.Window,
            $"{message}. Falling back to another concealment method - " +
            "run 'shubbak restore' if windows go missing.");
    }

    private const int CollectionRetries = 4;
    private const int RetryDelayMs = 15;

    private const int CoinitMultiThreaded = 0x0;
    private const int CoinitDisableOle1Dde = 0x4;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int ClsctxInprocServer = 0x1;
    private const int ClsctxLocalServer = 0x4;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, int coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        Guid* rclsid, nint pUnkOuter, int dwClsContext, Guid* riid, nint* ppv);
}
