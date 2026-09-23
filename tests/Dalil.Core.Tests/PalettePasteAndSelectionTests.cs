using Dalil.Core;

namespace Dalil.Core.Tests;

/// <summary>
/// Pasting, selecting all, and editing text that is not one UTF-16 unit per character.
/// </summary>
/// <remarks>
/// <para>
/// The palette had no paste: Ctrl+V's character is a control character and was
/// dropped, so a command copied from the docs had to be retyped. It had no
/// select-all, so starting over meant holding Backspace. And it edited by UTF-16
/// unit, so an emoji from the Win+. panel took two Backspaces to remove and one Left
/// to put the caret inside, where the renderer had half a character on each side.
/// </para>
/// <para>
/// The model is where all three live, since they are rules about the text and not
/// about the window: a paste is an insert of many characters through the same door a
/// typed one takes, so pasting a prefix into an empty palette changes mode as typing
/// it would.
/// </para>
/// </remarks>
public sealed class PalettePasteAndSelectionTests
{
    private static PaletteModel WithQuery(string query)
    {
        var model = new PaletteModel();
        model.SetQuery(query);
        return model;
    }

    // ---- pasting -----------------------------------------------------------------

    [Fact]
    public void APasteGoesInAtTheCaret()
    {
        PaletteModel model = WithQuery("ab");
        model.MoveCaret(-1);

        model.Insert("XY");

        Assert.Equal("aXYb", model.Query);
        Assert.Equal(3, model.Caret);
    }

    [Fact]
    public void APastedPrefixIntoAnEmptyPaletteChangesTheMode()
    {
        // The same rule typing follows, applied to the first character of the paste.
        PaletteModel model = WithQuery(string.Empty);

        model.Insert(">focus");

        Assert.Equal(PaletteMode.Commands, model.Mode);
        Assert.Equal(">focus", model.Query);
        Assert.Equal(6, model.Caret);
    }

    [Fact]
    public void APastedPrefixIntoATermIsLiteral()
    {
        PaletteModel model = WithQuery("abc");

        model.Insert(">x");

        Assert.Equal("abc>x", model.Query);
        Assert.Equal(PaletteMode.Windows, model.Mode);
    }

    [Fact]
    public void ALineBreakInAPasteBecomesOneSpace()
    {
        // A command copied from a terminal comes with its newline; a query is one line.
        PaletteModel model = WithQuery(string.Empty);

        model.Insert("focus\r\n--workspace\t3\n");

        Assert.Equal("focus --workspace 3 ", model.Query);
    }

    [Fact]
    public void OtherControlCharactersInAPasteVanish()
    {
        Assert.Equal("ab", PaletteModel.Sanitised("a\u0001b\u001B"));
        Assert.Equal("plain", PaletteModel.Sanitised("plain"));
    }

    [Fact]
    public void PastingNothingChangesNothing()
    {
        PaletteModel model = WithQuery("abc");
        model.MoveCaret(-1);

        model.Insert(string.Empty);
        model.Insert("\u0001");

        Assert.Equal("abc", model.Query);
        Assert.Equal(2, model.Caret);
    }

    // ---- select all --------------------------------------------------------------

    [Fact]
    public void SelectAllTakesTheTermAndNotThePrefix()
    {
        PaletteModel model = WithQuery(">focus");

        model.SelectAll();

        Assert.True(model.AllSelected);

        model.Insert('r');

        Assert.Equal(">r", model.Query);
        Assert.Equal(PaletteMode.Commands, model.Mode);
        Assert.False(model.AllSelected);
    }

    [Fact]
    public void APasteReplacesTheSelection()
    {
        PaletteModel model = WithQuery("old words");
        model.SelectAll();

        model.Insert("new");

        Assert.Equal("new", model.Query);
        Assert.Equal(3, model.Caret);
    }

    [Fact]
    public void BackspaceAndDeleteRemoveTheSelection()
    {
        PaletteModel backspaced = WithQuery(">focus");
        backspaced.SelectAll();
        backspaced.DeleteBack(wholeWord: false);

        Assert.Equal(">", backspaced.Query);
        Assert.Equal(1, backspaced.Caret);
        Assert.Equal(PaletteMode.Commands, backspaced.Mode);

        PaletteModel deleted = WithQuery("abc");
        deleted.SelectAll();
        deleted.DeleteForward();

        Assert.Equal(string.Empty, deleted.Query);
    }

