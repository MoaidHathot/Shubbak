using Shubbak.Cli;

namespace Shubbak.Cli.Tests;

/// <summary>
/// The command line that autostart writes into the registry, and reads back out.
/// </summary>
/// <remarks>
/// These two operations have to be exact inverses. <c>status</c> compares the
/// registered executable against the one running now to tell the user their update
/// did not take effect, and a parser that disagrees with the composer would report
/// drift on every correct installation.
/// </remarks>
public class AutostartCommandLineTests
{
    [Fact]
    public void TheExecutableIsQuoted()
    {
        string command = Autostart.BuildCommand(@"C:\Program Files\Shubbak\shubbak-wm.exe", []);

        Assert.Equal(@"""C:\Program Files\Shubbak\shubbak-wm.exe""", command);
    }

    /// <summary>
    /// The failure this whole class exists for.
    /// </summary>
    /// <remarks>
    /// An unquoted path with a space is read by Windows as a command followed by
    /// arguments, so the default install location would try to execute
    /// <c>C:\Program</c>. It fails at logon, on someone else's machine, with nothing
    /// written down anywhere.
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Program Files\Shubbak\shubbak-wm.exe")]
    [InlineData(@"C:\Users\Some One\AppData\Local\Shubbak\shubbak-wm.exe")]
    [InlineData(@"D:\shubbak\shubbak-wm.exe")]
    public void APathSurvivesBeingWrittenAndReadBack(string path)
    {
        string command = Autostart.BuildCommand(path, []);

        Assert.Equal(path, Autostart.ExecutableFrom(command));
    }

    [Fact]
    public void ArgumentsFollowTheExecutable()
    {
        string command = Autostart.BuildCommand(
            @"C:\shubbak\shubbak-wm.exe", ["--config", @"D:\dotfiles\shubbak.kdl"]);

        Assert.Equal(@"""C:\shubbak\shubbak-wm.exe"" --config D:\dotfiles\shubbak.kdl", command);
    }

    /// <summary>An argument with a space is quoted too, for the same reason.</summary>
    [Fact]
    public void AnArgumentContainingASpaceIsQuoted()
    {
        string command = Autostart.BuildCommand(
            @"C:\shubbak\shubbak-wm.exe", ["--config", @"D:\my dotfiles\shubbak.kdl"]);

        Assert.Equal(
            @"""C:\shubbak\shubbak-wm.exe"" --config ""D:\my dotfiles\shubbak.kdl""", command);
    }

    /// <summary>
    /// Reading back a command that has arguments still yields only the executable.
    /// </summary>
    [Fact]
    public void ArgumentsAreNotMistakenForPartOfThePath()
    {
        string command = Autostart.BuildCommand(
            @"C:\Program Files\Shubbak\shubbak-wm.exe", ["--config", @"D:\a b\c.kdl"]);

        Assert.Equal(
            @"C:\Program Files\Shubbak\shubbak-wm.exe", Autostart.ExecutableFrom(command));
    }

    /// <summary>
    /// An entry written by hand, or by an older build, is unquoted and has to parse.
    /// </summary>
    /// <remarks>
    /// Anyone who set this up before the command existed wrote the value themselves,
    /// and almost nobody quotes a path with no spaces in it. Refusing to read that
    /// back would report "not set to start at logon" to someone whose Shubbak
    /// demonstrably starts at logon.
    /// </remarks>
    [Fact]
    public void AnUnquotedEntryIsStillUnderstood()
    {
        Assert.Equal(
            @"C:\shubbak\shubbak-wm.exe",
            Autostart.ExecutableFrom(@"C:\shubbak\shubbak-wm.exe"));

        Assert.Equal(
            @"C:\shubbak\shubbak-wm.exe",
            Autostart.ExecutableFrom(@"C:\shubbak\shubbak-wm.exe --config c.kdl"));
    }

    /// <summary>Surrounding whitespace is not part of the path.</summary>
    [Fact]
    public void SurroundingWhitespaceIsIgnored()
    {
        Assert.Equal(
            @"C:\shubbak\shubbak-wm.exe",
            Autostart.ExecutableFrom("  \"C:\\shubbak\\shubbak-wm.exe\"  "));
    }

    /// <summary>
    /// <c>status</c> compares the registered executable with the running one by file,
    /// not by string: winget's portable install reaches the executables through
    /// symlinks in a second directory, and both names are the same program.
    /// </summary>
    [Fact]
    public void TheSameFileThroughASymbolicLinkIsTheSameFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "shubbak-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string target = Path.Combine(directory, "shubbak-wm.exe");
            File.WriteAllText(target, "not really");

            string link = Path.Combine(directory, "link.exe");
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (IOException)
            {
                // Creating a symlink needs Developer Mode or a privilege this account
                // may not have. Nothing to test then; the plain comparison below still is.
                return;
            }

            Assert.True(Autostart.SameFile(link, target));
            Assert.True(Autostart.SameFile(target, link));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DifferentFilesAreDifferent()
    {
        Assert.False(Autostart.SameFile(@"C:\Program Files\Shubbak\shubbak-wm.exe", @"D:\portable\shubbak-wm.exe"));
    }

    [Fact]
    public void TheSamePathSpelledDifferentlyIsTheSameFile()
    {
        Assert.True(Autostart.SameFile(@"C:\Program Files\Shubbak\shubbak-wm.exe", @"c:\program files\.\Shubbak\SHUBBAK-WM.EXE"));
    }
}