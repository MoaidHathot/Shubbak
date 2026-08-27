namespace Shubbak.Native.Tests;

/// <summary>
/// The single chord a suspended window manager still listens for.
/// </summary>
/// <remarks>
/// <para>
/// Suspending removes the low-level keyboard hook, which is the point of it and also
/// removes the only way Shubbak had of noticing a keystroke. Something has to keep
/// listening or suspending becomes a one-way door - the trap the pause command's own
/// comment warns about.
/// </para>
/// <para>
/// <c>RegisterHotKey</c> is deliberately not a hook. A hook runs our code on every
/// keystroke the machine receives; this asks the system to watch for one combination
/// and post a single message when it matches. That distinction is the entire
/// justification for suspending rather than exiting: a suspended Shubbak costs the
/// input path nothing, which is what somebody in a game is asking for.
/// </para>
/// </remarks>
public sealed class ResumeHotKeyTests
{
    /// <summary>
    /// A chord unlikely to be taken by anything else on a build agent.
    /// </summary>
    private const ushort F24 = 0x87;

    [Fact]
    public void ItRegistersAndUnregisters()
    {
        using var hotKey = new ResumeHotKey();

        Assert.False(hotKey.IsRegistered);

        Assert.True(
            hotKey.Register(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, F24, "ctrl+alt+shift+f24"),
            "the test chord should not already be taken");

        Assert.True(hotKey.IsRegistered);
        Assert.Equal("ctrl+alt+shift+f24", hotKey.Display);

        hotKey.Unregister();

        Assert.False(hotKey.IsRegistered);
        Assert.Null(hotKey.Display);
    }

    /// <summary>
    /// Registering twice must not leak the first registration.
    /// </summary>
    /// <remarks>
    /// Suspend/resume/suspend is the ordinary cycle, and each suspend registers again.
    /// A registration that was never released would make the second attempt fail with
    /// the chord apparently taken - by us.
    /// </remarks>
    [Fact]
    public void RegisteringAgainReplacesTheFirst()
    {
        using var hotKey = new ResumeHotKey();

        Assert.True(hotKey.Register(KeyModifiers.Control | KeyModifiers.Alt, F24, "first"));
        Assert.True(hotKey.Register(KeyModifiers.Control | KeyModifiers.Alt, F24, "second"));

        Assert.True(hotKey.IsRegistered);
        Assert.Equal("second", hotKey.Display);
    }

    /// <summary>Unregistering when nothing is registered is not an error.</summary>
    /// <remarks>
    /// Resume runs it unconditionally, including on the path where registration failed
    /// because another program owned the chord.
    /// </remarks>
    [Fact]
    public void UnregisteringWhenIdleIsHarmless()
    {
        using var hotKey = new ResumeHotKey();

        hotKey.Unregister();
        hotKey.Unregister();

        Assert.False(hotKey.IsRegistered);
    }

    /// <summary>
    /// A chord somebody else owns is refused rather than throwing.
    /// </summary>
    /// <remarks>
    /// <c>RegisterHotKey</c> does not share. The caller reports it and carries on,
    /// because <c>shubbak wm-resume</c> is still a way back - a failure here must not
    /// prevent suspending, only change the advice printed with it.
    /// </remarks>
    [Fact]
    public void AChordAlreadyTakenIsRefusedNotThrown()
    {
        using var first = new ResumeHotKey();
        using var second = new ResumeHotKey();

        Assert.True(first.Register(KeyModifiers.Control | KeyModifiers.Shift, F24, "taken"));

        // Same thread, same id: the second registration cannot succeed.
        Assert.False(second.Register(KeyModifiers.Control | KeyModifiers.Shift, F24, "wanted"));
        Assert.False(second.IsRegistered);
    }

    /// <summary>Disposal releases the registration.</summary>
    [Fact]
    public void DisposingReleasesTheChord()
    {
        var hotKey = new ResumeHotKey();

        Assert.True(hotKey.Register(KeyModifiers.Alt | KeyModifiers.Shift, F24, "held"));
        hotKey.Dispose();

        // Provable only by taking it again.
        using var again = new ResumeHotKey();
        Assert.True(again.Register(KeyModifiers.Alt | KeyModifiers.Shift, F24, "retaken"));
    }
}
