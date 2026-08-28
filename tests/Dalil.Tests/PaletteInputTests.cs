using Dalil;
using Dalil.Core;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dalil.Tests;

/// <summary>
/// What a keystroke means in the palette.
/// </summary>
/// <remarks>
/// These decisions used to live inside <c>PaletteWindow</c>, which cannot be built
/// without a real window, a message loop and a device context - so none of them were
/// tested, including the ones that decide whether a keypress closes a window or
/// explains one.
/// </remarks>
public class PaletteInputTests
{
    private static PaletteEntry Entry(
        string primary = "a window",
        string secondary = "",
        string command = "focus-window 42",
        PaletteMode? switchesTo = null,
        IReadOnlyList<PaletteAction>? actions = null,
        long? explains = null,
        string? expands = null) =>
        new(primary, secondary, [], command, 0, switchesTo, actions, explains, expands);

    // ---- chords ------------------------------------------------------------------

    /// <summary>
    /// The chord a letter spells, named the way a person would say it.
    /// </summary>
    /// <remarks>
    /// A letter's virtual-key code is its uppercase ASCII value, so this reads as the
    /// key actually pressed. The cast is here rather than in the signature because
    /// CsWin32 generates <c>VIRTUAL_KEY</c> as internal, and a public test method
    /// cannot name it.
    /// </remarks>
    private static string? Chord(char letter, bool control = true, bool shift = true) =>
        PaletteInput.ChordFor((VIRTUAL_KEY)char.ToUpperInvariant(letter), control, shift);

    [Theory]
    [InlineData('F', "Ctrl+Shift+F")]
    [InlineData('S', "Ctrl+Shift+S")]
    [InlineData('M', "Ctrl+Shift+M")]
    [InlineData('A', "Ctrl+Shift+A")]
    [InlineData('W', "Ctrl+Shift+W")]
    [InlineData('I', "Ctrl+Shift+I")]
    public void AChordIsSpelledTheWayTheActionAdvertisesIt(char letter, string expected)
    {
        // The lookup matches on this string, so the key that produces a chord and the
        // label that advertises it cannot drift apart without the lookup simply
        // finding nothing - which is the failure that is easiest to notice.
        Assert.Equal(expected, Chord(letter));
    }

    [Fact]
    public void AChordNeedsBothModifiers()
    {
        Assert.Null(Chord('I', control: true, shift: false));
        Assert.Null(Chord('I', control: false, shift: true));
        Assert.Null(Chord('I', control: false, shift: false));
    }

    [Fact]
    public void AnOrdinaryLetterIsNotAChord()
    {
        // Otherwise typing would run things. The search box is the default consumer of
        // every key, and that has to stay true.
        Assert.Null(Chord('X'));
        Assert.Null(PaletteInput.ChordFor(VIRTUAL_KEY.VK_RETURN, control: false, shift: false));
    }

    [Fact]
    public void OnlyInspectingIsExemptFromTheGuard()
    {
        // The guard exists so an action cannot be taken by accident, and inspecting
        // takes no action. Every other chord does something to a window and stays
        // behind it.
        Assert.True(PaletteInput.IsExemptFromGuard("Ctrl+Shift+I"));

        foreach (string chord in new[] { "Ctrl+Shift+F", "Ctrl+Shift+S", "Ctrl+Shift+M", "Ctrl+Shift+A", "Ctrl+Shift+W" })
            Assert.False(PaletteInput.IsExemptFromGuard(chord), chord);
    }

    [Fact]
    public void AChordAlwaysActsInsideTheActionList()
    {
        // The bug this exists for. The action list is the only place a chord is written
        // down - every row carries its own as a badge - and it was the one place chords
        // were refused outright. With the shipped default the guard blocked them in the
        // main list too, so every chord but one was inert everywhere while being
        // advertised in a list that could not honour it.
        foreach (string chord in new[] { "Ctrl+Shift+S", "Ctrl+Shift+F", "Ctrl+Shift+W", "Ctrl+Shift+A" })
        {
            Assert.True(PaletteInput.ChordActsHere(chord, insideActionList: true, guard: true), chord);
            Assert.True(PaletteInput.ChordActsHere(chord, insideActionList: true, guard: false), chord);
        }
    }

    [Fact]
    public void TheGuardStillHoldsChordsBackInTheMainList()
    {
        // Which is what the setting is for: the keyboard there is busy searching, and
        // a stray Ctrl+Shift+W would close a window somebody was only looking for.
        Assert.False(PaletteInput.ChordActsHere("Ctrl+Shift+W", insideActionList: false, guard: true));
        Assert.False(PaletteInput.ChordActsHere("Ctrl+Shift+S", insideActionList: false, guard: true));
    }

    [Fact]
    public void TurningTheGuardOffGivesEveryChordToTheMainList()
    {
        // The documented trade: a safety net for speed, as one switch rather than a
        // per-action table nobody would finish filling in.
        foreach (string chord in new[] { "Ctrl+Shift+S", "Ctrl+Shift+F", "Ctrl+Shift+W", "Ctrl+Shift+A" })
            Assert.True(PaletteInput.ChordActsHere(chord, insideActionList: false, guard: false), chord);
    }

