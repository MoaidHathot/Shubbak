using Shubbak.Core.Geometry;
using Shubbak.Ui.Layout;

namespace Taj.Core.Tests;

/// <summary>
/// A predictable text measurer.
/// </summary>
/// <remarks>
/// <para>
/// Every character is a fixed width and every line a fixed height, so layout tests
/// assert exact pixel values without depending on installed fonts. Real measurement
/// is the renderer's job; the interface exists precisely so it can be substituted
/// here.
/// </para>
/// <para>
/// Deliberately duplicated from Shubbak.Ui.Tests rather than shared. The two live in
/// separate assemblies and the type is internal, so sharing it would mean one test
/// project referencing another - which makes build order matter and risks xunit
/// discovering the same tests twice. Eight lines of test double is the cheaper
/// duplication, and it matches how TreeBuilder and WmFixture are already scoped to
/// the projects that use them.
/// </para>
/// </remarks>
internal sealed class FixedTextMeasurer : ITextMeasurer
{
    public const int CharacterWidth = 10;
    public const int LineHeight = 16;

    public Size Measure(string text, FontStyle font) =>
        new((text ?? string.Empty).Length * CharacterWidth, LineHeight);
}
