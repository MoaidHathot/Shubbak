using System.Diagnostics;
using Shubbak.Core.Wm;
using Shubbak.Native;
using Windows.Win32.Devices.Display;

namespace Shubbak.Native.Tests;

/// <summary>
/// Asking the display configuration API what each display is.
/// </summary>
/// <remarks>
/// <para>
/// The monitor enumeration names displays <c>\\.\DISPLAY1</c>, <c>\\.\DISPLAY2</c> in
/// the order it finds them, and that is all it says. The EDID name, the connector's
/// device path and whether a panel is built in all live behind
/// <c>QueryDisplayConfig</c>, and this is the first code in the program to ask.
/// </para>
/// <para>
/// Real-desktop tests, in the same spirit as <see cref="UserActivityTests"/>: they
/// assert the P/Invoke shape is right and the join to the enumeration holds on the
/// machine being built on, and decline to assert anything about how many monitors
/// that machine has or what they are called. A build agent has a display and no EDID;
/// a laptop has a panel with no friendly name; a remote session has neither.
/// </para>
/// </remarks>
public sealed class DisplayTopologyTests
{
    [Fact]
    public void EveryEnumeratedMonitorHasATarget()
    {
        // The join is the whole point. A target without a GDI name is useless to the
        // daemon, and an enumerated monitor without a target means the two APIs
        // disagree about what is attached - which is the case that would make named
        // monitors silently fall back to indices.
        IReadOnlyList<MonitorInfo> monitors = MonitorSource.Enumerate();
        IReadOnlyList<DisplayTarget> targets = DisplayTopology.Targets();

        // No display at all is a legitimate build agent, and there is nothing to join.
        if (monitors.Count == 0) return;

        Assert.NotEmpty(targets);

        foreach (MonitorInfo monitor in monitors)
        {
            Assert.Contains(targets, t =>
                string.Equals(t.DeviceId, monitor.DeviceId, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TargetsCarryADevicePathWhenTheMachineHasOne()
    {
        // The device path is the stable identity the GDI name is not, so a target
        // without one has lost the property that justifies asking. Not asserted for
        // every target - a remote session's virtual display has none - but at least one
        // real display on a machine with displays should.
        IReadOnlyList<DisplayTarget> targets = DisplayTopology.Targets();

        if (targets.Count == 0) return;
        if (DisplayPreferences.IsRemoteSession()) return;

        Assert.Contains(targets, t => t.DevicePath.Length > 0);

        foreach (DisplayTarget target in targets)
        {
            // Never null: a name that could not be read is empty, so a caller can fall
            // back to the GDI name with one comparison and no null check.
            Assert.NotNull(target.FriendlyName);
            Assert.NotNull(target.DevicePath);
            Assert.StartsWith(@"\\.\DISPLAY", target.DeviceId, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheTopologyIsOneOfTheFourChoices()
    {
        // Unknown is a failed call. On a machine with any display the call succeeds and
        // the answer is one of the four things Win+P offers.
        if (MonitorSource.Enumerate().Count == 0) return;

        DisplayTopologyKind topology = DisplayTopology.Current();

        Assert.NotEqual(DisplayTopologyKind.Unknown, topology);
    }

    [Fact]
    public void RepeatedCallsAgree()
    {
        // No caching, no state: two reads a moment apart describe the same desktop.
        Assert.Equal(DisplayTopology.Targets(), DisplayTopology.Targets());
        Assert.Equal(DisplayTopology.Current(), DisplayTopology.Current());
    }

    [Fact]
    public void ARoundTripIsCheapEnoughToRunWhenMonitorsChange()
    {
        // Not a benchmark, and deliberately loose - a build agent is a busy machine.
        // What it guards is the budget the daemon spends this from: it is called when
        // the monitor enumeration changes, on the daemon thread, and a call that took
        // tens of milliseconds would be a hitch on every dock and undock. Ten
        // iterations well under a second says it is not that.
        var clock = Stopwatch.StartNew();

        for (int i = 0; i < 10; i++)
        {
            DisplayTopology.Targets();
            DisplayTopology.Current();
        }

        Assert.True(clock.ElapsedMilliseconds < 1_000,
            $"twenty display-configuration reads took {clock.ElapsedMilliseconds} ms");
    }

    // The connector enum is one CsWin32 generates and is internal to the library, so
    // the mapping is exposed over the raw value - which is also what the documentation
    // lists, and what these write.

    [Theory]
    [InlineData(unchecked((int)0x80000000))] // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
    [InlineData(11)] // DISPLAYPORT_EMBEDDED
    [InlineData(13)] // UDI_EMBEDDED
    public void BuiltInPanelsAreInternal(int technology)
    {
        // eDP is what most laptop panels are wired with, and a driver that reports the
        // link rather than the panel says EMBEDDED. A rule that only knew INTERNAL
        // would call a great many laptop screens external.
        Assert.True(DisplayTopology.IsInternalConnector(technology));
    }

    [Theory]
    [InlineData(5)] // HDMI
    [InlineData(4)] // DVI
    [InlineData(10)] // DISPLAYPORT_EXTERNAL
    [InlineData(18)] // DISPLAYPORT_USB_TUNNEL
    [InlineData(15)] // MIRACAST
    [InlineData(17)] // INDIRECT_VIRTUAL
    [InlineData(-1)] // OTHER
    public void EverythingElseIsExternal(int technology)
    {
        // OTHER included: it is what a remote session's display reports, and a remote
        // session is the case where "is this the laptop panel" must be answered no.
        Assert.False(DisplayTopology.IsInternalConnector(technology));
    }

    [Fact]
    public void TheRawValuesAboveAreTheEnumsValues()
    {
        // Pins the comments in the theories to the generated enum - this project
        // generates its own copy from the same metadata - so a metadata update that
        // renumbered anything would fail here rather than silently test the wrong
        // connector.
        Assert.Equal(unchecked((int)0x80000000), (int)DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL);
        Assert.Equal(11, (int)DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED);
        Assert.Equal(13, (int)DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED);
        Assert.Equal(5, (int)DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI);
        Assert.Equal(-1, (int)DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER);
    }
}