    [Fact]
    public void InspectingReachesTheMainListThroughTheGuard()
    {
        Assert.True(PaletteInput.ChordActsHere("Ctrl+Shift+I", insideActionList: false, guard: true));
    }

    // ---- typing ---------------------------------------------------------------------

    [Fact]
    public void APrefixTypedIntoAnEmptyBoxSelectsItsMode()
    {
        Assert.Equal("!", PaletteInput.Typed(string.Empty, '!'));
        Assert.Equal(">", PaletteInput.Typed(string.Empty, '>'));
    }

    [Fact]
    public void APrefixReplacesAnotherPrefixRatherThanFollowingIt()
    {
        // The bug this exists for. Prefixes only ever worked from the window list,
        // because in any other mode the query already began with one: typing ! in the
        // command list produced ">!", which is the command list searching for an
        // exclamation mark. Every mode but the first was a one-way door.
        Assert.Equal("!", PaletteInput.Typed(">", '!'));
        Assert.Equal("#", PaletteInput.Typed("!", '#'));
        Assert.Equal("?", PaletteInput.Typed("$", '?'));
    }

    [Theory]
    [InlineData('>')]
    [InlineData('#')]
    [InlineData('~')]
    [InlineData('%')]
    [InlineData('$')]
    [InlineData('?')]
    [InlineData('!')]
    public void EveryModeIsReachableFromEveryOtherMode(char prefix)
    {
        // Not just the one that was reported. The fault was in the route, so it applied
        // to all of them equally.
        foreach (char from in PaletteModel.Prefixes.Keys)
            Assert.Equal(prefix.ToString(), PaletteInput.Typed(from.ToString(), prefix));
    }

    [Fact]
    public void APrefixStaysLiteralOnceThereIsSomethingToSearch()
    {
        // Typing # after ">foo" is somebody spelling a search, not somebody changing
        // their mind about the mode. Swapping there would eat the query they were
        // halfway through.
        Assert.Equal(">foo#", PaletteInput.Typed(">foo", '#'));
        Assert.Equal("foo!", PaletteInput.Typed("foo", '!'));
    }

    [Fact]
    public void AnOrdinaryCharacterIsAppended()
    {
        Assert.Equal(">fo", PaletteInput.Typed(">f", 'o'));
        Assert.Equal("a", PaletteInput.Typed(string.Empty, 'a'));
    }

    [Fact]
    public void TypingAModesOwnPrefixAgainLeavesItWhereItIs()
    {
        // Rather than producing ">>", which would be the command list searching for a
        // greater-than sign.
        Assert.Equal(">", PaletteInput.Typed(">", '>'));
    }

    // ---- what Enter does ----------------------------------------------------------

    [Fact]
    public void ARowThatNamesAModeChangesMode()
    {
        // What makes the help list usable: somebody reading a list of keys presses
        // Enter on the line they want.
        Assert.Equal(
            PaletteChoice.SwitchMode,
            PaletteInput.Choose(
                Entry(command: "", switchesTo: PaletteMode.Layouts),
                PaletteMode.Help,
                insideOverlay: false));
    }

    [Fact]
    public void AWindowRowGoesToTheWindow()
    {
        Assert.Equal(
            PaletteChoice.Run,
            PaletteInput.Choose(Entry(), PaletteMode.Windows, insideOverlay: false));
    }

    [Fact]
    public void TheInspectActionInspectsBecauseItRunsNothing()
    {
        // In an action list the inspect row has no command, which is what marks it out
        // from every other row there.
        Assert.Equal(
            PaletteChoice.Inspect,
            PaletteInput.Choose(
                Entry(command: "", explains: 42),
                PaletteMode.Windows,
                insideOverlay: true));
    }

    [Fact]
    public void EnterInTheInspectModeAsksWhyRatherThanGoingThere()
    {
        // The rows are windows that were skipped, and the question being asked of all
        // of them is "why?". Going to a window you have just been told is not managed
        // answers nothing - so Enter inspects even though the row carries a command.
        Assert.Equal(
            PaletteChoice.Inspect,
            PaletteInput.Choose(
                Entry(command: "focus-window 42", explains: 42),
                PaletteMode.Inspect,
                insideOverlay: false));
    }

    [Fact]
    public void TheSameRowInTheWindowListStillGoesToTheWindow()
    {
        // The override belongs to the mode, not to the row. In the ordinary list Enter
        // has to keep meaning "take me to it".
        Assert.Equal(
            PaletteChoice.Run,
            PaletteInput.Choose(
                Entry(command: "focus-window 42", explains: 42),
                PaletteMode.Windows,
                insideOverlay: false));
    }

    [Fact]
    public void InsideTheActionListOfASkippedWindowEnterActsAgain()
    {
        // Ctrl+Enter from the inspect mode opens the actions, and there "Go to it"
        // must go to it. Without the overlay check the mode would swallow Enter for
        // every row underneath it too.
        Assert.Equal(
            PaletteChoice.Run,
            PaletteInput.Choose(
                Entry(command: "focus-window 42"),
                PaletteMode.Inspect,
                insideOverlay: true));
    }