    [Fact]
    public void MovingTheCaretDropsTheSelection()
    {
        PaletteModel model = WithQuery("abc");
        model.SelectAll();

        model.MoveCaret(-1);

        Assert.False(model.AllSelected);

        model.Insert('x');

        Assert.Equal("abxc", model.Query);
    }

    [Fact]
    public void HomeAndEndDropTheSelectionToo()
    {
        PaletteModel model = WithQuery("abc");
        model.SelectAll();
        model.CaretToEdge(end: false);

        Assert.False(model.AllSelected);
        Assert.Equal(0, model.Caret);
    }

    [Fact]
    public void NothingToSelectIsNothingSelected()
    {
        PaletteModel empty = WithQuery(">");
        empty.SelectAll();

        Assert.False(empty.AllSelected);

        // And the next Backspace does what it always did on an empty term: leaves the mode.
        empty.DeleteBack(wholeWord: false);

        Assert.Equal(string.Empty, empty.Query);
    }

    [Fact]
    public void ANewQueryIsNotSelected()
    {
        PaletteModel model = WithQuery("abc");
        model.SelectAll();

        model.SetQuery("def");

        Assert.False(model.AllSelected);
    }

    // ---- characters that are not one unit ----------------------------------------

    private const string Emoji = "\U0001F600";              // 😀: two UTF-16 units
    private const string Flag = "\U0001F1EC\U0001F1E7";     // 🇬🇧: two pairs, one grapheme
    private const string Accented = "e\u0301";              // e + combining acute

    [Fact]
    public void BackspaceRemovesAWholeEmoji()
    {
        PaletteModel model = WithQuery("a" + Emoji);

        model.DeleteBack(wholeWord: false);

        Assert.Equal("a", model.Query);
        Assert.Equal(1, model.Caret);
    }

    [Fact]
    public void BackspaceRemovesAWholeFlag()
    {
        PaletteModel model = WithQuery("a" + Flag);

        model.DeleteBack(wholeWord: false);

        Assert.Equal("a", model.Query);
    }

    [Fact]
    public void BackspaceRemovesALetterWithItsMark()
    {
        PaletteModel model = WithQuery("caf" + Accented);

        model.DeleteBack(wholeWord: false);

        Assert.Equal("caf", model.Query);
    }

    [Fact]
    public void DeleteRemovesAWholeEmoji()
    {
        PaletteModel model = WithQuery(Emoji + "b");
        model.CaretToEdge(end: false);

        model.DeleteForward();

        Assert.Equal("b", model.Query);
        Assert.Equal(0, model.Caret);
    }

    [Fact]
    public void TheCaretStepsOverAWholeEmoji()
    {
        PaletteModel model = WithQuery("a" + Emoji + "b");

        model.MoveCaret(-1);
        Assert.Equal(3, model.Caret);

        model.MoveCaret(-1);
        Assert.Equal(1, model.Caret);

        model.MoveCaret(1);
        Assert.Equal(3, model.Caret);
    }

    [Fact]
    public void TheCaretNeverLandsInsideAPairWhateverItIsHanded()
    {
        PaletteModel model = WithQuery(Emoji);
        model.CaretToEdge(end: false);

        model.MoveCaret(1);

        Assert.Equal(2, model.Caret);

        model.MoveCaret(-1);

        Assert.Equal(0, model.Caret);
    }

    [Fact]
    public void AWordDeleteStillWorksAcrossAnEmoji()
    {
        PaletteModel model = WithQuery("focus " + Emoji + "x");

        model.DeleteBack(wholeWord: true);

        Assert.Equal("focus ", model.Query);
    }

    [Fact]
    public void AnEmojiTypedAsOneStringIsOneCharacterToTheCaret()
    {
        PaletteModel model = WithQuery("a");

        model.Insert(Emoji);

        Assert.Equal("a" + Emoji, model.Query);
        Assert.Equal(3, model.Caret);

        model.MoveCaret(-1);

        Assert.Equal(1, model.Caret);
    }
}
