using Shubbak.Core.Commands;

namespace Shubbak.Config.Tests;

/// <summary>
/// Spelling a value so the tokeniser reads it back as one token.
/// </summary>
/// <remarks>
/// For everything that builds a command from a name it did not choose: the palette
/// for the row's workspace, the bar for the one clicked, the command line for what
/// was typed. The property that matters is the round trip - what <see cref="CommandParser.Quote"/>
/// writes, <c>Tokenise</c> reads back as exactly the value.
/// </remarks>
public sealed class CommandQuotingTests
{
    private static string RoundTrip(string value)
    {
        string command = $"focus --workspace {CommandParser.Quote(value)}";

        Assert.True(
            CommandParser.TryParse(command, default, out WmCommand? parsed, out Diagnostic? problem),
            $"'{command}' did not parse: {problem?.Message}");

        return ((FocusWorkspaceCommand)parsed!).Workspace;
    }

    [Theory]
    [InlineData("3")]
    [InlineData("code")]
    [InlineData("-")]
    [InlineData("\\")]
    [InlineData("'")]
    [InlineData("\"")]
    [InlineData("Second Monitor")]
    [InlineData("it's here")]
    [InlineData("say \"hi\"")]
    [InlineData("\ttabbed")]
    [InlineData("--focus")]
    [InlineData("عربي")]
    public void WhatIsQuotedIsReadBackAsItself(string value)
    {
        Assert.Equal(value, RoundTrip(value));
    }

    [Theory]
    [InlineData("3", "3")]
    [InlineData("code", "code")]
    [InlineData("-", "-")]
    [InlineData("Second Monitor", "\"Second Monitor\"")]
    [InlineData("'", "\"'\"")]
    [InlineData("\"", "'\"'")]
    [InlineData("", "\"\"")]
    public void NothingIsQuotedThatNeedNotBe(string value, string expected)
    {
        Assert.Equal(expected, CommandParser.Quote(value));
    }

    [Fact]
    public void AValueWithBothKindsOfQuoteCannotBeWritten()
    {
        // The tokeniser has no escape, so there is no spelling; the loader refuses such
        // a workspace name up front (SHB0454) so nothing downstream meets one.
        Assert.False(CommandParser.CanQuote("it's \"odd\""));
        Assert.Throws<ArgumentException>(() => CommandParser.Quote("it's \"odd\""));

        Assert.True(CommandParser.CanQuote("it's fine"));
        Assert.True(CommandParser.CanQuote("say \"hi\""));
    }

    [Fact]
    public void TheLoaderRefusesAWorkspaceNameNoCommandCouldWrite()
    {
        ConfigLoadResult result = ConfigLoader.Load("""
            workspaces {
                workspace "it's \"odd\""
                workspace "fine"
            }
            """);

        Diagnostic error = Assert.Single(result.Diagnostics, d => d.Code == "SHB0454");
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal("fine", Assert.Single(result.Config.Workspaces).Name);
    }
}
