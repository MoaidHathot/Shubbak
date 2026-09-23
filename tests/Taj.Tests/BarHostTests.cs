using Shubbak.Core.Geometry;
using Taj.Core;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Taj.Tests;

/// <summary>Where a profile puts its bar on a display.</summary>
/// <remarks>
/// A docked bar is its strip. A floating one is inset by its margin from the screen
/// edge and from both sides, and the strip is deepened by the same margin on the inner
/// side, so the room above the bar and the room below it match without the window
/// manager's gaps having to know about either.
/// </remarks>
public sealed class BarGeometryTests
{
    private static readonly Rect Monitor = new(0, 0, 1920, 1080);

    private static BarProfile Profile(BarEdge edge, int height, int margin = 0) =>
        TajConfigLoader.CreateDefault().Default with { Edge = edge, Height = height, Margin = margin };

    [Fact]
    public void ADockedTopBarIsItsStrip()
    {
        (Rect strip, Rect window) = BarWindow.Geometry(Monitor, Profile(BarEdge.Top, 30));

        Assert.Equal(new Rect(0, 0, 1920, 30), strip);
        Assert.Equal(strip, window);
    }

    [Fact]
    public void ADockedBottomBarSitsOnTheBottomEdge()
    {
        (Rect strip, Rect window) = BarWindow.Geometry(Monitor, Profile(BarEdge.Bottom, 30));

        Assert.Equal(new Rect(0, 1050, 1920, 30), strip);
        Assert.Equal(strip, window);
    }

    [Fact]
    public void AFloatingBarIsInsetAndReservesItsMarginOnBothSides()
    {
        (Rect strip, Rect window) = BarWindow.Geometry(Monitor, Profile(BarEdge.Top, 30, margin: 8));

        // The strip is the height plus the margin above and the margin below.
        Assert.Equal(new Rect(0, 0, 1920, 46), strip);

        // The window sits inside it, a margin in from every edge.
        Assert.Equal(new Rect(8, 8, 1904, 30), window);
    }

    [Fact]
    public void AFloatingBottomBarKeepsItsMarginFromTheBottom()
    {
        (Rect strip, Rect window) = BarWindow.Geometry(Monitor, Profile(BarEdge.Bottom, 30, margin: 8));

        Assert.Equal(new Rect(0, 1034, 1920, 46), strip);
        Assert.Equal(new Rect(8, 1042, 1904, 30), window);
        Assert.Equal(Monitor.Bottom - 8, window.Bottom);
    }

    [Fact]
    public void ANegativeMarginIsReadAsNone()
    {
        (Rect strip, Rect window) = BarWindow.Geometry(Monitor, Profile(BarEdge.Top, 30, margin: -5));

        Assert.Equal(new Rect(0, 0, 1920, 30), strip);
        Assert.Equal(strip, window);
    }

    [Fact]
    public void ADisplayThatIsNotAtTheOriginIsHonoured()
    {
        var second = new Rect(1920, -200, 2560, 1440);

        (Rect strip, Rect window) = BarWindow.Geometry(second, Profile(BarEdge.Top, 34, margin: 6));

        Assert.Equal(new Rect(1920, -200, 2560, 46), strip);
        Assert.Equal(new Rect(1926, -194, 2548, 34), window);
    }
}

/// <summary>Which keyboard layout is next.</summary>
public sealed class KeyboardLanguageTests
{
    private static unsafe HKL Layout(int value) => new((void*)value);

    private static unsafe int Id(HKL layout) => (int)layout.Value;

    // The low word is the language, the high word the physical layout.
    private const int EnglishUs = 0x04090409;
    private const int Hebrew = 0x040D040D;
    private const int Arabic = 0x04010401;

    [Fact]
    public void NextWrapsAroundTheEnd()
    {
        HKL[] installed = [Layout(EnglishUs), Layout(Hebrew), Layout(Arabic)];

        Assert.Equal(Hebrew, Id(KeyboardLanguage.Step(installed, Layout(EnglishUs), +1)));
        Assert.Equal(Arabic, Id(KeyboardLanguage.Step(installed, Layout(Hebrew), +1)));
        Assert.Equal(EnglishUs, Id(KeyboardLanguage.Step(installed, Layout(Arabic), +1)));
    }

    [Fact]
    public void PreviousWrapsAroundTheStart()
    {
        HKL[] installed = [Layout(EnglishUs), Layout(Hebrew), Layout(Arabic)];

        Assert.Equal(Arabic, Id(KeyboardLanguage.Step(installed, Layout(EnglishUs), -1)));
        Assert.Equal(EnglishUs, Id(KeyboardLanguage.Step(installed, Layout(Hebrew), -1)));
    }

    [Fact]
    public void ALayoutKnownOnlyByItsLanguageIsStillFound()
    {
        // The window reports an English layout on a different physical keyboard than
        // the one installed: same low word, different high word. Matched by language.
        HKL[] installed = [Layout(EnglishUs), Layout(Hebrew)];

        Assert.Equal(Hebrew, Id(KeyboardLanguage.Step(installed, Layout(0x00000409), +1)));
    }

    [Fact]
    public void AnUnknownLayoutStepsToAnEnd()
    {
        HKL[] installed = [Layout(EnglishUs), Layout(Hebrew)];

        Assert.Equal(EnglishUs, Id(KeyboardLanguage.Step(installed, Layout(0x12345678), +1)));
        Assert.Equal(Hebrew, Id(KeyboardLanguage.Step(installed, Layout(0x12345678), -1)));
    }

    [Fact]
    public void TheCodeIsTheTwoLetterLanguage()
    {
        Assert.Equal("EN", KeyboardLanguage.Code(Layout(EnglishUs)));
        Assert.Equal("HE", KeyboardLanguage.Code(Layout(Hebrew)));
        Assert.Equal("AR", KeyboardLanguage.Code(Layout(Arabic)));
    }
}
