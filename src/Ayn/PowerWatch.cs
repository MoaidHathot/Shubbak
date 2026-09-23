using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ayn.Core;
using Shubbak.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Ayn;

/// <summary>
/// The mains lead, the battery, the lid and the person at the keyboard, and a way to
/// be woken when any of them changes.
/// </summary>
/// <remarks>
/// <para>
/// Windows already knows all four and will say when they change: power-setting
/// notifications, delivered to a callback on a system thread with no window of ours
/// involved. Each callback does nothing but note the value and set an event the
/// watcher's loop is already waiting on, the same shape as the audio endpoint. There
/// is no timer and no polling; a laptop on battery costs this nothing until something
/// about its power changes.
/// </para>
/// <para>
/// The first notification for each setting arrives on registration with the current
/// value, so the picture is whole a moment after opening; the mains and the battery
/// are also read directly at open, for a machine that is slow about the first
/// notification. The lid is taken as open and the user as present until Windows says
/// otherwise, since neither has a way of being asked.
/// </para>
/// <para>
/// "Away" is Windows's own judgement - <c>GUID_SESSION_USER_PRESENCE</c>, the one that
/// dims the display - rather than an idle timer of ours, so it agrees with the lock
/// screen about when nobody is there.
/// </para>
/// </remarks>
internal sealed unsafe class PowerWatch : IDisposable
{
    /// <summary>Set by the callbacks; the loop waits on it.</summary>
    public AutoResetEvent Changed { get; } = new(false);

    private readonly Lock _gate = new();
    private readonly List<HPOWERNOTIFY> _registrations = [];
    private DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS* _parameters;
    private GCHandle _self;

    private bool _onBattery;
    private int? _batteryPercent;
    private bool _lidClosed;
    private bool _userAway;

    /// <summary>Whether anything was registered; false on a machine that refused every one.</summary>
    public bool IsOpen => _registrations.Count > 0;

    /// <summary>Registers for the four settings and reads what can be read at once.</summary>
    public bool Open()
    {
        if (IsOpen) return true;

        Seed();

        _self = GCHandle.Alloc(this);
        _parameters = (DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS*)NativeMemory.AllocZeroed((nuint)sizeof(DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS));
        _parameters->Callback = &OnSetting;
        _parameters->Context = (void*)GCHandle.ToIntPtr(_self);

        foreach ((Guid setting, string name) in new[]
                 {
                     (PInvoke.GUID_ACDC_POWER_SOURCE, "the power source"),
                     (PInvoke.GUID_BATTERY_PERCENTAGE_REMAINING, "the battery"),
                     (PInvoke.GUID_LIDSWITCH_STATE_CHANGE, "the lid"),
                     (PInvoke.GUID_SESSION_USER_PRESENCE, "the user's presence"),
                 })
        {
            Guid guid = setting;
            void* registration = null;

            WIN32_ERROR result = PInvoke.PowerSettingRegisterNotification(
                &guid,
                REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_CALLBACK,
                new HANDLE((nint)_parameters),
                &registration);

            if (result != WIN32_ERROR.ERROR_SUCCESS)
            {
                // A desktop has no lid and says so; that is not a fault.
                Log.Debug(LogCategory.Wm, $"could not register for changes to {name} (error {(uint)result})");
                continue;
            }

            _registrations.Add(new HPOWERNOTIFY((nint)registration));
        }

        if (!IsOpen)
        {
            Log.Warn(LogCategory.Wm, "no power notifications could be registered; the power facts will not be reported");
            Close();
        }

        return IsOpen;
    }

    /// <summary>What the machine's power says now.</summary>
    public PowerReading Read()
    {
        lock (_gate) return new PowerReading(_onBattery, _batteryPercent, _lidClosed, _userAway);
    }

    /// <summary>The mains and the battery, asked directly.</summary>
    private void Seed()
    {
        if (!PInvoke.GetSystemPowerStatus(out SYSTEM_POWER_STATUS status)) return;

        lock (_gate)
        {
            // 0 is offline, 1 online, 255 unknown - and unknown is not on battery.
            _onBattery = status.ACLineStatus == 0;

            // 128 in the flag is "no system battery"; 255 in the percent is unknown.
            _batteryPercent = (status.BatteryFlag & 128) != 0 || status.BatteryLifePercent > 100
                ? null
                : status.BatteryLifePercent;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnSetting(void* context, uint type, void* setting)
    {
        try
        {
            if (type != PInvoke.PBT_POWERSETTINGCHANGE || setting is null) return 0;
            if (GCHandle.FromIntPtr((nint)context).Target is not PowerWatch self) return 0;

            var broadcast = (POWERBROADCAST_SETTING*)setting;
            if (broadcast->DataLength < sizeof(uint)) return 0;

            // The data follows the header; for all four settings it is one DWORD.
            uint value = *(uint*)((byte*)setting + sizeof(Guid) + sizeof(uint));

            self.Apply(broadcast->PowerSetting, value);
        }
        catch (Exception)
        {
            // A callback on a system thread must not throw; the next change comes anyway.
        }

        return 0;
    }

    private void Apply(Guid setting, uint value)
    {
        lock (_gate)
        {
            if (setting == PInvoke.GUID_ACDC_POWER_SOURCE)
            {
                // 0 mains, 1 battery, 2 a short-term source such as a UPS - which is not
                // the battery the file means.
                _onBattery = value == 1;
            }
            else if (setting == PInvoke.GUID_BATTERY_PERCENTAGE_REMAINING)
            {
                _batteryPercent = value <= 100 ? (int)value : null;
            }
            else if (setting == PInvoke.GUID_LIDSWITCH_STATE_CHANGE)
            {
                // 0 closed, 1 opened.
                _lidClosed = value == 0;
            }
            else if (setting == PInvoke.GUID_SESSION_USER_PRESENCE)
            {
                // 0 present; 2 inactive, which is Windows's word for away.
                _userAway = value != 0;
            }
            else
            {
                return;
            }
        }

        Changed.Set();
    }

    private void Close()
    {
        foreach (HPOWERNOTIFY registration in _registrations)
            _ = PInvoke.PowerSettingUnregisterNotification(registration);

        _registrations.Clear();

        if (_parameters is not null)
        {
            NativeMemory.Free(_parameters);
            _parameters = null;
        }

        if (_self.IsAllocated) _self.Free();
    }

    public void Dispose()
    {
        Close();
        Changed.Dispose();
    }
}
