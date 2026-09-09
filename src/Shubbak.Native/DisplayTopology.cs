using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace Shubbak.Native;

/// <summary>
/// What a display <i>is</i>, as opposed to where it is.
/// </summary>
/// <param name="DeviceId">
/// The GDI name, <c>\\.\DISPLAY1</c>, which is what <see cref="MonitorInfo.DeviceId"/>
/// and every other monitor API in the program already uses. The join key.
/// </param>
/// <param name="FriendlyName">
/// The name the panel reports in its EDID - <c>DELL U2723QE</c>, <c>LG ULTRAWIDE</c> -
/// or empty when there is none to report. Built-in panels usually have none; a remote
/// session never does.
/// </param>
/// <param name="DevicePath">
/// The connector's device interface path, <c>\\?\DISPLAY#DELA1D2#5&amp;...#{...}</c>.
/// Stable for a given panel on a given port across replug, renumbering and reboot,
/// which is the property the GDI name lacks and the reason to have this at all.
/// </param>
/// <param name="IsInternal">
/// Whether the panel is built into the machine. Answered by the connector type, not
/// guessed from the name or the position.
/// </param>
public readonly record struct DisplayTarget(
    string DeviceId,
    string FriendlyName,
    string DevicePath,
    bool IsInternal);

/// <summary>
/// The arrangement the user chose with Win+P.
/// </summary>
/// <remarks>
/// Named for the four choices on that panel rather than the constants behind them,
/// because the choice is what a person recognises. <c>Unknown</c> is a failed call or
/// a machine with no display at all.
/// </remarks>
public enum DisplayTopologyKind
{
    /// <summary>Could not be read.</summary>
    Unknown,

    /// <summary>"PC screen only": the built-in panel and nothing else.</summary>
    Internal,

    /// <summary>"Duplicate": every display shows the same desktop.</summary>
    Clone,

    /// <summary>"Extend": one desktop across several displays.</summary>
    Extend,

    /// <summary>"Second screen only": the built-in panel is off.</summary>
    External,
}

/// <summary>
/// The display configuration API, asked the two questions the monitor enumeration
/// cannot answer: which physical panel is behind each GDI name, and which Win+P
/// arrangement is in force.
/// </summary>
/// <remarks>
/// <para>
/// <c>EnumDisplayMonitors</c> hands out <c>\\.\DISPLAY1</c>, <c>\\.\DISPLAY2</c> in
/// enumeration order and reuses the names, so the same panel can be <c>DISPLAY1</c>
/// before undocking and <c>DISPLAY2</c> after, and nothing in that API says which
/// panel it is. The comment on <c>MonitorNode.DeviceId</c> called it a device path,
/// which it never was. This is where the device path lives.
/// </para>
/// <para>
/// Cost: a buffer-size call, one <c>QueryDisplayConfig</c>, and two
/// <c>DisplayConfigGetDeviceInfo</c> calls per active path - a few hundred
/// microseconds all told, and a handful of small allocations for the strings. Cheap,
/// and still not something to do twice a second for the life of the process, which is
/// why the daemon asks only when the monitor enumeration has actually changed.
/// </para>
/// <para>
/// Every failure is reported as an absence - an empty list, <c>Unknown</c>, an empty
/// name - and never thrown. A display API that fails is a session in a strange state,
/// and a window manager in a strange state should carry on with less information
/// rather than stop.
/// </para>
/// </remarks>
public static class DisplayTopology
{
    /// <summary>Every active display, described by what it is.</summary>
    public static unsafe IReadOnlyList<DisplayTarget> Targets()
    {
        if (!QueryPaths(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out DISPLAYCONFIG_PATH_INFO[] paths, out _))
            return [];

        List<DisplayTarget> targets = new(paths.Length);

        foreach (DISPLAYCONFIG_PATH_INFO path in paths)
        {
            // QDC_ONLY_ACTIVE_PATHS should already have filtered, and the flag is
            // still checked: the documentation promises the filter, and a path that
            // is not active has no GDI name to join on anyway.
            if ((path.flags & PInvoke.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;

            string deviceId = SourceName(path.sourceInfo.adapterId, path.sourceInfo.id);
            if (deviceId.Length == 0) continue;

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                },
            };

            string friendlyName = string.Empty;
            string devicePath = string.Empty;

            // The path's own outputTechnology is the fallback: the target-name call is
            // the one that can fail on an odd session, and the connector type is
            // reported in both places.
            DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY technology = path.targetInfo.outputTechnology;

            if (PInvoke.DisplayConfigGetDeviceInfo(ref target.header) == (int)WIN32_ERROR.ERROR_SUCCESS)
            {
                friendlyName = target.monitorFriendlyDeviceName.ToString();
                devicePath = target.monitorDevicePath.ToString();
                technology = target.outputTechnology;
            }

            targets.Add(new DisplayTarget(deviceId, friendlyName, devicePath, IsInternal(technology)));
        }

        return targets;
    }

