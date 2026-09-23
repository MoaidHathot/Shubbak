using Dalil.Core;

namespace Dalil.Tests;

/// <summary>
/// The host's half of the palette's new manners: a reload that keeps its scale, a
/// title that reads right to left, and the command history on disk.
/// </summary>
public sealed class PaletteHostBehaviourTests
{
    // ---- scale -----------------------------------------------------------------

    [Fact]
    public void AReloadedConfigIsScaledLikeTheOriginal()
    {
        // The bug: Reconfigure installed the file's numbers raw, so a palette on a
        // 150 percent display shrank to two thirds on every save until it was closed
        // and opened again.
        var config = new DalilConfig { Width = 720, RowHeight = 36, FontSize = 14 };

        DalilConfig scaled = PaletteWindow.Scaled(config, 1.5);

        Assert.Equal(1080, scaled.Width);
        Assert.Equal(54, scaled.RowHeight);
        Assert.Equal(21, scaled.FontSize);
    }

    [Fact]
    public void AScaleOfOneIsTheConfigItself()
    {
        var config = new DalilConfig { Width = 720 };

        Assert.Same(config, PaletteWindow.Scaled(config, 1.0));
    }

    // ---- direction -------------------------------------------------------------

    [Theory]
    [InlineData("مستند - Word", true)]
    [InlineData("שלום", true)]
    [InlineData("Chat | Ohad", false)]
    [InlineData("Café résumé", false)]
    [InlineData("", false)]
    [InlineData("a\u200Fb", true)]
    public void RightToLeftTextIsRecognised(string text, bool expected) =>
        Assert.Equal(expected, PaletteRenderer.ContainsRightToLeft(text));

    // ---- the history on disk ----------------------------------------------------

    [Fact]
    public void WhatWasRunIsThereAfterAReopen()
    {
        string directory = Path.Combine(Path.GetTempPath(), "shubbak-frecency-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "dalil-frecency.tsv");

        try
        {
            FrecencyStore first = FrecencyStore.Open(path);
            first.Ran("focus");
            first.Ran("focus");
            first.Ran("Dev layout");

            Assert.True(File.Exists(path));

            FrecencyStore second = FrecencyStore.Open(path);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            Assert.True(second.Record.WeightOf("focus", now) > second.Record.WeightOf("Dev layout", now));
            Assert.True(second.Record.WeightOf("Dev layout", now) > 0);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMissingFileIsAnEmptyHistoryNotAFault()
    {
        string path = Path.Combine(Path.GetTempPath(), "shubbak-frecency-" + Guid.NewGuid().ToString("N"), "none.tsv");

        FrecencyStore store = FrecencyStore.Open(path);

        Assert.Empty(store.Record.Keys);
    }
    // ---- what the renderer draws ------------------------------------------------

    /// <summary>Remembers every call, so a paint can be asserted on without a window.</summary>
    private sealed class RecordingRenderer : Shubbak.Ui.Rendering.IRenderer
    {
        public List<string> Calls { get; } = [];

        public Shubbak.Ui.Layout.Size Measure(string text, Shubbak.Ui.Layout.FontStyle font) =>
            new((text ?? string.Empty).Length * 10, 16);

        public void BeginFrame(Shubbak.Core.Geometry.Rect bounds, Shubbak.Core.Rendering.Colour background) => Calls.Add("begin");

        public void FillRectangle(Shubbak.Core.Geometry.Rect rect, Shubbak.Core.Rendering.Colour colour, int cornerRadius = 0) =>
            Calls.Add($"fill {rect.Width}x{rect.Height} {colour}");

        public void DrawRectangle(Shubbak.Core.Geometry.Rect rect, Shubbak.Core.Rendering.Colour colour, int thickness, int cornerRadius = 0) =>
            Calls.Add("outline");

        public void DrawText(string text, Shubbak.Core.Geometry.Rect rect, Shubbak.Core.Rendering.Colour colour, Shubbak.Ui.Layout.FontStyle font) =>
            Calls.Add($"text \"{text}\"");

        public void EndFrame() => Calls.Add("end");

        public void Dispose() { }
    }

    private static (RecordingRenderer Renderer, DalilConfig Config) Painted(Action<PaletteModel> arrange)
    {
        var config = new DalilConfig();
        var model = new PaletteModel();
        model.SetEntries([new PaletteEntry("Visual Studio", string.Empty, [], "focus-window 1")]);
        arrange(model);

        var renderer = new RecordingRenderer();
        var layout = new PaletteLayout(config, 1.0, new Shubbak.Core.Geometry.Rect(0, 0, config.Width, 400));

        PaletteRenderer.Draw(renderer, model, config, layout);

        return (renderer, config);
    }

    [Fact]
    public void SelectAllIsDrawnAsAPillBehindTheTerm()
    {
        (RecordingRenderer selected, DalilConfig config) = Painted(m =>
        {
            m.SetQuery("stud");
            m.SelectAll();
        });

        (RecordingRenderer plain, _) = Painted(m => m.SetQuery("stud"));

        // One more fill than the same paint without the selection, in the selection
        // colour, and as wide as the four ten-pixel characters plus its margins.
        string pill = $"fill 44x{config.FontSize + 8} {config.SelectionBackground}";

        Assert.Contains(pill, selected.Calls);
        Assert.DoesNotContain(pill, plain.Calls);
    }

    [Fact]
    public void ARightToLeftTitleIsDrawnWholeEvenWhenItMatched()
    {
        (RecordingRenderer renderer, _) = Painted(m =>
        {
            m.SetEntries([new PaletteEntry("مستند - Word", string.Empty, [], "focus-window 1")]);
            m.SetQuery("word");
        });

        Assert.Contains("text \"مستند - Word\"", renderer.Calls);
        Assert.DoesNotContain("text \"Word\"", renderer.Calls);
    }

    [Fact]
    public void ALeftToRightTitleIsDrawnInPieces()
    {
        (RecordingRenderer renderer, _) = Painted(m => m.SetQuery("stud"));

        Assert.Contains("text \"Stud\"", renderer.Calls);
        Assert.Contains("text \"io\"", renderer.Calls);
    }
}