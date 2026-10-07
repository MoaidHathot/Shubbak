using Shubbak.Config;
using Taj.Core;

namespace Taj.Core.Tests;

/// <summary>
/// The second click command the bar performs itself: pressing a media key.
/// </summary>
/// <remarks>
/// Parsed in the core so the loader can refuse a misspelling where the file can be
/// pointed at; the key press itself is the host's, and is not tested here. What is
/// tested is the vocabulary - which words mean which key, since the keybinding names
/// are already in people's files - and that a bad one is said at load.
/// </remarks>
public sealed class MediaCommandTests
{
    [Theory]
    [InlineData("media play-pause", MediaKey.PlayPause)]
    [InlineData("media play", MediaKey.PlayPause)]
    [InlineData("media pause", MediaKey.PlayPause)]
    [InlineData("media toggle", MediaKey.PlayPause)]
    [InlineData("media media_play_pause", MediaKey.PlayPause)]
    [InlineData("media next", MediaKey.Next)]
    [InlineData("media media_next", MediaKey.Next)]
    [InlineData("media previous", MediaKey.Previous)]
    [InlineData("media prev", MediaKey.Previous)]
    [InlineData("media stop", MediaKey.Stop)]
    [InlineData("media mute", MediaKey.Mute)]
    [InlineData("media volume_mute", MediaKey.Mute)]
    [InlineData("media volume-up", MediaKey.VolumeUp)]
    [InlineData("media volume_up", MediaKey.VolumeUp)]
    [InlineData("media up", MediaKey.VolumeUp)]
    [InlineData("media volume-down", MediaKey.VolumeDown)]
    [InlineData("media down", MediaKey.VolumeDown)]
    [InlineData("  MEDIA   Next  ", MediaKey.Next)]
    public void TheKeysAreSpelledSeveralWays(string command, MediaKey expected)
    {
        Assert.True(MediaCommand.TryParse(command, out MediaCommand? parsed, out string? problem), problem);
        Assert.Equal(expected, parsed!.Key);
    }

    [Theory]
    [InlineData("media")]
    [InlineData("media louder please")]
    [InlineData("media rewind")]
    [InlineData("exec mixer")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsRefusedWithAReason(string? command)
    {
        Assert.False(MediaCommand.TryParse(command, out MediaCommand? parsed, out string? problem));
        Assert.Null(parsed);
        Assert.NotNull(problem);
        Assert.Contains("media play-pause", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerbIsRecognisedOnTheFirstWordAlone()
    {
        // So a malformed media command is refused with a reason rather than sent to a
        // window manager that has never heard of the verb.
        Assert.True(MediaCommand.Recognises("media next"));
        Assert.True(MediaCommand.Recognises("media rewind"));
        Assert.True(MediaCommand.Recognises("  Media  "));
        Assert.False(MediaCommand.Recognises("mediaplayer next"));
        Assert.False(MediaCommand.Recognises("exec media"));
        Assert.False(MediaCommand.Recognises(null));
    }

    [Fact]
    public void ItSaysWhatItIsInTheCanonicalSpelling()
    {
        Assert.True(MediaCommand.TryParse("media play", out MediaCommand? parsed, out _));
        Assert.Equal("media play-pause", parsed!.ToString());

        Assert.True(MediaCommand.TryParse("media up", out parsed, out _));
        Assert.Equal("media volume-up", parsed!.ToString());
    }

    [Fact]
    public void TheKeyboardVerbIsNotThisOneAndViceVersa()
    {
        Assert.False(MediaCommand.Recognises("keyboard next"));
        Assert.False(KeyboardCommand.Recognises("media next"));
    }

    // ---- at load ---------------------------------------------------------------

    [Fact]
    public void AMediaPillLoadsClean()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                source "track" kind="signal"
                profile "default" {
                    zone "right" {
                        text template="{{ track | truncate:30 }}" on-click="media play-pause" on-double-click="media next" on-right-click="media previous" on-scroll-up="media volume-up" on-scroll-down="media volume-down" on-middle-click="media mute"
                    }
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ABadMediaCommandIsReportedWithTheGestureItWasWrittenOn()
    {
        (_, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "right" {
                        text id="track" template="x" on-click="media play-pause" on-scroll-up="media louder please"
                    }
                }
            }
            """);

        Diagnostic warning = Assert.Single(diagnostics, d => d.Code == "TAJ0023");

        Assert.Contains("on-scroll-up", warning.Message, StringComparison.Ordinal);
        Assert.Contains("louder please", warning.Message, StringComparison.Ordinal);
        Assert.Contains("media play-pause", warning.Hint!, StringComparison.Ordinal);
    }
}
