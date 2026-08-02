using Shubbak.Core.Tree;

namespace Shubbak.Native.Tests;

/// <summary>
/// Tests for the windows Shubbak refuses to tile without being told to.
/// </summary>
/// <remarks>
/// <para>
/// Dialogs and shell surfaces need handling out of the box. Nobody writes a rule for
/// the file-copy progress window until it has already thrown their layout around, and
/// by then they have formed a view about whether the window manager is any good.
/// GlazeWM ships the same defaults for the same reason.
/// </para>
/// <para>
/// These are the defaults, not a policy anyone can reach: they exist because a
/// tiling window manager that tiles a Save dialog is broken on arrival.
/// </para>
/// </remarks>
public sealed class DefaultWindowRuleTests
{
    [Theory]
    [InlineData("#32770")]                  // the standard Win32 dialog class
    [InlineData("OperationStatusWindow")]   // file copy, move and delete progress
    public void DialogClassesStartFloating(string className)
    {
        Assert.Equal(
            WindowState.Floating,
            WindowFilter.InitialStateForClass(className, WindowState.Tiling));
    }

    [Fact]
    public void OrdinaryWindowsStartTiled()
    {
        Assert.Equal(
            WindowState.Tiling,
            WindowFilter.InitialStateForClass("MozillaWindowClass", WindowState.Tiling));
    }

    [Fact]
    public void TheConfiguredDefaultIsHonouredForOrdinaryWindows()
    {
        // Floating everything is a legitimate choice, and the dialog rule must not
        // quietly override the opposite preference either.
        Assert.Equal(
            WindowState.Floating,
            WindowFilter.InitialStateForClass("MozillaWindowClass", WindowState.Floating));
    }

    [Theory]
    [InlineData("SearchHost")]                 // Windows 11 search
    [InlineData("SearchApp")]                  // Windows 10 search
    [InlineData("StartMenuExperienceHost")]    // Start
    [InlineData("ShellExperienceHost")]        // action centre and flyouts
    [InlineData("ScreenClippingHost")]         // Win+Shift+S
    [InlineData("LockApp")]
    [InlineData("TextInputHost")]              // IME candidates, emoji panel
    public void ShellProcessesAreNeverManaged(string processName)
    {
        Assert.True(
            WindowFilter.IsExcludedProcessName(processName),
            $"{processName} should never be tiled; it is part of the shell, not an application.");
    }

    [Fact]
    public void OrdinaryProcessesAreNotExcluded()
    {
        Assert.False(WindowFilter.IsExcludedProcessName("firefox"));
        Assert.False(WindowFilter.IsExcludedProcessName("explorer"));
    }

    [Fact]
    public void ExplorerIsNotExcluded()
    {
        // Stated on its own because it is tempting. The Win+Space language switcher
        // is titled "Input Flyout" and hosted by explorer, so excluding the process
        // would take File Explorer with it. Transient shell windows are handled by
        // waiting for a window to settle instead.
        Assert.False(WindowFilter.IsExcludedProcessName("explorer"));
    }
}
