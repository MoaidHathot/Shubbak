using Shubbak.Wm;

namespace Shubbak.Wm.Tests;

/// <summary>
/// Which startup commands can skip the shell.
/// </summary>
/// <remarks>
/// <para>
/// Every startup command went through <c>ShellExecuteEx</c>. Launching the bar took
/// 2,126 ms of a 2,472 ms startup - 86% of it - and the bar had not begun running
/// when the call returned, so none of that was the bar starting. It was all shell
/// overhead, on a local disk.
/// </para>
/// <para>
/// <c>CreateProcess</c> takes milliseconds, and cannot do everything the shell can.
/// The whole question is therefore where the two are definitely equivalent, and
/// answering it wrongly in the permissive direction means a startup command that
/// silently stops working.
/// </para>
/// </remarks>
public sealed class DirectLaunchTests
{
    private static string TempFile(string extension)
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"shubbak-launch-{Guid.NewGuid():N}{extension}");

        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void AFullPathToAnExecutableThatExistsGoesDirect()
    {
        string path = TempFile(".exe");

        try
        {
            Assert.True(WmDaemon.CanLaunchDirectly(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheExtensionIsMatchedWithoutRegardToCase()
    {
        string path = TempFile(".EXE");

        try
        {
            Assert.True(WmDaemon.CanLaunchDirectly(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    public void ABatchFileNeedsACommandProcessor(string extension)
    {
        // CreateProcess cannot run one. Taking the fast path here would mean a startup
        // command that simply stopped working.
        string path = TempFile(extension);

        try
        {
            Assert.False(WmDaemon.CanLaunchDirectly(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AShortcutNeedsResolving()
    {
        string path = TempFile(".lnk");

        try
        {
            Assert.False(WmDaemon.CanLaunchDirectly(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ADocumentNeedsAVerbLookedUp()
    {
        string path = TempFile(".txt");

        try
        {
            Assert.False(WmDaemon.CanLaunchDirectly(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("shell:startup")]
    public void AUrlOrShellVerbStaysWithTheShell(string target) =>
        Assert.False(WmDaemon.CanLaunchDirectly(target));

    [Theory]
    [InlineData("notepad")]
    [InlineData("notepad.exe")]
    [InlineData("wt.exe")]
    public void ABareNameIsLeftToThePathSearchTheShellDoes(string target)
    {
        // Resolving a bare name the way the shell does is exactly the work being
        // avoided, so it is not attempted.
        Assert.False(WmDaemon.CanLaunchDirectly(target));
    }

    [Theory]
    [InlineData(@".\taj.exe")]
    [InlineData(@"..\dist\taj.exe")]
    [InlineData(@"dist\taj.exe")]
    public void ARelativePathIsNotFullyQualified(string target) =>
        Assert.False(WmDaemon.CanLaunchDirectly(target));

    [Fact]
    public void AnExecutableThatIsNotThereStaysWithTheShell()
    {
        // The shell produces a better error for a missing target than CreateProcess,
        // and this is a path nobody should reach silently.
        Assert.False(WmDaemon.CanLaunchDirectly(@"C:\definitely\not\here\nothing.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingAtAllStaysWithTheShell(string? target) =>
        Assert.False(WmDaemon.CanLaunchDirectly(target!));
}
