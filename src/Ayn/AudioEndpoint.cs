using Shubbak.Core.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ayn;

/// <summary>
/// The default microphone's mute switch, and a way to be woken when it flips.
/// </summary>
/// <remarks>
/// <para>
/// Core Audio, by hand: the enumerator finds the default communications capture
/// device - the one Teams and its kind use, falling back to the console device when
/// the machine has no notion of a communications one - and its endpoint-volume
/// interface reads and sets the mute. Raw vtables rather than generated wrappers,
/// as the shell's application-view collection is reached elsewhere in this
/// repository, because the two callbacks Core Audio wants are objects we have to
/// <em>be</em>, not call, and a hand-rolled vtable is the honest way to be one under
/// NativeAOT.
/// </para>
/// <para>
/// Woken, not polled. The endpoint tells us when its mute or volume changes, and the
/// enumerator tells us when the default device changes; each callback does nothing
/// but note the fact and set an event the watcher's loop is already waiting on. There
/// is no timer here and no thread of ours; the callbacks arrive on Core Audio's.
/// </para>
/// <para>
/// This is the system's mute, the one the Sound settings toggle. A call's own mute
/// button - Teams's, Zoom's - is that program's and invisible from here; Teams does
/// notice this one and says "muted by your system", which is the right division.
/// </para>
/// </remarks>
internal sealed unsafe partial class AudioEndpoint : IDisposable
{
    // {BCDE0395-E52F-467C-8E3D-C4579291692E}
    private static readonly Guid MMDeviceEnumeratorClsid = new(0xBCDE0395, 0xE52F, 0x467C, 0x8E, 0x3D, 0xC4, 0x57, 0x92, 0x91, 0x69, 0x2E);

    // {A95664D2-9614-4F35-A746-DE8DB63617E6}
    private static readonly Guid IMMDeviceEnumeratorIid = new(0xA95664D2, 0x9614, 0x4F35, 0xA7, 0x46, 0xDE, 0x8D, 0xB6, 0x36, 0x17, 0xE6);

    // {5CDF2C82-841E-4546-9722-0CF74078229A}
    private static readonly Guid IAudioEndpointVolumeIid = new(0x5CDF2C82, 0x841E, 0x4546, 0x97, 0x22, 0x0C, 0xF7, 0x40, 0x78, 0x22, 0x9A);

    // {657804FA-D6AD-4496-8A60-352752AF4F89}
    private static readonly Guid IAudioEndpointVolumeCallbackIid = new(0x657804FA, 0xD6AD, 0x4496, 0x8A, 0x60, 0x35, 0x27, 0x52, 0xAF, 0x4F, 0x89);

    // {7991EEC9-7E89-4D85-8390-6C703CEC60C0}
    private static readonly Guid IMMNotificationClientIid = new(0x7991EEC9, 0x7E89, 0x4D85, 0x83, 0x90, 0x6C, 0x70, 0x3C, 0xEC, 0x60, 0xC0);

    // {00000000-0000-0000-C000-000000000046}
    private static readonly Guid IUnknownIid = new(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);

    /// <summary>Our own event context, so our own changes can be told from the user's in the log.</summary>
    private static readonly Guid OurContext = new(0x6179616E, 0x2d65, 0x7965, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01);

    private const int ECapture = 1;
    private const int ECommunications = 2;
    private const int EConsole = 0;

    private const int SOk = 0;
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int ENotFound = unchecked((int)0x80070490);
    private const int ClsctxAll = 0x17;
    private const int CoinitMultiThreaded = 0x0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    // IMMDeviceEnumerator slots.
    private const int GetDefaultAudioEndpointSlot = 4;
    private const int RegisterEndpointNotificationSlot = 6;
    private const int UnregisterEndpointNotificationSlot = 7;

    // IMMDevice slots.
    private const int ActivateSlot = 3;

    // IAudioEndpointVolume slots.
    private const int RegisterControlChangeNotifySlot = 3;
    private const int UnregisterControlChangeNotifySlot = 4;
    private const int SetMuteSlot = 14;
    private const int GetMuteSlot = 15;

    private const int ReleaseSlot = 2;

    /// <summary>
    /// Set by the callbacks; the loop waits on it beside the registry's events. Static,
    /// because the callbacks are static: they have no instance, only a vtable.
    /// </summary>
    public static AutoResetEvent Changed { get; } = new(false);
    private static int s_defaultDeviceChanged;

    private nint _enumerator;
    private nint _volume;
    private void* _volumeCallback;
    private void* _deviceCallback;
    private bool _complained;

    /// <summary>Whether there is an endpoint to ask.</summary>
    public bool HasDevice => _volume != 0;

