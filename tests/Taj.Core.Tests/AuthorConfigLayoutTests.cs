using Shubbak.Config;
using Shubbak.Core.Geometry;
using Taj.Core;
using Shubbak.Ui.Layout;

namespace Taj.Core.Tests;

/// <summary>
/// Lays out the author's real config file, straight from disk.
/// </summary>
/// <remarks>
/// The other bar tests use a copy of the config embedded in the test. A copy can
/// drift, and when a user reports that the bar still looks wrong the first question
/// is whether the file on disk says what the test thinks it says. This removes that
/// question. It is skipped where the file does not exist, so it does not fail on
/// anyone else's machine.
/// </remarks>
public sealed class AuthorConfigLayoutTests
{
    private const string Path = @"P:\Github\Neovim-Moaid\config\shubbak\shubbak.kdl";
    private const int BarWidth = 1920;

    /// <summary>
    /// Proportional-ish measurement, so widths vary with text and font size the way
    /// a real font does. Exact values do not matter; relative placement does.
    /// </summary>
    private sealed class Measurer : ITextMeasurer
    {
        public Size Measure(string text, FontStyle font) =>
            new((int)((text ?? string.Empty).Length * font.Size * 0.55), (int)(font.Size * 1.4));
    }

    private static TajConfig? Load()
    {
        if (!File.Exists(Path)) return null;

        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) =
            TajConfigLoader.Load(File.ReadAllText(Path));

        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        return config;
    }

    private static VisualNode Arrange(BarProfile profile)
    {
        var model = new BarModel(profile);

        model.SetValue("workspaces", "1|Firefox|1|1|1\t2|Edge|0|1|0\t3|Code|0|1|0");
        model.SetValue("window.title", "herdrdev/herdr - Mozilla Firefox");
        model.SetValue("clock", "Sun 2 Aug 23:15");
        model.SetValue("seattle", "Sun 2 Aug 13:15");
        model.SetValue("layout", "\u2502\u2502");
        model.SetValue("binding_mode", string.Empty);

        VisualNode tree = model.Build();

        new FlexLayout(new Measurer()).Arrange(tree, new Rect(0, 0, BarWidth, profile.Height));

        return tree;
    }

    private static VisualNode? Find(VisualNode root, string id) =>
        root.SelfAndDescendants().FirstOrDefault(n => n.Id == id);

    /// <summary>Every profile the config actually declares.</summary>
    /// <remarks>
    /// Enumerated rather than named. These tests read a real configuration file that
    /// its owner is free to edit, and hardcoding profile names meant commenting one
    /// out failed the suite with a missing-key exception - reporting the test's
    /// assumption as though it were a fault in the bar.
    /// </remarks>
    public static TheoryData<string> Profiles()
    {
        var data = new TheoryData<string>();

        foreach (string name in Load()?.Profiles.Keys ?? (IEnumerable<string>)["default"])
            data.Add(name);

        return data;
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void TheClockIsOnTheRight(string profileName)
    {
        if (Load() is not { } config) return;
        if (!config.Profiles.TryGetValue(profileName, out BarProfile? profile)) return;

        VisualNode? clock = Find(Arrange(profile), "clock");

        Assert.NotNull(clock);

        Assert.True(
            clock.Rect.Left > BarWidth / 2,
            $"{profileName}: clock is at x={clock.Rect.Left} in a {BarWidth}px bar - it should be " +
            "in the right-hand half.");
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void NothingIsDrawnOutsideTheBar(string profileName)
    {
        if (Load() is not { } config) return;
        if (!config.Profiles.TryGetValue(profileName, out BarProfile? profile)) return;

        foreach (VisualNode node in Arrange(profile).SelfAndDescendants())
        {
            if (!node.Visible || node.Rect.IsEmpty) continue;

            Assert.True(
                node.Rect.Right <= BarWidth,
                $"{profileName}: '{node.Id}' ends at x={node.Rect.Right}, past the {BarWidth}px edge.");

            Assert.True(
                node.Rect.Bottom <= profile.Height,
                $"{profileName}: '{node.Id}' reaches y={node.Rect.Bottom} in a {profile.Height}px bar.");
        }
    }

    [Fact]
    public void SwitchingProfilesDoesNotMoveTheClock()
    {
        // The reported bug: the clock jumped to the other side of the bar on switch.
        if (Load() is not { } config) return;

        int? edge = null;

        foreach (BarProfile profile in config.Profiles.Values)
        {
            if (Find(Arrange(profile), "clock") is not { } clock) continue;

            edge ??= clock.Rect.Right;

            Assert.Equal(edge, clock.Rect.Right);
        }
    }

    [Fact]
    public void SwitchingProfilesDoesNotResizeTheBar()
    {
        // A height change means the window is resized and the shell is told a new
        // appbar reservation, on every switch to a presentation workspace. It reads
        // as the bar glitching rather than as a setting, and it was: the height was
        // the only thing the variant changed.
        if (Load() is not { } config) return;

        int[] heights = [.. config.Profiles.Values.Select(p => p.Height).Distinct()];

        Assert.True(
            heights.Length <= 1,
            $"profiles disagree on height ({string.Join(", ", heights)}), so the bar is " +
            "resized and the shell re-notified on every switch between them.");
    }
}
