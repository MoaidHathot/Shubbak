using Shubbak.Native;

namespace Shubbak.Native.Tests;

/// <summary>
/// Turning held keys into the modifier set a binding is matched on.
/// </summary>
/// <remarks>
/// <para>
/// This exists for one reason: AltGr. On layouts that have it - German, French,
/// Polish, Spanish, the Nordics, Arabic 102 - AltGr is not a key. The layout emits
/// left Control followed by right Alt, and read through the merged
/// <c>VK_CONTROL</c> and <c>VK_MENU</c> that is indistinguishable from a user
/// holding Control and Alt.
/// </para>
/// <para>
/// The consequence was that AltGr+X reported <c>Alt | Control</c>. Matching is exact,
/// so it collided with every binding written <c>alt+ctrl+X</c> - two in the shipped
/// example config and three in the author's own. The hook swallows what it matches,
/// so the character never arrived, in every application on the machine.
/// </para>
/// <para>
/// It cannot be tested on a US keyboard, which is exactly why the decision is a pure
/// function taking the key states rather than something buried behind
/// <c>GetAsyncKeyState</c>.
/// </para>
/// </remarks>
public sealed class ModifierDerivationTests
{
    private static KeyModifiers Derive(
        bool leftAlt = false,
        bool rightAlt = false,
        bool leftControl = false,
        bool rightControl = false,
        bool shift = false,
        bool windows = false) =>
        KeyboardSource.DeriveModifiers(leftAlt, rightAlt, leftControl, rightControl, shift, windows);

    [Fact]
    public void AltGrIsNeitherControlNorAlt()
    {
        // The whole point. Left Control plus right Alt is what the layout emits for
        // AltGr, so it must reach no binding at all and pass through to the
        // application, which is the only thing that can turn it into a character.
        Assert.Equal(KeyModifiers.None, Derive(leftControl: true, rightAlt: true));
    }

    [Fact]
    public void AltGrWithAKeyStillMatchesNothing()
    {
        // AltGr+Q is @ on German and AltGr+A is ą on Polish. Reported as Alt|Control
        // these collided with any binding written alt+ctrl+q or alt+ctrl+a, and the
        // hook swallows what it matches - so the character never appeared.
        KeyModifiers altGr = Derive(leftControl: true, rightAlt: true);

        Assert.False(altGr.HasFlag(KeyModifiers.Alt));
        Assert.False(altGr.HasFlag(KeyModifiers.Control));
    }

    [Fact]
    public void RightAltAloneIsStillAlt()
    {
        // On a layout without AltGr the right Alt is simply Alt, and plenty of people
        // press their bindings with it.
        Assert.Equal(KeyModifiers.Alt, Derive(rightAlt: true));
    }

    [Fact]
    public void LeftControlAloneIsStillControl()
    {
        Assert.Equal(KeyModifiers.Control, Derive(leftControl: true));
    }

    [Theory]
    [InlineData(true, false, false, true)]   // left Alt  + right Control
    [InlineData(true, false, true, false)]   // left Alt  + left Control
    [InlineData(false, true, false, true)]   // right Alt + right Control
    public void EveryOtherWayOfPressingControlAndAltStillWorks(
        bool leftAlt, bool rightAlt, bool leftControl, bool rightControl)
    {
        // Only one of the four combinations is claimed by AltGr. The binding
        // alt+ctrl+t therefore remains reachable three ways, which is what makes the
        // trade acceptable.
        Assert.Equal(
            KeyModifiers.Alt | KeyModifiers.Control,
            Derive(leftAlt: leftAlt, rightAlt: rightAlt, leftControl: leftControl, rightControl: rightControl));
    }

    [Fact]
    public void HoldingLeftAltAsWellAsAltGrIsStillAlt()
    {
        // AltGr accounts for the right Alt and the left Control. Anything held over
        // and above that is the user's, and must survive.
        Assert.Equal(
            KeyModifiers.Alt,
            Derive(leftAlt: true, rightAlt: true, leftControl: true));
    }

    [Fact]
    public void HoldingRightControlAsWellAsAltGrIsStillControl()
    {
        Assert.Equal(
            KeyModifiers.Control,
            Derive(leftControl: true, rightAlt: true, rightControl: true));
    }

    [Fact]
    public void ShiftAndWindowsAreUnaffectedByAnyOfIt()
    {
        // AltGr+Shift+key is an ordinary way to reach a third character level, so
        // Shift must still be reported even while Alt and Control are being withheld.
        Assert.Equal(
            KeyModifiers.Shift,
            Derive(leftControl: true, rightAlt: true, shift: true));

        Assert.Equal(
            KeyModifiers.Shift | KeyModifiers.Windows,
            Derive(shift: true, windows: true));
    }

    [Fact]
    public void NothingHeldIsNoModifiers()
    {
        Assert.Equal(KeyModifiers.None, Derive());
    }

    [Fact]
    public void EverythingHeldIsEverythingButTheAltGrPair()
    {
        // Both sides of both keys: AltGr is satisfied by the left Control and right
        // Alt, and the left Alt and right Control that remain are genuine.
        Assert.Equal(
            KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Windows,
            Derive(
                leftAlt: true,
                rightAlt: true,
                leftControl: true,
                rightControl: true,
                shift: true,
                windows: true));
    }
}