    [Fact]
    public void ALongRowOpensInFull()
    {
        Assert.Equal(
            PaletteChoice.Expand,
            PaletteInput.Choose(
                Entry(command: "", expands: "path  C:\\a\\very\\long\\path.exe"),
                PaletteMode.Windows,
                insideOverlay: true));
    }

    [Fact]
    public void InspectingBeatsExpandingWhenARowCouldDoBoth()
    {
        // A report row expands; the action that fetched it inspects. If the order were
        // the other way round the inspect action would open its own description
        // instead of asking the window manager anything.
        Assert.Equal(
            PaletteChoice.Inspect,
            PaletteInput.Choose(
                Entry(command: "", explains: 42, expands: "something"),
                PaletteMode.Windows,
                insideOverlay: true));
    }

    [Fact]
    public void ARowCarryingAListOpensItButOnlyInsideAFrame()
    {
        PaletteEntry entry = Entry(
            command: "",
            actions: [new PaletteAction("Tags\u2026", "pick", string.Empty)]);

        Assert.Equal(
            PaletteChoice.OpenChildren,
            PaletteInput.Choose(entry, PaletteMode.Windows, insideOverlay: true));

        // At the top level a row's list is what Ctrl+Enter is for, and Enter has to
        // keep meaning "go to this window".
        Assert.NotEqual(
            PaletteChoice.OpenChildren,
            PaletteInput.Choose(entry, PaletteMode.Windows, insideOverlay: false));
    }

    [Fact]
    public void AVerbNeedingArgumentsIsOfferedAsTextToFinish()
    {
        // Running it bare would be rejected by the parser and read as a broken palette.
        Assert.Equal(
            PaletteChoice.Complete,
            PaletteInput.Choose(
                Entry("focus", command: ""),
                PaletteMode.Commands,
                insideOverlay: false));
    }

    [Fact]
    public void AHelpRowThatIsOnlyAKeyReferenceDoesNothing()
    {
        // Pressing Enter on the line describing Alt+Q should not close a window, and
        // it should not start composing a command called "Alt+Q" either.
        Assert.Equal(
            PaletteChoice.Nothing,
            PaletteInput.Choose(Entry("Alt+Q", command: ""), PaletteMode.Help, insideOverlay: false));
    }

    [Fact]
    public void AReportRowInsideAFrameWithNothingToOpenDoesNothing()
    {
        Assert.Equal(
            PaletteChoice.Nothing,
            PaletteInput.Choose(Entry(command: ""), PaletteMode.Windows, insideOverlay: true));
    }

    // ---- copying -------------------------------------------------------------------

    [Fact]
    public void CopyingARowTakesItsWholeTextRatherThanWhatWasDrawn()
    {
        // What is on screen has been clipped to the width of the window, and a path
        // with an ellipsis in the middle of it is not a path.
        PaletteEntry row = Entry("C:\\short.exe", expands: "path  C:\\the\\whole\\thing.exe");

        Assert.Equal(
            "path  C:\\the\\whole\\thing.exe",
            PaletteInput.CopyText(row, [row], frameWhole: null, everything: false));
    }

    [Fact]
    public void CopyingARowWithNothingHiddenTakesWhatItSays()
    {
        PaletteEntry row = Entry("msedge");

        Assert.Equal("msedge", PaletteInput.CopyText(row, [row], null, everything: false));
    }

    [Fact]
    public void CopyingEverythingJoinsTheRows()
    {
        // The whole report, which is the version that belongs in an issue.
        PaletteEntry[] rows =
        [
            Entry("0x3047A", expands: "handle  0x3047A"),
            Entry("msedge", expands: "process  msedge"),
        ];

        Assert.Equal(
            $"handle  0x3047A{Environment.NewLine}process  msedge",
            PaletteInput.CopyText(rows[0], rows, frameWhole: null, everything: true));
    }

    [Fact]
    public void CopyingAnExpandedFrameTakesTheValueNotTheVisualLines()
    {
        // Rejoining the wrapped lines would bake in breaks that belong to this
        // window's width rather than to the value - so a copied path would come back
        // with spaces in it.
        PaletteEntry[] wrapped = [Entry("C:\\the\\whole", command: ""), Entry("\\thing.exe", command: "")];

        Assert.Equal(
            "C:\\the\\whole\\thing.exe",
            PaletteInput.CopyText(wrapped[0], wrapped, "C:\\the\\whole\\thing.exe", everything: true));
    }

    [Fact]
    public void CopyingWithNothingSelectedCopiesNothing()
    {
        // Rather than clearing the clipboard, which would be a surprising thing to do
        // to somebody who mistyped a chord.
        Assert.Null(PaletteInput.CopyText(null, [], null, everything: false));
    }

    [Fact]
    public void CopyingAnEmptyListCopiesNothing()
    {
        Assert.Null(PaletteInput.CopyText(null, [], null, everything: true));
    }
}
