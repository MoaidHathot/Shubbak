namespace Shubbak.Config.Tests;

/// <summary>
/// Turning what the user wrote into a virtual-key code.
/// </summary>
/// <remarks>
/// <para>
/// No test called <c>KeyParser.TryParse</c> directly. SHB0201 through SHB0204 were
/// never asserted, and <c>SplitParts</c> - which exists specifically so
/// <c>alt++</c> and <c>alt+-</c> work, the second of which appears in the author's
/// own config - was covered only sideways, through a test that asserts consequences
/// rather than the mapping.
/// </para>
/// <para>
/// For a parser whose failure mode is a binding that silently binds the wrong
/// physical key, that was the wrong place to have no tests.
/// </para>
/// </remarks>
public sealed class KeyParserTests
{
    private static KeyBinding Parse(string text)
    {
        Assert.True(
            KeyParser.TryParse(text, default, out KeyBinding binding, out Diagnostic? diagnostic),
            $"'{text}' failed to parse: {diagnostic?.Message}");

        Assert.Null(diagnostic);
        return binding;
    }

    private static Diagnostic Refuse(string text)
    {
        Assert.False(KeyParser.TryParse(text, default, out _, out Diagnostic? diagnostic));

        return Assert.IsType<Diagnostic>(diagnostic);
    }

