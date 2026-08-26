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
}
