using Shubbak.Native;

namespace Shubbak.Native.Tests;

/// <summary>
/// Prints what the display configuration API says about this machine, as test output.
/// </summary>
/// <remarks>
/// Not an assertion, a window. Run with <c>dotnet test --logger "console;verbosity=detailed"</c>
/// and filter on the name to read it. It exists so that "what does my laptop report"
/// can be answered without writing a program.
/// </remarks>
public sealed class DisplayTopologyReport(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void WhatThisMachineReports()
    {
        output.WriteLine($"topology: {DisplayTopology.Current()}");
        output.WriteLine($"remote:   {DisplayPreferences.IsRemoteSession()}");

        foreach (DisplayTarget target in DisplayTopology.Targets())
        {
            output.WriteLine(
                $"{target.DeviceId,-14} internal={target.IsInternal,-5} " +
                $"name=\"{target.FriendlyName}\" path={target.DevicePath}");
        }

        foreach (MonitorInfo monitor in MonitorSource.Enumerate())
        {
            output.WriteLine(
                $"{monitor.DeviceId,-14} primary={monitor.IsPrimary,-5} " +
                $"{monitor.Bounds.Width}x{monitor.Bounds.Height} @ {monitor.Dpi} dpi");
        }
    }
}