    /// <summary>The Win+P arrangement currently in force.</summary>
    public static DisplayTopologyKind Current()
    {
        if (!QueryPaths(QUERY_DISPLAY_CONFIG_FLAGS.QDC_DATABASE_CURRENT, out _, out DISPLAYCONFIG_TOPOLOGY_ID topology))
            return DisplayTopologyKind.Unknown;

        return topology switch
        {
            DISPLAYCONFIG_TOPOLOGY_ID.DISPLAYCONFIG_TOPOLOGY_INTERNAL => DisplayTopologyKind.Internal,
            DISPLAYCONFIG_TOPOLOGY_ID.DISPLAYCONFIG_TOPOLOGY_CLONE => DisplayTopologyKind.Clone,
            DISPLAYCONFIG_TOPOLOGY_ID.DISPLAYCONFIG_TOPOLOGY_EXTEND => DisplayTopologyKind.Extend,
            DISPLAYCONFIG_TOPOLOGY_ID.DISPLAYCONFIG_TOPOLOGY_EXTERNAL => DisplayTopologyKind.External,
            _ => DisplayTopologyKind.Unknown,
        };
    }

    /// <summary>
    /// Whether a connector type means a panel built into the machine.
    /// </summary>
    /// <param name="outputTechnology">
    /// A <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c> value, as the raw number the
    /// documentation lists. The generated enum is internal to this assembly, and the
    /// number is what a test can write without it.
    /// </param>
    /// <remarks>
    /// <c>INTERNAL</c> is the documented answer. The two <c>EMBEDDED</c> values are the
    /// same answer given by drivers that describe the link rather than the panel - eDP
    /// is what most laptop panels are actually wired with - so a rule that only knew
    /// <c>INTERNAL</c> would call a great many laptop screens external.
    /// </remarks>
    public static bool IsInternalConnector(int outputTechnology) =>
        (DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY)outputTechnology is
            DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL or
            DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED or
            DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;

    private static bool IsInternal(DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY technology) =>
        IsInternalConnector((int)technology);

    /// <summary>
    /// Asks for the path list, retrying if the display configuration changed between
    /// the size query and the read.
    /// </summary>
    /// <remarks>
    /// <c>ERROR_INSUFFICIENT_BUFFER</c> is the documented race: a monitor arrived
    /// between the two calls. Twice is enough; a machine whose display configuration
    /// changes faster than that has bigger problems than a stale monitor list.
    /// </remarks>
    private static unsafe bool QueryPaths(
        QUERY_DISPLAY_CONFIG_FLAGS flags,
        out DISPLAYCONFIG_PATH_INFO[] paths,
        out DISPLAYCONFIG_TOPOLOGY_ID topology)
    {
        paths = [];
        topology = default;

        bool wantsTopology = (flags & QUERY_DISPLAY_CONFIG_FLAGS.QDC_DATABASE_CURRENT) != 0;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (PInvoke.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount)
                != WIN32_ERROR.ERROR_SUCCESS)
            {
                return false;
            }

            var pathBuffer = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modeBuffer = new DISPLAYCONFIG_MODE_INFO[modeCount];

            WIN32_ERROR result;

            fixed (DISPLAYCONFIG_PATH_INFO* pathPtr = pathBuffer)
            fixed (DISPLAYCONFIG_MODE_INFO* modePtr = modeBuffer)
            {
                // The topology pointer must be supplied with QDC_DATABASE_CURRENT and
                // must be null without it; the call fails with ERROR_INVALID_PARAMETER
                // either way round.
                DISPLAYCONFIG_TOPOLOGY_ID topologyOut = default;

                result = PInvoke.QueryDisplayConfig(
                    flags,
                    &pathCount,
                    pathPtr,
                    &modeCount,
                    modePtr,
                    wantsTopology ? &topologyOut : null);

                topology = topologyOut;
            }

            if (result == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER) continue;
            if (result != WIN32_ERROR.ERROR_SUCCESS) return false;

            // The call may return fewer paths than the buffer holds.
            paths = pathCount == pathBuffer.Length ? pathBuffer : pathBuffer[..(int)pathCount];
            return true;
        }

        return false;
    }

    /// <summary>The GDI name behind a source, or empty if it cannot be read.</summary>
    private static unsafe string SourceName(LUID adapter, uint id)
    {
        var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                adapterId = adapter,
                id = id,
            },
        };

        return PInvoke.DisplayConfigGetDeviceInfo(ref source.header) == (int)WIN32_ERROR.ERROR_SUCCESS
            ? source.viewGdiDeviceName.ToString()
            : string.Empty;
    }
}
