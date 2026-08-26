using Shubbak.Core.Diagnostics;

namespace Shubbak.Core.Tests;

/// <summary>
/// The one version number four executables and a package manager have to agree on.
/// </summary>
/// <remarks>
/// There is no assertion here on a literal version: that would have to be edited on
/// every release and would be the fifth place the number lives. What is held to
/// account is the shape, because the shape is what a release pipeline parses and what
/// a tag is compared against.
/// </remarks>
public class ShubbakVersionTests
{
    [Fact]
    public void ThereIsAVersion()
    {
        Assert.False(string.IsNullOrWhiteSpace(ShubbakVersion.Current));
        Assert.NotEqual("unknown", ShubbakVersion.Current);
    }

    /// <summary>
    /// The four-part form is what <c>AssemblyName.Version</c> gives, and reporting it
    /// was the bug this replaced: a release tagged <c>v0.9.0</c> shipping a binary
    /// that called itself <c>0.9.0.0</c> looks like a mismatch to anything comparing
    /// the two, and to anyone reading a bug report.
    /// </summary>
    [Fact]
    public void ItIsNotTheFourPartAssemblyForm()
    {
        Assert.Equal(2, ShubbakVersion.Current.Count(c => c == '.'));
    }

    /// <summary>
    /// Source-link builds append <c>+&lt;commit&gt;</c>, which SemVer excludes from
    /// comparison and which nobody reading <c>--version</c> asked for.
    /// </summary>
    [Fact]
    public void BuildMetadataIsStripped()
    {
        Assert.False(ShubbakVersion.Current.Contains('+', StringComparison.Ordinal));
    }

    /// <summary>
    /// Parseable as a version, because a release workflow compares it to a git tag.
    /// </summary>
    [Fact]
    public void ItParsesAsAVersion()
    {
        Assert.True(Version.TryParse(ShubbakVersion.Current, out _));
    }

    [Fact]
    public void TheBannerNamesTheProduct()
    {
        Assert.Equal("Shubbak " + ShubbakVersion.Current, ShubbakVersion.Banner);
    }

    /// <summary>Computed once, so the startup path of four binaries does not repeat it.</summary>
    [Fact]
    public void ItIsStable()
    {
        Assert.Same(ShubbakVersion.Current, ShubbakVersion.Current);
    }
}
