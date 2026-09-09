using Shubbak.Ipc;

namespace Shubbak.Cli.Tests;

/// <summary>
/// Printing what each display is, and writing the definition that names it.
/// </summary>
/// <remarks>
/// The fixture is the desk this was written on: two identical Dells. The friendly name
/// is the same for both, so the block that names one of them has to come from the
/// connector path, and the block has to be something a person can read rather than a
/// hundred characters of hexadecimal.
/// </remarks>
public sealed class MonitorReportTextTests
{
    private static MonitorInfoDto Dell(int index, string uid, bool primary) => new(
        Id: index + 1,
        DeviceId: $@"\\.\DISPLAY{index + 1}",
        Primary: primary,
        Dpi: 144,
        X: index * 3840,
        Y: 0,
        Width: 3840,
        Height: 2160,
        ActiveWorkspace: primary ? "1" : "`",
        FriendlyName: "DELL U3219Q",
        DevicePath: $@"\\?\DISPLAY#DELA12{index + 4}#5&38500b75&0&{uid}#{{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}}",
        Internal: false,
        Names: index == 0 ? ["dell-left"] : []);

    private static readonly IReadOnlyList<MonitorInfoDto> TwoDells =
    [
        Dell(0, "UID4355", primary: true),
        Dell(1, "UID4357", primary: false),
    ];