    [Theory]
    [InlineData("a", 0x41)]
    [InlineData("A", 0x41)]
    [InlineData("z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("9", 0x39)]
    public void LettersAndDigitsAreTheirOwnVirtualKeys(string text, int expected)
    {
        KeyBinding binding = Parse(text);

        Assert.Equal(expected, binding.VirtualKey);
        Assert.Equal(0, binding.Modifiers);
    }

    [Theory]
    [InlineData("f1", 0x70)]
    [InlineData("F12", 0x7B)]
    [InlineData("f24", 0x87)]
    public void FunctionKeysCountFromF1(string text, int expected)
    {
        Assert.Equal(expected, Parse(text).VirtualKey);
    }

    [Theory]
    [InlineData("f0")]
    [InlineData("f25")]
    [InlineData("f100")]
    public void ThereIsNoSuchFunctionKey(string text) => Assert.Equal("SHB0203", Refuse(text).Code);

    [Theory]
    [InlineData("alt+h", KeyParser.ModAlt)]
    [InlineData("ctrl+h", KeyParser.ModControl)]
    [InlineData("control+h", KeyParser.ModControl)]
    [InlineData("shift+h", KeyParser.ModShift)]
    [InlineData("win+h", KeyParser.ModWindows)]
    [InlineData("super+h", KeyParser.ModWindows)]
    [InlineData("meta+h", KeyParser.ModWindows)]
    [InlineData("cmd+h", KeyParser.ModWindows)]
    public void EverySpellingOfEveryModifier(string text, int expected)
    {
        KeyBinding binding = Parse(text);

        Assert.Equal(expected, binding.Modifiers);
        Assert.Equal(0x48, binding.VirtualKey);
    }

    [Fact]
    public void ModifiersCombineAndOrderDoesNotMatter()
    {
        const int Expected = KeyParser.ModAlt | KeyParser.ModShift | KeyParser.ModControl;

        Assert.Equal(Expected, Parse("alt+shift+ctrl+h").Modifiers);
        Assert.Equal(Expected, Parse("ctrl+alt+shift+h").Modifiers);
        Assert.Equal(Expected, Parse("SHIFT+CTRL+ALT+h").Modifiers);
    }

    [Fact]
    public void ARepeatedModifierIsNotAnError()
    {
        // Bit flags, so saying it twice is saying it once.
        Assert.Equal(KeyParser.ModAlt, Parse("alt+alt+h").Modifiers);
    }

    // ---- the separator, which is also a key --------------------------------

    [Theory]
    [InlineData("alt+-", 0xBD)]
    [InlineData("alt++", 0xBB)]
    [InlineData("+", 0xBB)]
    [InlineData("-", 0xBD)]
    public void TheSeparatorCanAlsoBeTheKey(string text, int expected)
    {
        // A naive split on '+' breaks both of these. alt+- is in the author's config.
        KeyBinding binding = Parse(text);

        Assert.Equal(expected, binding.VirtualKey);
    }

    [Fact]
    public void ModifiersStillApplyToAPunctuationKey()
    {
        KeyBinding binding = Parse("alt+shift++");

        Assert.Equal(KeyParser.ModAlt | KeyParser.ModShift, binding.Modifiers);
        Assert.Equal(0xBB, binding.VirtualKey);
    }

    [Theory]
    [InlineData(";", 0xBA)]
    [InlineData("=", 0xBB)]
    [InlineData(",", 0xBC)]
    [InlineData(".", 0xBE)]
    [InlineData("/", 0xBF)]
    [InlineData("`", 0xC0)]
    [InlineData("[", 0xDB)]
    [InlineData("\\", 0xDC)]
    [InlineData("]", 0xDD)]
    [InlineData("'", 0xDE)]
    public void BarePunctuationResolves(string text, int expected) =>
        Assert.Equal(expected, Parse(text).VirtualKey);

    [Theory]
    [InlineData("oem_1", 0xBA)]
    [InlineData("semicolon", 0xBA)]
    [InlineData("oem_quotes", 0xDE)]
    [InlineData("oem_minus", 0xBD)]
    [InlineData("comma", 0xBC)]
    [InlineData("period", 0xBE)]
    public void GlazeWmSpellingsAreAcceptedSoAConfigCanBeMovedOver(string text, int expected) =>
        Assert.Equal(expected, Parse(text).VirtualKey);

    // ---- the vocabulary that was missing -----------------------------------

    [Theory]
    [InlineData("numpad0", 0x60)]
    [InlineData("kp0", 0x60)]
    [InlineData("num0", 0x60)]
    [InlineData("numpad1", 0x61)]
    [InlineData("kp1", 0x61)]
    [InlineData("num1", 0x61)]
    [InlineData("numpad9", 0x69)]
    public void TheNumpadCanBeBound(string text, int expected)
    {
        // A numpad is the obvious hardware for nineteen workspaces, and all three of
        // these spellings get guessed. None of them worked at all before.
        Assert.Equal(expected, Parse(text).VirtualKey);
    }

    [Theory]
    [InlineData("numpad_add", 0x6B)]
    [InlineData("numpad_subtract", 0x6D)]
    [InlineData("numpad_multiply", 0x6A)]
    [InlineData("numpad_divide", 0x6F)]
    [InlineData("numpad_decimal", 0x6E)]
    public void SoCanItsOperators(string text, int expected) =>
        Assert.Equal(expected, Parse(text).VirtualKey);

    [Theory]
    [InlineData("volume_mute", 0xAD)]
    [InlineData("volume_up", 0xAF)]
    [InlineData("media_play_pause", 0xB3)]
    [InlineData("media_next", 0xB0)]
    [InlineData("browser_back", 0xA6)]
    [InlineData("browser_home", 0xAC)]
    public void MediaAndBrowserKeysCanBeBound(string text, int expected) =>
        Assert.Equal(expected, Parse(text).VirtualKey);

    [Theory]
    [InlineData("printscreen", 0x2C)]
    [InlineData("capslock", 0x14)]
    [InlineData("scrolllock", 0x91)]
    [InlineData("numlock", 0x90)]
    [InlineData("pause", 0x13)]
    [InlineData("apps", 0x5D)]
    public void LocksAndSystemKeysCanBeBound(string text, int expected) =>
        Assert.Equal(expected, Parse(text).VirtualKey);

    [Theory]
    [InlineData("oem_102", 0xE2)]
    [InlineData("backslash_iso", 0xE2)]
    [InlineData("oem_8", 0xDF)]
    public void TheIsoKeysExistOnEveryNonUsKeyboardAndNowHaveNames(string text, int expected) =>
        Assert.Equal(expected, Parse(text).VirtualKey);

    [Fact]
    public void KeyNamesAreCaseInsensitive()
    {
        Assert.Equal(0x61, Parse("NUMPAD1").VirtualKey);
        Assert.Equal(0x0D, Parse("Enter").VirtualKey);
        Assert.Equal(0xAF, Parse("VOLUME_UP").VirtualKey);
    }

    // ---- the diagnostics ---------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyBindingIsSHB0201(string text) => Assert.Equal("SHB0201", Refuse(text).Code);

    [Theory]
    [InlineData("a+b")]
    [InlineData("alt+h+j")]
    [InlineData("enter+space")]
    public void TwoMainKeysIsSHB0202(string text) => Assert.Equal("SHB0202", Refuse(text).Code);

    [Theory]
    [InlineData("alt+notakey")]
    [InlineData("alt+f25")]
    [InlineData("hyper+h")]
    public void AnUnknownKeyIsSHB0203(string text) => Assert.Equal("SHB0203", Refuse(text).Code);

    [Theory]
    [InlineData("alt")]
    [InlineData("alt+shift")]
    [InlineData("ctrl+alt+shift+win")]
    public void ModifiersWithNoKeyIsSHB0204(string text) => Assert.Equal("SHB0204", Refuse(text).Code);

    [Fact]
    public void EveryRefusalSaysWhatWasWrittenAndSuggestsSomething()
    {
        // These are read by someone whose keybinding did not work, so naming the
        // binding is the difference between a fix and a hunt.
        Diagnostic diagnostic = Refuse("alt+notakey");

        Assert.Contains("notakey", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("alt+notakey", diagnostic.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Hint));
    }

    [Fact]
    public void TheDisplayFormIsWhatTheUserWrote()
    {
        // It ends up in logs and in the bar, so it has to be their spelling rather
        // than a normalised one they would not recognise.
        Assert.Equal("alt+shift+H", Parse("alt+shift+H").Display);
        Assert.Equal("Win+Left", Parse("Win+Left").Display);
    }
}
