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
        // would take File Explorer with it.
        Assert.False(WindowFilter.IsExcludedProcessName("explorer"));
    }

    [Fact]
    public void TheLanguageSwitcherIsExcludedByClass()
    {
        // The only thing that identifies it. It is hosted by explorer, so process
        // matching would take File Explorer with it, and it lives long enough that
        // waiting for it to disappear does not work either - a settling delay of
        // 150ms let it through, and lengthening that would make every window the
        // user opens visibly late.
        Assert.True(WindowFilter.IsExcludedClassName("Shell_InputSwitchTopLevelWindow"));
    }

    [Fact]
    public void TheLockScreenIsNotAWindowToTile()
    {
        // Locking the machine creates two of these, and they are hosted by explorer -
        // so, like the language switcher above, the process cannot be excluded without
        // excluding File Explorer with it.
        //
        // Found in a log rather than reasoned about:
        //
        //   managed 0xFF088A "Backstop Window" (explorer)
        //       [LockScreenBackstopFrame] Tiling -> workspace 3
        //   managed 0x7096C "Input Occlusion Window" (explorer)
        //       [LockScreenInputOcclusionFrame] Tiling -> workspace 3
        //
        // And they are not transient. That pair sat in the tree for three and a half
        // hours, from the machine locking until it was next unlocked, so a workspace
        // holding one real window spent the night splitting itself three ways with two
        // invisible participants.
        Assert.True(WindowFilter.IsExcludedClassName("LockScreenBackstopFrame"));
        Assert.True(WindowFilter.IsExcludedClassName("LockScreenInputOcclusionFrame"));
    }

    [Fact]
    public void OrdinaryClassesAreNotExcluded()
    {
        Assert.False(WindowFilter.IsExcludedClassName("MozillaWindowClass"));
        Assert.False(WindowFilter.IsExcludedClassName("CabinetWClass"));

        // Explorer's own windows in particular, since the two exclusions above are
        // explorer-hosted and an over-broad match would take the file manager with
        // them.
        Assert.False(WindowFilter.IsExcludedClassName("ExplorerWClass"));
    }
}