    [Fact]
    public void TwinsGetDefinitionsThatTellThemApart()
    {
        string text = MonitorReportText.Format(TwoDells);

        // The connector id is the shortest distinctive tail of the path, and it is
        // the only thing in the path that differs between the two.
        Assert.Contains("path *= \"UID4355\"", text, StringComparison.Ordinal);
        Assert.Contains("path *= \"UID4357\"", text, StringComparison.Ordinal);

        // The model is there for the reader, as a comment, because the block cannot
        // match on it without matching both.
        Assert.Contains("// DELL U3219Q", text, StringComparison.Ordinal);
        Assert.DoesNotContain("name ~=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("name =", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeaderSaysWhatEachDisplayIsAndWhatItIsCalled()
    {
        string text = MonitorReportText.Format(TwoDells);

        Assert.Contains(@"0  \\.\DISPLAY1, primary  3840x2160 at (0,0), 144 dpi, external", text, StringComparison.Ordinal);
        Assert.Contains(@"1  \\.\DISPLAY2  3840x2160 at (3840,0), 144 dpi, external", text, StringComparison.Ordinal);
        Assert.Contains("called:  \"dell-left\"", text, StringComparison.Ordinal);
        Assert.Contains("called:  (no declared monitor matches it)", text, StringComparison.Ordinal);
        Assert.Contains("showing: 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSuggestedNameIsTheModelAsASlug()
    {
        var alone = new MonitorInfoDto(
            1, @"\\.\DISPLAY1", Primary: true, Dpi: 96, X: 0, Y: 0, Width: 2560, Height: 1440,
            ActiveWorkspace: "1", FriendlyName: "LG ULTRAWIDE", DevicePath: @"\\?\DISPLAY#GSM5B09#4&abc&0&UID256#{g}",
            Internal: false, Names: []);

        string definition = MonitorReportText.Definition(alone, 0, [alone]);

        Assert.StartsWith("   monitor \"lg-ultrawide\" {", definition, StringComparison.Ordinal);
        Assert.Contains("workspace \"...\" monitor=\"lg-ultrawide\"", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void TwinsAreSuggestedDifferentNamesByWhereTheySit()
    {
        // Both would be "dell-u3219q", and pasting both blocks would make the second a
        // duplicate. Where a person put a monitor is the thing they already know.
        Assert.Equal("dell-u3219q-left", MonitorReportText.SuggestedName(TwoDells[0], 0, TwoDells));
        Assert.Equal("dell-u3219q-right", MonitorReportText.SuggestedName(TwoDells[1], 1, TwoDells));

        string text = MonitorReportText.Format(TwoDells);

        Assert.Contains("monitor \"dell-u3219q-left\" {", text, StringComparison.Ordinal);
        Assert.Contains("monitor \"dell-u3219q-right\" {", text, StringComparison.Ordinal);

        // Stacked rather than side by side.
        MonitorInfoDto upper = TwoDells[0] with { X = 0, Y = 0 };
        MonitorInfoDto lower = TwoDells[1] with { X = 0, Y = 2160 };

        Assert.Equal("dell-u3219q-top", MonitorReportText.SuggestedName(upper, 0, [upper, lower]));
        Assert.Equal("dell-u3219q-bottom", MonitorReportText.SuggestedName(lower, 1, [upper, lower]));

        // Three in a row: the middle one is numbered.
        MonitorInfoDto middle = TwoDells[1] with { X = 3840 };
        MonitorInfoDto far = TwoDells[1] with { X = 7680, DeviceId = @"\\.\DISPLAY3" };
        IReadOnlyList<MonitorInfoDto> three = [TwoDells[0], middle, far];

        Assert.Equal("dell-u3219q-left", MonitorReportText.SuggestedName(three[0], 0, three));
        Assert.Equal("dell-u3219q-2", MonitorReportText.SuggestedName(three[1], 1, three));
        Assert.Equal("dell-u3219q-right", MonitorReportText.SuggestedName(three[2], 2, three));
    }

    [Fact]
    public void ABuiltInPanelIsCalledLaptopAndMatchedOnItsKindWhenItHasNoPath()
    {
        var laptop = new MonitorInfoDto(
            3, @"\\.\DISPLAY3", Primary: false, Dpi: 192, X: 7680, Y: 0, Width: 2880, Height: 1800,
            ActiveWorkspace: null, FriendlyName: null, DevicePath: null, Internal: true, Names: []);

        string definition = MonitorReportText.Definition(laptop, 2, [.. TwoDells, laptop]);

        Assert.StartsWith("   monitor \"laptop\" {", definition, StringComparison.Ordinal);
        Assert.Contains("       internal", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("path", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuiltInPanelWithAPathStillMatchesOnThePath()
    {
        // The kind is a fine fallback and a poor first choice: a laptop with two
        // built-in panels exists, and the path is unique where the kind is not.
        var laptop = new MonitorInfoDto(
            3, @"\\.\DISPLAY3", Primary: false, Dpi: 192, X: 7680, Y: 0, Width: 2880, Height: 1800,
            ActiveWorkspace: null, FriendlyName: null,
            DevicePath: @"\\?\DISPLAY#SHP1523#4&1c3f2a1&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            Internal: true, Names: []);

        string definition = MonitorReportText.Definition(laptop, 2, [.. TwoDells, laptop]);

        Assert.StartsWith("   monitor \"laptop\" {", definition, StringComparison.Ordinal);
        Assert.Contains("path *= \"UID8388688\"", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisplayNobodyKnowsAnythingAboutFallsBackToTheDeviceNameAndSaysWhy()
    {
        // A remote session's display: no EDID, no connector, no kind.
        var remote = new MonitorInfoDto(
            1, @"\\.\DISPLAY1", Primary: true, Dpi: 96, X: 0, Y: 0, Width: 1920, Height: 1080,
            ActiveWorkspace: "1", FriendlyName: null, DevicePath: null, Internal: null, Names: null);

        string definition = MonitorReportText.Definition(remote, 0, [remote]);

        Assert.StartsWith("   monitor \"main\" {", definition, StringComparison.Ordinal);
        Assert.Contains(@"device = ""\\\\.\\DISPLAY1""", definition, StringComparison.Ordinal);
        Assert.Contains("changes on replug", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDistinctiveSegmentWidensUntilItIsUnique()
    {
        const string path = @"\\?\DISPLAY#DELA124#5&38500b75&0&UID4355#{guid}";

        // Alone: the connector id.
        Assert.Equal("UID4355", MonitorReportText.DistinctiveSegment(path, []));

        // Against a twin with a different connector id: still the connector id.
        Assert.Equal("UID4355", MonitorReportText.DistinctiveSegment(path,
            [@"\\?\DISPLAY#DELA133#5&38500b75&0&UID4357#{guid}"]));

        // Against a rival sharing the connector id: one segment more.
        Assert.Equal("0&UID4355", MonitorReportText.DistinctiveSegment(path,
            [@"\\?\DISPLAY#DELA133#5&38500b75&1&UID4355#{guid}"]));

        // Against an identical path: the whole body, escaped for KDL, rather than
        // nothing.
        Assert.Equal(@"\\\\?\\DISPLAY#DELA124#5&38500b75&0&UID4355", MonitorReportText.DistinctiveSegment(path, [path]));
    }

    [Fact]
    public void NoDisplaysIsSaidRatherThanPrintedAsNothing()
    {
        Assert.Contains("no displays attached", MonitorReportText.Format([]), StringComparison.Ordinal);
    }
}
