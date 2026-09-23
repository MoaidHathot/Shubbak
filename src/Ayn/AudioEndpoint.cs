using Shubbak.Core.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ayn;

/// <summary>
/// The default microphone's mute switch - and the default speaker's, when asked - and a
/// way to be woken when either flips.
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

    private const int ERender = 0;
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
    private const int OpenPropertyStoreSlot = 4;

    // IPropertyStore slots.
    private const int GetValueSlot = 5;

    // PKEY_Device_FriendlyName: {A45C254E-DF1C-4EFD-8020-67D146A850E0} 14, the name
    // Windows shows in its sound settings - "Speakers (Realtek(R) Audio)".
    private static readonly Guid DeviceFriendlyNameFmtid = new(0xA45C254E, 0xDF1C, 0x4EFD, 0x80, 0x20, 0x67, 0xD1, 0x46, 0xA8, 0x50, 0xE0);
    private const uint DeviceFriendlyNamePid = 14;

    private const int StgmRead = 0;
    private const ushort VtLpwstr = 31;

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
    private nint _speakerVolume;
    private void* _volumeCallback;
    private void* _deviceCallback;
    private string? _lastComplaint;

    /// <summary>Whether there is a microphone to ask.</summary>
    public bool HasDevice => _volume != 0;

    /// <summary>Whether there is a speaker to ask.</summary>
    public bool HasSpeaker => _speakerVolume != 0;

    /// <summary>
    /// The default microphone's name as Windows shows it, or null when there is none.
    /// Read once when the device is resolved, not on every wake: a name is a property
    /// store round trip, and it changes only when the default device does, which is
    /// exactly when <see cref="Resolve"/> runs again.
    /// </summary>
    public string? MicrophoneName { get; private set; }

    /// <summary>The default speaker's name, or null when there is none or it is not followed.</summary>
    public string? SpeakerName { get; private set; }

    /// <summary>
    /// Whether the default speaker is followed as well as the default microphone. Off
    /// unless the file reports the speaker's mute; set before <see cref="Open"/>, or
    /// followed by <see cref="Resolve"/>.
    /// </summary>
    public bool WatchSpeaker { get; set; }

    /// <summary>Whether Core Audio has been opened; see <see cref="Open"/>.</summary>
    public bool IsOpen => _enumerator != 0;

    /// <summary>
    /// Opens Core Audio and finds the default microphone. False if Core Audio itself
    /// is not there, which is not a machine this can do anything useful on. Opening
    /// what is already open is nothing.
    /// </summary>
    public bool Open()
    {
        if (IsOpen) return true;

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
        ReleaseVolume(ref _volume);
        ReleaseVolume(ref _speakerVolume);
        MicrophoneName = null;
        SpeakerName = null;

        _volume = ResolveEndpoint(ECapture, "microphone", out string? microphoneName);
        MicrophoneName = microphoneName;

        if (WatchSpeaker)
        {
            _speakerVolume = ResolveEndpoint(ERender, "speaker", out string? speakerName);
            SpeakerName = speakerName;
        }
    }

    /// <summary>
    /// The default device of one flow - the communications one, falling back to the
    /// console one - with its endpoint volume activated and our callback registered.
    /// </summary>
    /// <param name="flow">Render or capture.</param>
    /// <param name="what">The word for the log.</param>
    /// <param name="name">The device's name, or null when there is no device or it has none.</param>
    private nint ResolveEndpoint(int flow, string what, out string? name)
    {
        name = null;

        nint device = DefaultDevice(flow, ECommunications, what) is var communications && communications != 0
            ? communications
            : DefaultDevice(flow, EConsole, what);

        if (device == 0)
        {
            Log.Info(LogCategory.Wm, $"no {what}: nothing to report about its mute until one appears");
            return 0;
        }

        try
        {
            name = FriendlyName(device, what);

            if (name is not null) Log.Debug(LogCategory.Wm, $"the {what} is \"{name}\"");

            Guid iid = IAudioEndpointVolumeIid;
            nint volume = 0;

            var activate = (delegate* unmanaged[Stdcall]<nint, Guid*, int, void*, nint*, int>)(*(void***)device)[ActivateSlot];
            int hr = activate(device, &iid, ClsctxAll, null, &volume);

            if (hr < 0 || volume == 0)
            {
                Fail($"activating the {what}'s endpoint volume", hr);
                return 0;
            }

            if (_volumeCallback is null) _volumeCallback = MakeObject(VolumeVtable());

            var register = (delegate* unmanaged[Stdcall]<nint, void*, int>)(*(void***)volume)[RegisterControlChangeNotifySlot];
            hr = register(volume, _volumeCallback);
            if (hr < 0) Log.Warn(LogCategory.Wm, $"could not register for the {what}'s mute changes (hr 0x{hr:X8}); it will be read only on other wake-ups");

            return volume;
        }
        finally
        {
            Release(device);
        }
    }

    /// <summary>
    /// The device's friendly name from its property store, or null when it has none
    /// or the store cannot be read - which is logged once and is not a reason to do
    /// without the device's mute.
    /// </summary>
    /// <remarks>
    /// <c>IMMDevice::OpenPropertyStore</c>, then <c>IPropertyStore::GetValue</c> with
    /// <c>PKEY_Device_FriendlyName</c>; the value is a <c>PROPVARIANT</c> holding an
    /// <c>LPWSTR</c> the caller frees with <c>PropVariantClear</c>. The variant is
    /// twenty-four bytes on x64 - type word, three reserved words, then the union -
    /// and the string pointer sits at offset eight.
    /// </remarks>
    private string? FriendlyName(nint device, string what)
    {
        nint store = 0;

        var open = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)(*(void***)device)[OpenPropertyStoreSlot];
        int hr = open(device, StgmRead, &store);

        if (hr < 0 || store == 0)
        {
            Fail($"opening the {what}'s properties", hr);
            return null;
        }

        try
        {
            var key = new PropertyKey(DeviceFriendlyNameFmtid, DeviceFriendlyNamePid);
            byte* variant = stackalloc byte[24];
            new Span<byte>(variant, 24).Clear();

            var getValue = (delegate* unmanaged[Stdcall]<nint, PropertyKey*, byte*, int>)(*(void***)store)[GetValueSlot];
            hr = getValue(store, &key, variant);

            if (hr < 0)
            {
                Fail($"reading the {what}'s name", hr);
                return null;
            }

            try
            {
                if (*(ushort*)variant != VtLpwstr) return null;

                nint text = *(nint*)(variant + 8);
                string? name = text == 0 ? null : Marshal.PtrToStringUni(text);

                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            finally
            {
                _ = PropVariantClear(variant);
            }
        }
        finally
        {
            Release(store);
        }
    }

    /// <summary>A <c>PROPERTYKEY</c>: a property set and a property within it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid Fmtid = fmtid;
        public readonly uint Pid = pid;
    }

    /// <summary>
    /// The microphone's mute state now, or null when there is no microphone.
    /// </summary>
    /// <remarks>
    /// Read on every wake rather than remembered from the last callback, because a
    /// read is one call and a remembered value is a second copy of the truth.
    /// </remarks>
    public bool? IsMuted() => ReadMute(_volume, "microphone");

    /// <summary>The speaker's mute state now, or null when there is no speaker or it is not followed.</summary>
    public bool? IsSpeakerMuted() => ReadMute(_speakerVolume, "speaker");

    private bool? ReadMute(nint volume, string what)
    {
        if (volume == 0) return null;

        int muted = 0;
        var getMute = (delegate* unmanaged[Stdcall]<nint, int*, int>)(*(void***)volume)[GetMuteSlot];
        int hr = getMute(volume, &muted);

        if (hr < 0)
        {
            Fail($"reading the {what}'s mute", hr);
            return null;
        }

        return muted != 0;
    }

    /// <summary>Sets the microphone's mute. False when there is no microphone or the call failed.</summary>
    public bool SetMuted(bool muted) => WriteMute(_volume, muted, "microphone");

    /// <summary>Sets the speaker's mute. False when there is no speaker or the call failed.</summary>
    public bool SetSpeakerMuted(bool muted) => WriteMute(_speakerVolume, muted, "speaker");

    private bool WriteMute(nint volume, bool muted, string what)
    {
        if (volume == 0) return false;

        Guid context = OurContext;
        var setMute = (delegate* unmanaged[Stdcall]<nint, int, Guid*, int>)(*(void***)volume)[SetMuteSlot];
        int hr = setMute(volume, muted ? 1 : 0, &context);

        return hr >= 0 || Fail($"setting the {what}'s mute", hr);
    }

    /// <summary>
    /// Whether the default device changed since this was last asked. The loop calls
    /// <see cref="Resolve"/> when it did.
    /// </summary>
    public static bool TakeDefaultDeviceChanged() => Interlocked.Exchange(ref s_defaultDeviceChanged, 0) != 0;

    public void Dispose()
    {
        ReleaseVolume(ref _volume);
        ReleaseVolume(ref _speakerVolume);

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

    private nint DefaultDevice(int flow, int role, string what)
    {
        nint device = 0;
        var get = (delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)(*(void***)_enumerator)[GetDefaultAudioEndpointSlot];
        int hr = get(_enumerator, flow, role, &device);

        if (hr == ENotFound) return 0;
        if (hr < 0 || device == 0)
        {
            Fail($"finding the default {what}", hr);
            return 0;
        }

        return device;
    }

    private void ReleaseVolume(ref nint volume)
    {
        if (volume == 0) return;

        if (_volumeCallback is not null)
        {
            var unregister = (delegate* unmanaged[Stdcall]<nint, void*, int>)(*(void***)volume)[UnregisterControlChangeNotifySlot];
            unregister(volume, _volumeCallback);
        }

        Release(volume);
        volume = 0;
    }

    /// <summary>
    /// Says what failed, once per distinct failure. The same call failing on every wake
    /// - a microphone that answers nothing while it sleeps - is one line, not a line per
    /// wake; a different call failing later is still said, where a flag set by the first
    /// failure for the life of the process would have hidden it.
    /// </summary>
    private bool Fail(string what, int hr)
    {
        string complaint = $"Core Audio: {what} failed (hr 0x{hr:X8}); its mute will not be reported";

        if (!string.Equals(complaint, _lastComplaint, StringComparison.Ordinal))
        {
            _lastComplaint = complaint;
            Log.Warn(LogCategory.Wm, complaint);
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

    /// <summary>
    /// A device was enabled, disabled, unplugged or plugged in. Treated as the default
    /// changing, because it may well have: the only microphone unplugged leaves no
    /// default, and reading the mute off the endpoint we still hold answered the last
    /// thing it said for ever - a context held for a microphone that was in a drawer.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceStateChanged(void* self, ushort* id, uint state)
    {
        Interlocked.Exchange(ref s_defaultDeviceChanged, 1);
        Changed.Set();
        return SOk;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDeviceAddedOrRemoved(void* self, ushort* id)
    {
        Interlocked.Exchange(ref s_defaultDeviceChanged, 1);
        Changed.Set();
        return SOk;
    }

    /// <summary>A new default microphone or speaker: the loop re-resolves and re-reads.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnDefaultDeviceChanged(void* self, int flow, int role, ushort* id)
    {
        if (flow is ECapture or ERender && role is ECommunications or EConsole)
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

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(byte* pvar);
}
