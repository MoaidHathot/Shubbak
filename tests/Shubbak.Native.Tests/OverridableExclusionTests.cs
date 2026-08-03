namespace Shubbak.Native.Tests;

/// <summary>
/// Which of the filter's judgements a user may overturn.
/// </summary>
/// <remarks>
/// The exclusions are heuristics and heuristics are wrong sometimes - a chat client
/// that arrives without a title, a call window that declares itself a palette, an
/// application owned by an invisible parent so it never reaches Alt+Tab. Until these
/// could be overturned from configuration, the only remedy was editing the source.
/// </remarks>
public sealed class OverridableExclusionTests
{
    [Theory]
    [InlineData(ExclusionReason.ToolWindow)]
    [InlineData(ExclusionReason.NotInAltTabList)]
    [InlineData(ExclusionReason.NoTitle)]
    [InlineData(ExclusionReason.ExcludedClass)]
    [InlineData(ExclusionReason.ExcludedProcess)]
    public void AHeuristicCanBeOverruled(ExclusionReason reason)
    {
        Assert.True(WindowFilter.CanBeOverridden(reason));
    }

    [Theory]
    [InlineData(ExclusionReason.NotAWindow)]
    [InlineData(ExclusionReason.ShellWindow)]
    [InlineData(ExclusionReason.ChildWindow)]
    public void WhatIsNotAWindowCannotBeMadeOne(ExclusionReason reason)
    {
        // Tiling the desktop, the taskbar's parent, or a child control is not a
        // preference anyone is entitled to; it is a window manager that does not work.
        Assert.False(WindowFilter.CanBeOverridden(reason));
    }

    [Theory]
    [InlineData(ExclusionReason.CloakedByShell)]
    [InlineData(ExclusionReason.CloakedByOwner)]
    [InlineData(ExclusionReason.NotVisible)]
    [InlineData(ExclusionReason.ZeroSized)]
    public void AWindowThatIsNotThereIsNotTakenOn(ExclusionReason reason)
    {
        // A cloaked window is on another virtual desktop or suspended. Managing it
        // would drag it onto this one, which is not what the rule is asking for.
        Assert.False(WindowFilter.CanBeOverridden(reason));
    }

    [Fact]
    public void EveryReasonHasADeliberateAnswer()
    {
        // A new exclusion reason must be classified rather than defaulting, since the
        // default - not overridable - is the one that leaves a user stuck.
        foreach (ExclusionReason reason in Enum.GetValues<ExclusionReason>())
        {
            // Exercises the switch; the assertion is that it is total and does not throw.
            _ = WindowFilter.CanBeOverridden(reason);

            Assert.NotEqual("unknown", ManageDecision.No(reason).Explain());
        }
    }

    [Fact]
    public void TheBuiltInExclusionsAreStillTheDefaults()
    {
        // Being overridable does not mean being gone: the defaults are what make the
        // first five minutes bearable.
        Assert.True(WindowFilter.IsExcludedProcessName("SearchHost"));
        Assert.True(WindowFilter.IsExcludedProcessName("StartMenuExperienceHost"));
        Assert.True(WindowFilter.IsExcludedClassName("Shell_TrayWnd"));
        Assert.True(WindowFilter.IsExcludedClassName("Shell_InputSwitchTopLevelWindow"));

        Assert.False(WindowFilter.IsExcludedProcessName("firefox"));
        Assert.False(WindowFilter.IsExcludedClassName("MozillaWindowClass"));
    }

    [Fact]
    public void TheScreenshotOverlayIsExcludedButTheEditorIsNot()
    {
        // Win+Shift+S puts a full-screen overlay up for a second or two. Tiling it
        // animated the real windows aside to make room for something about to vanish.
        //
        // By class, not by process: the Snipping Tool also has an ordinary editor
        // window, and excluding the whole process would take that with it.
        Assert.True(WindowFilter.IsExcludedClassName("SnipOverlayRootWindow"));

        Assert.False(WindowFilter.IsExcludedProcessName("SnippingTool"));
    }

    [Fact]
    public void TheOverlayExclusionCanStillBeOverruled()
    {
        // It is a default, not a policy. Someone who wants it tiled may say so.
        Assert.True(WindowFilter.CanBeOverridden(ExclusionReason.ExcludedClass));
    }
}
