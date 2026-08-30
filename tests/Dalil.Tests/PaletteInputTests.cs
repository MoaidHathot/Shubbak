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
    public void AltEnterIsAChord()
    {
        // It was advertised as a badge on "Bring it here" from the day that action was
        // written and was in no lookup table at all, so pressing it did nothing. A
        // badge naming a key that does nothing is worse than no badge.
        Assert.Equal(
            "Alt+Enter",
            PaletteInput.ChordFor(VIRTUAL_KEY.VK_RETURN, control: false, shift: false, alt: true));
    }

    [Fact]
    public void EveryChordTheHelpAdvertisesIsAChordSomethingProduces()
    {
        // The drift test the comment above PaletteEntries.Keys has always claimed
        // existed. It did not, and the list had already fallen behind by six entries:
        // Ctrl+Shift+C was implemented and documented in the README, and all five
        // action chords were printed as badges in the list they belong to, and not one
        // of the six was written on the page that exists to be the source of truth.
        HashSet<string> produced = [];

        foreach (char letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            foreach ((bool control, bool shift, bool alt) in Modifiers())
            {
                if (PaletteInput.ChordFor((VIRTUAL_KEY)letter, control, shift, alt) is { } chord)
                    produced.Add(chord);
            }
        }

        foreach ((bool control, bool shift, bool alt) in Modifiers())
        {
            if (PaletteInput.ChordFor(VIRTUAL_KEY.VK_RETURN, control, shift, alt) is { } chord)
                produced.Add(chord);
        }

        string listed = string.Join('\n', PaletteEntries.Keys.Select(k => k.Keys));

        foreach (string chord in produced)
        {
            Assert.True(
                listed.Contains(chord, StringComparison.Ordinal),
                $"'{chord}' works but the help screen never mentions it.");
        }
    }

    private static IEnumerable<(bool Control, bool Shift, bool Alt)> Modifiers()
    {
        for (int i = 0; i < 8; i++)
            yield return ((i & 1) != 0, (i & 2) != 0, (i & 4) != 0);
    }

    // ---- jumping straight to a mode ------------------------------------------------

    [Fact]
    public void ADigitJumpsToTheModeInThatPositionOfTheHintBar()
    {
        // The route that works on every keyboard layout in the world. Prefixes are
        // faster and unavailable on several - a dead key produces no character at all
        // until the next keypress - and Tab is seven presses end to end.
        Assert.Equal(PaletteMode.Windows, PaletteInput.JumpFor(VIRTUAL_KEY.VK_1, control: true));
        Assert.Equal(PaletteMode.Commands, PaletteInput.JumpFor(VIRTUAL_KEY.VK_2, control: true));
        Assert.Equal(PaletteMode.Help, PaletteInput.JumpFor(VIRTUAL_KEY.VK_8, control: true));
    }

    [Fact]
    public void TheNumericKeypadJumpsToo()
    {
        // A desk has one and a laptop does not, so both.
        Assert.Equal(PaletteMode.Windows, PaletteInput.JumpFor(VIRTUAL_KEY.VK_NUMPAD1, control: true));
    }

    [Fact]
    public void ADigitWithoutControlIsJustADigit()
    {
        // Or workspace "3" would be unsearchable.
        Assert.Null(PaletteInput.JumpFor(VIRTUAL_KEY.VK_3, control: false));
    }

    [Fact]
    public void ADigitPastTheEndOfTheListJumpsNowhere()
    {
        Assert.Null(PaletteInput.JumpFor(VIRTUAL_KEY.VK_9, control: true));
    }

    [Fact]
    public void EveryModeIsReachableByADigit()
    {
        // Which is what makes it safe for a mode to have no prefix at all - somebody
        // who has remapped the lot, or whose keyboard cannot produce them, can still
        // get everywhere.
        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
            Assert.Contains(mode, PaletteModel.JumpOrder);
    }

    // ---- typing ---------------------------------------------------------------------

    /// <summary>What a character does to a query, with the caret at the end of it.</summary>
    private static string Typed(string query, char typed) =>
        PaletteModel.AfterTyping(PalettePrefixes.Default, query, query.Length, typed).Query;

    [Fact]
    public void APrefixTypedIntoAnEmptyBoxSelectsItsMode()
    {
        Assert.Equal("!", Typed(string.Empty, '!'));
        Assert.Equal(">", Typed(string.Empty, '>'));
    }

    [Fact]
    public void APrefixReplacesAnotherPrefixRatherThanFollowingIt()
    {
        // The bug this exists for. Prefixes only ever worked from the window list,
        // because in any other mode the query already began with one: typing ! in the
        // command list produced ">!", which is the command list searching for an
        // exclamation mark. Every mode but the first was a one-way door.
        Assert.Equal("!", Typed(">", '!'));
        Assert.Equal("#", Typed("!", '#'));
        Assert.Equal("?", Typed("$", '?'));
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
        foreach (char from in PaletteModel.DefaultPrefixes.Keys)
            Assert.Equal(prefix.ToString(), Typed(from.ToString(), prefix));
    }

    [Fact]
    public void APrefixStaysLiteralOnceThereIsSomethingToSearch()
    {
        // Typing # after ">foo" is somebody spelling a search, not somebody changing
        // their mind about the mode. Swapping there would eat the query they were
        // halfway through.
        Assert.Equal(">foo#", Typed(">foo", '#'));
        Assert.Equal("foo!", Typed("foo", '!'));
    }

    [Fact]
    public void AnOrdinaryCharacterIsAppended()
    {
        Assert.Equal(">fo", Typed(">f", 'o'));
        Assert.Equal("a", Typed(string.Empty, 'a'));
    }

    [Fact]
    public void ACharacterGoesInAtTheCaretRatherThanAtTheEnd()
    {
        // Which is the whole of what a caret is for. The box used to be append-only, so
        // a typo in the middle of "resize --width +5%" cost the rest of the line.
        Assert.Equal(
            ">focus", PaletteModel.AfterTyping(PalettePrefixes.Default, ">fcus", 2, 'o').Query);
    }

    [Fact]
    public void TypingAModesOwnPrefixAgainLeavesItWhereItIs()
    {
        // Rather than producing ">>", which would be the command list searching for a
        // greater-than sign.
        Assert.Equal(">", Typed(">", '>'));
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
    public void OnlyCommandsModeOffersTextToFinish()
    {
        // The bug this exists for. Completing was the fall-through for every row with
        // no command in every mode but help, so choosing a monitor with no workspace on
        // it put the display's own name into the command box - as though `\\.\DISPLAY2`
        // were a verb somebody had started typing.
        foreach (PaletteMode mode in Enum.GetValues<PaletteMode>())
        {
            if (mode is PaletteMode.Commands) continue;

            Assert.Equal(
                PaletteChoice.Nothing,
                PaletteInput.Choose(Entry("DISPLAY2", command: ""), mode, insideOverlay: false));
        }
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

    // ---- asking before something irreversible ---------------------------------------

    [Fact]
    public void SomethingIrreversibleAsksFirst()
    {
        // What replaced action-guard. That setting disabled eight harmless chords to
        // protect against two dangerous ones, and its default left every chord in the
        // palette inert except the one that took no action at all.
        Assert.Equal(
            PaletteChoice.Confirm,
            PaletteInput.Choose(
                Destructive(),
                PaletteMode.Windows,
                insideOverlay: true,
                confirmDestructive: true));
    }

    [Fact]
    public void SomethingReversibleJustHappens()
    {
        // Floating a window is a toggle and pressing it twice puts the desktop back the
        // way it was found, so making somebody confirm it would be a tax on the case
        // that is not dangerous.
        Assert.Equal(
            PaletteChoice.Run,
            PaletteInput.Choose(
                Entry(command: "focus-window 42\ntoggle-floating"),
                PaletteMode.Windows,
                insideOverlay: true,
                confirmDestructive: true));
    }

    [Fact]
    public void TurningTheAskingOffMakesItHappenOutright()
    {
        Assert.Equal(
            PaletteChoice.Run,
            PaletteInput.Choose(
                Destructive(),
                PaletteMode.Windows,
                insideOverlay: true,
                confirmDestructive: false));
    }

    [Fact]
    public void TheConfirmationsOwnYesRowDoesNotAskAgain()
    {
        // Its "yes" row is itself marked destructive, which is what draws it in the
        // warning colour. Routing that back through the same test would ask the same
        // question for ever, which is the one way this feature could be worse than not
        // having it.
        PaletteEntry yes = PaletteActions
            .Confirmation("Close it", "focus-window 42\nclose")
            .Single(e => e.Command.Length > 0);

        Assert.Equal(
            PaletteChoice.Run,
            PaletteInput.Choose(yes, PaletteMode.Windows, insideOverlay: true, confirmDestructive: false));
    }

    [Fact]
    public void RefusingIsTheFirstAndSafestRowOfAConfirmation()
    {
        // So that the reflex of pressing Enter twice does not close a window.
        IReadOnlyList<PaletteEntry> rows = PaletteActions.Confirmation("Close it", "close");

        Assert.Equal(2, rows.Count);
        Assert.Equal(string.Empty, rows[0].Command);
        Assert.True(rows[0].Rank > rows[1].Rank);
    }

    private static PaletteEntry Destructive() =>
        new("Close it", "Ask the window to close", [], "focus-window 42\nclose", Destructive: true);

    // ---- copying -------------------------------------------------------------------

    [Fact]
    public void CopyingAWindowRowTakesTheAttributesWorthPastingIntoARule()
    {
        // The reason to copy a row out of the window list is almost always to put its
        // class or its process into a rule, and both of those live in the dim half - so
        // copying the title alone handed over the one attribute guaranteed to be the
        // wrong thing to match on.
        PaletteEntry row = Entry("Inbox - Fastmail", secondary: "msedge  \u00B7  2");

        Assert.Equal(
            "Inbox - Fastmail  \u2014  msedge  \u00B7  2",
            PaletteInput.DescribeForClipboard(row));
    }

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
