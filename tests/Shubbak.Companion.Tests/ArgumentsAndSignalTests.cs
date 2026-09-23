using Shubbak.Companion;

namespace Shubbak.Companion.Tests;

/// <summary>The command line as the companions read it.</summary>
public sealed class ArgumentsTests
{
    [Fact]
    public void AFlagsValueIsTheArgumentAfterIt()
    {
        Assert.Equal("debug", Arguments.Value(["--log-level", "debug"], "--log-level"));
        Assert.Equal("C:\\x.kdl", Arguments.Value(["--quiet", "--config", "C:\\x.kdl"], "--config"));
    }

    [Fact]
    public void AFlagWithNothingAfterItHasNoValue()
    {
        // Two of the four copies this replaced read past the end here.
        Assert.Null(Arguments.Value(["--log-file"], "--log-file"));
        Assert.Null(Arguments.Value([], "--log-file"));
    }

    [Fact]
    public void AnotherFlagIsNeverReadAsAValue()
    {
        // `taj --log-file --quiet`: the file has no path, and --quiet is still a switch.
        Assert.Null(Arguments.Value(["--log-file", "--quiet"], "--log-file"));
        Assert.True(Arguments.Has(["--log-file", "--quiet"], "--quiet"));
    }

    [Fact]
    public void FlagsAreMatchedExactly()
    {
        Assert.Null(Arguments.Value(["--Config", "x"], "--config"));
        Assert.False(Arguments.Has(["--Quiet"], "--quiet"));
    }

    [Theory]
    [InlineData(new[] { "--help" }, true)]
    [InlineData(new[] { "-h" }, true)]
    [InlineData(new[] { "help" }, true)]
    [InlineData(new[] { "--config", "x", "--help" }, false)]
    [InlineData(new string[0], false)]
    public void HelpIsAskedForFirstOrNotAtAll(string[] args, bool asks)
    {
        Assert.Equal(asks, Arguments.AsksForHelp(args));
    }

    [Theory]
    [InlineData(new[] { "--version" }, true)]
    [InlineData(new[] { "-v" }, true)]
    [InlineData(new[] { "--config", "x", "version" }, true)]
    [InlineData(new[] { "--quiet" }, false)]
    public void VersionIsAskedForAnywhere(string[] args, bool asks)
    {
        Assert.Equal(asks, Arguments.AsksForVersion(args));
    }
}

/// <summary>What a signal event carries.</summary>
public sealed class SignalPayloadTests
{
    [Fact]
    public void ANameAndItsWordsAreRead()
    {
        SignalPayload? signal = SignalPayload.Parse("""{"name":"ayn","arguments":["microphone","mute"]}""");

        Assert.NotNull(signal);
        Assert.Equal("ayn", signal.Name);
        Assert.Equal(["microphone", "mute"], signal.Arguments);
        Assert.True(signal.IsFor("AYN"));
        Assert.False(signal.IsFor("palette"));
    }

    [Fact]
    public void NoArgumentsIsAnEmptyList()
    {
        SignalPayload? signal = SignalPayload.Parse("""{"name":"palette"}""");

        Assert.NotNull(signal);
        Assert.Empty(signal.Arguments);

        Assert.Empty(SignalPayload.Parse("""{"name":"palette","arguments":null}""")!.Arguments);
    }

    [Fact]
    public void ANumberWrittenBareStillArrivesAsAWord()
    {
        SignalPayload? signal = SignalPayload.Parse("""{"name":"palette","arguments":["run",3]}""");

        Assert.Equal(["run", "3"], signal!.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":7}""")]
    public void WhatIsNotASignalIsNullNotAnException(string? json)
    {
        Assert.Null(SignalPayload.Parse(json));
    }
}