    /// <summary>
    /// Opens Core Audio and finds the default microphone. False if Core Audio itself
    /// is not there, which is not a machine this can do anything useful on.
    /// </summary>
    public bool Open()
    {
        int hr = CoInitializeEx(0, CoinitMultiThreaded);
        if (hr < 0 && hr != RpcEChangedMode) return Fail("CoInitializeEx", hr);

        Guid clsid = MMDeviceEnumeratorClsid;
        Guid iid = IMMDeviceEnumeratorIid;
        nint enumerator = 0;

        hr = CoCreateInstance(&clsid, 0, ClsctxAll, &iid, &enumerator);
        if (hr < 0 || enumerator == 0) return Fail("creating the device enumerator", hr);

        _enumerator = enumerator;

        _deviceCallback = MakeObject(DeviceVtable());
        var register = (delegate* unmanaged[Stdcall]<nint, void*, int>)(*(void***)_enumerator)[RegisterEndpointNotificationSlot];
        hr = register(_enumerator, _deviceCallback);
        if (hr < 0) Log.Warn(LogCategory.Wm, $"could not register for default-device changes (hr 0x{hr:X8}); a new microphone will not be noticed until restart");

        Resolve();
        return true;
    }

    /// <summary>
    /// Re-finds the default microphone after the default device changed, or when the
    /// loop is asked to. Safe to call when nothing changed.
    /// </summary>
    public void Resolve()
    {
        ReleaseVolume();

        nint device = DefaultCaptureDevice(ECommunications) is var communications && communications != 0
            ? communications
            : DefaultCaptureDevice(EConsole);

        if (device == 0)
        {
            Log.Info(LogCategory.Wm, "no microphone: nothing to report about mute until one appears");
            return;
        }

        try
        {
            Guid iid = IAudioEndpointVolumeIid;
            nint volume = 0;

            var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, int, void*, nint*, int>)(*(void***)device)[ActivateSlot];
            int hr = activate(device, &iid, ClsctxAll, null, &volume);

            if (hr < 0 || volume == 0)
            {
                Fail("activating the endpoint volume", hr);
                return;
            }

            _volume = volume;
            if (_volumeCallback is null) _volumeCallback = MakeObject(VolumeVtable());

            var register = (delegate* unmanaged[Stdcall]<nint, void*, int>)(*(void***)_volume)[RegisterControlChangeNotifySlot];
            hr = register(_volume, _volumeCallback);
            if (hr < 0) Log.Warn(LogCategory.Wm, $"could not register for mute changes (hr 0x{hr:X8}); the microphone's mute will be read only on other wake-ups");
        }
        finally
        {
            Release(device);
        }
    }

    /// <summary>
    /// The mute state now, or null when there is no microphone.
    /// </summary>
    /// <remarks>
    /// Read on every wake rather than remembered from the last callback, because a
    /// read is one call and a remembered value is a second copy of the truth.
    /// </remarks>
    public bool? IsMuted()
    {
        if (_volume == 0) return null;

        int muted = 0;
        var getMute = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(void***)_volume)[GetMuteSlot];
        int hr = getMute(_volume, &muted);

        if (hr < 0)
        {
            Fail("reading the mute", hr);
            return null;
        }

        return muted != 0;
    }

    /// <summary>Sets the mute. False when there is no microphone or the call failed.</summary>
    public bool SetMuted(bool muted)
    {
        if (_volume == 0) return false;

        Guid context = OurContext;
        var setMute = (delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)(*(void***)_volume)[SetMuteSlot];
        int hr = setMute(_volume, muted ? 1 : 0, &context);

        return hr >= 0 || Fail("setting the mute", hr);
    }

    /// <summary>
    /// Whether the default device changed since this was last asked. The loop calls
    /// <see cref="Resolve"/> when it did.
    /// </summary>
    public static bool TakeDefaultDeviceChanged() => Interlocked.Exchange(ref s_defaultDeviceChanged, 0) != 0;

    public void Dispose()
    {
        ReleaseVolume();

        if (_enumerator != 0)
        {
            if (_deviceCallback is not null)
            {
                var unregister = (delegate* unmanaged[Stdcall]<nint, void*, int>)(*(void***)_enumerator)[UnregisterEndpointNotificationSlot];
                unregister(_enumerator, _deviceCallback);
            }

            Release(_enumerator);
            _enumerator = 0;
        }

        // The callback objects are freed after nothing can call them any more.
        if (_deviceCallback is not null) { NativeMemory.Free(_deviceCallback); _deviceCallback = null; }
        if (_volumeCallback is not null) { NativeMemory.Free(_volumeCallback); _volumeCallback = null; }
    }

    private nint DefaultCaptureDevice(int role)
    {
        nint device = 0;
        var get = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)(*(void***)_enumerator)[GetDefaultAudioEndpointSlot];
        int hr = get(_enumerator, ECapture, role, &device);

        if (hr == ENotFound) return 0;
        if (hr < 0 || device == 0)
        {
            Fail("finding the default microphone", hr);
            return 0;
        }

        return device;
    }

    private void ReleaseVolume()
    {
        if (_volume == 0) return;

        if (_volumeCallback is not null)
        {
            var unregister = (delegate* unmanaged[Stdcall]<nint, void*, int>)(*(void***)_volume)[UnregisterControlChangeNotifySlot];
            unregister(_volume, _volumeCallback);
        }

        Release(_volume);
        _volume = 0;
    }

    private bool Fail(string what, int hr)
    {
        if (!_complained)
        {
            _complained = true;
            Log.Warn(LogCategory.Wm, $"Core Audio: {what} failed (hr 0x{hr:X8}); the microphone's mute will not be reported");
        }

        return false;
    }

    private static void Release(nint unknown)
    {
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)unknown)[ReleaseSlot];
        release(unknown);
    }

    // ---- the two objects we are ------------------------------------------------

    /// <summary>
    /// A COM object is a pointer to a pointer to a vtable. This allocates the object
    /// - one pointer - and points it at the vtable.
    /// </summary>
    private static void* MakeObject(void** vtable)
    {
        void** self = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
        self[0] = vtable;
        return self;
    }

    private static void** s_volumeVtable;
    private static void** s_deviceVtable;

    /// <summary>IAudioEndpointVolumeCallback: IUnknown and OnNotify.</summary>
    private static void** VolumeVtable()
    {
        if (s_volumeVtable is not null) return s_volumeVtable;

        void** vtable = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
        vtable[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryVolumeCallback;
        vtable[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
        vtable[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&ReleaseSelf;
        vtable[3] = (delegate* unmanaged[Stdcall]<void*, void*, int>)&OnNotify;
        return s_volumeVtable = vtable;
    }

    /// <summary>IMMNotificationClient: IUnknown and five notifications, of which one matters.</summary>
    private static void** DeviceVtable()
    {
        if (s_deviceVtable is not null) return s_deviceVtable;

        void** vtable = (void**)NativeMemory.Alloc((nuint)(8 * sizeof(void*)));
        vtable[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryDeviceCallback;
        vtable[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
        vtable[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&ReleaseSelf;
        vtable[3] = (delegate* unmanaged[Stdcall]<void*, ushort*, uint, int>)&OnDeviceStateChanged;
        vtable[4] = (delegate* unmanaged[Stdcall]<void*, ushort*, int>)&OnDeviceAddedOrRemoved;
        vtable[5] = (delegate* unmanaged[Stdcall]<void*, ushort*, int>)&OnDeviceAddedOrRemoved;
        vtable[6] = (delegate* unmanaged[Stdcall]<void*, int, int, ushort*, int>)&OnDefaultDeviceChanged;
        vtable[7] = (delegate* unmanaged[Stdcall]<void*, ushort*, void*, int>)&OnPropertyValueChanged;
        return s_deviceVtable = vtable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryVolumeCallback(void* self, Guid* riid, void** ppv) =>
        QueryInterface(self, riid, ppv, IAudioEndpointVolumeCallbackIid);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryDeviceCallback(void* self, Guid* riid, void** ppv) =>
        QueryInterface(self, riid, ppv, IMMNotificationClientIid);

    private static int QueryInterface(void* self, Guid* riid, void** ppv, Guid own)
    {
        if (ppv is null) return ENoInterface;

        if (*riid == IUnknownIid || *riid == own)
        {
            *ppv = self;
            return SOk;
        }

        *ppv = null;
        return ENoInterface;
    }

    // Static lifetime: the objects live as long as the process and are freed only
    // after they are unregistered, so the counts are decoration.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseSelf(void* self) => 1;

    /// <summary>
    /// The mute or the volume changed. The data carries the new mute, but it is not
    /// kept: the loop reads the endpoint itself, so there is one copy of the truth.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnNotify(void* self, void* data)
    {
        Changed.Set();
        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceStateChanged(void* self, ushort* id, uint state) => SOk;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceAddedOrRemoved(void* self, ushort* id) => SOk;

    /// <summary>A new default microphone: the loop re-resolves and re-reads.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDefaultDeviceChanged(void* self, int flow, int role, ushort* id)
    {
        if (flow == ECapture && role is ECommunications or EConsole)
        {
            Interlocked.Exchange(ref s_defaultDeviceChanged, 1);
            Changed.Set();
        }

        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnPropertyValueChanged(void* self, ushort* id, void* key) => SOk;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, int coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* rclsid, nint pUnkOuter, int dwClsContext, Guid* riid, nint* ppv);
}
