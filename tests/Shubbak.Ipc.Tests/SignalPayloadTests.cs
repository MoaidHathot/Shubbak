using Shubbak.Ipc;

namespace Shubbak.Ipc.Tests;

/// <summary>What a signal event carries, read and written by the one type both ends share.</summary>
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
    public void WhatTheWindowManagerWritesIsWhatAClientReads()
    {
        // The daemon publishes through Payload; every companion reads through Parse.
        // The exact text is the contract a hand-written publisher on the pipe can
        // rely on: both keys always present, the arguments a JSON array of strings.
        Assert.Equal(
            """{"name":"battery","arguments":["41"]}""",
            SignalPayload.Payload("battery", ["41"]));

        Assert.Equal(
            """{"name":"announce","arguments":[]}""",
            SignalPayload.Payload("announce", []));

        SignalPayload? back = SignalPayload.Parse(SignalPayload.Payload("weather", ["Sunny 21C", "say \"hi\"", "tab\there"]));

        Assert.NotNull(back);
        Assert.Equal("weather", back.Name);
        Assert.Equal(["Sunny 21C", "say \"hi\"", "tab\there"], back.Arguments);
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

    [Fact]
    public void ANestedValueArrivesAsItsJson()
    {
        // Nothing the window manager sends today; kept so a hand-written publisher on
        // the pipe is read the way the document-based parser read it.
        SignalPayload? signal = SignalPayload.Parse("""{"name":"x","arguments":[{"a":[1,2]},true,null,"s"]}""");

        Assert.Equal(["""{"a":[1,2]}""", "true", "null", "s"], signal!.Arguments);
    }

    [Fact]
    public void UnknownPropertiesAndAnOrderOfTheirOwnAreTolerated()
    {
        SignalPayload? signal = SignalPayload.Parse("""{"extra":{"deep":[1,{"x":2}]},"arguments":["a"],"name":"ayn","later":5}""");

        Assert.NotNull(signal);
        Assert.Equal("ayn", signal.Name);
        Assert.Equal(["a"], signal.Arguments);
    }

    [Fact]
    public void ArgumentsThatAreNotAListAreNoArguments()
    {
        Assert.Empty(SignalPayload.Parse("""{"name":"palette","arguments":"run"}""")!.Arguments);
        Assert.Empty(SignalPayload.Parse("""{"name":"palette","arguments":{"a":1}}""")!.Arguments);
    }

    [Fact]
    public void EscapesInAWordAreDecoded()
    {
        SignalPayload? signal = SignalPayload.Parse("""{"name":"speaker","arguments":["Speakers (Realtek\u0028R\u0029 Audio) \"quoted\""]}""");

        Assert.Equal(["Speakers (Realtek(R) Audio) \"quoted\""], signal!.Arguments);
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
    [InlineData("""{"name":"x","arguments":[1,2""")]
    [InlineData("{\"name\":\"x\"")]
    public void WhatIsNotASignalIsNullNotAnException(string? json)
    {
        Assert.Null(SignalPayload.Parse(json));
    }
}
