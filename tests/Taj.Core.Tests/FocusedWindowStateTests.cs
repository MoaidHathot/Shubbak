using Shubbak.Ipc;
using System.Text.Json;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// Showing which window state the focused window is in.
/// </summary>
/// <remarks>
/// <para>
/// Shubbak has two fullscreen modes that look identical once a window is in one of
/// them: fullscreen stops at the bar, and monitor-fullscreen covers it. The second is
/// the more confusing of the two precisely because it works - the bar is gone, so the
/// bar cannot say why.
/// </para>
/// <para>
/// The indicator is therefore worth having for the first mode, where the bar is still
/// on screen, and is by construction impossible for the second. That is the honest
/// limit of this feature and it is why the glyph pair reads as an intensity rather
/// than as two unrelated markers.
/// </para>
/// </remarks>
public sealed class FocusedWindowStateTests
{
    private static string Icon(string state) => Template.Render(
        "{{ window.state | state-icon }}",
        new Dictionary<string, string?> { ["window.state"] = state });

    [Theory]
    [InlineData("fullscreen")]
    [InlineData("monitorfullscreen")]
    public void TheStatesWorthMarkingGetAGlyph(string state)
    {
        Assert.NotEmpty(Icon(state));
    }

    [Theory]
    [InlineData("tiling")]
    [InlineData("floating")]
    [InlineData("minimised")]
    [InlineData("")]
    [InlineData("something-new")]
    public void OrdinaryStatesGetNothing(string state)
    {
        // Empty rather than a placeholder, so a widget with hide-when-empty leaves no
        // gap. A marker that is always lit is not a marker.
        Assert.Equal(string.Empty, Icon(state));
    }

    [Fact]
    public void TheTwoFullscreenModesLookDifferent()
    {
        Assert.NotEqual(Icon("fullscreen"), Icon("monitorfullscreen"));
    }

    [Theory]
    [InlineData("fullscreen")]
    [InlineData("monitorfullscreen")]
    public void EveryStateIconIsAGlyphSegoeUiVariableActuallyHas(string state)
    {
        // Same constraint as the layout icons, and the same reason: a glyph the bar
        // font lacks is measured at one width, borrowed from a substitute font at
        // another, and drawn clipped. That reads as a rendering fault.
        foreach (char c in Icon(state))
        {
            bool covered =
                (c >= '\u2500' && c <= '\u259F') ||   // box drawing and block elements
                c == '\u25A0' || c == '\u25A1' ||     // the two squares that are present
                c == '\u2261';                        // identical to

            Assert.True(
                covered,
                $"'{state}' uses U+{(int)c:X4}, which is outside the ranges the bar " +
                "font covers; it will be borrowed from another font, mismeasured, " +
                "and clipped");
        }
    }

    // ---- what the bar is told -----------------------------------------------

    private static string PayloadFor(string state, bool focused) =>
        JsonSerializer.Serialize(
            new WindowInfo(1, 42, "A title", "SomeClass", "someprocess", state, focused, 0, 0, 8, 6),
            IpcJsonContext.Default.WindowInfo);

    [Fact]
    public void TheStateOfTheFocusedWindowReachesTheBar()
    {
        FocusedWindowValues values =
            Assert.NotNull(FocusedWindow.Parse(PayloadFor("monitorfullscreen", focused: true)));

        Assert.Equal("monitorfullscreen", values.State);
        Assert.Equal("A title", values.Title);
        Assert.Equal("someprocess", values.Process);
    }

    [Fact]
    public void AnUnfocusedWindowChangingLeavesTheBarAlone()
    {
        // window.title_changed and window.state_changed both fire for any window, and
        // carry the window that changed rather than the focused one. Without this the
        // bar showed whichever background window had most recently retitled itself -
        // which for a chat client updating an unread count is several times a minute.
        Assert.Null(FocusedWindow.Parse(PayloadFor("tiling", focused: false)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    public void FocusGoingAwayEmptiesTheBar(string payload)
    {
        FocusedWindowValues values = Assert.NotNull(FocusedWindow.Parse(payload));

        Assert.Equal(FocusedWindowValues.None, values);
    }

    [Fact]
    public void AMalformedPayloadChangesNothing()
    {
        // Not an exception, and not a blank bar either: a corrupt message should cost
        // nothing more than the update it was carrying.
        Assert.Null(FocusedWindow.Parse("{\"this\": is not json"));
    }
}
