using System.Reflection;

namespace Shubbak.Core.Diagnostics;

/// <summary>
/// The product version, as one string that every executable agrees on.
/// </summary>
/// <remarks>
/// <para>
/// There is one <c>&lt;Version&gt;</c> in <c>Directory.Build.props</c> and four
/// executables that must report it identically. Reading it here, from the assembly
/// the SDK stamped, means none of them can carry a number of its own that drifts.
/// </para>
/// <para>
/// The assembly read is <c>Shubbak.Core</c>'s, not the entry executable's, and that
/// is deliberate: every executable references this project and every project takes
/// its version from the same property, so Core's version <em>is</em> the product
/// version. Reading the entry assembly instead would report whatever the host
/// happened to be, which under a test runner is the test runner.
/// </para>
/// <para>
/// <see cref="AssemblyInformationalVersionAttribute"/> is preferred over
/// <see cref="AssemblyName.Version"/> because the latter is always four parts -
/// <c>0.9.0.0</c> for a version written as <c>0.9.0</c> - and a package manager
/// comparing that against the tag it published would see a mismatch that is not one.
/// </para>
/// </remarks>
public static class ShubbakVersion
{
    /// <summary>The product version, such as <c>0.9.0</c>.</summary>
    /// <remarks>
    /// Computed once. Nothing here can fail in a way worth retrying, and this is read
    /// on the startup path of every executable.
    /// </remarks>
    public static string Current { get; } = Read();

    /// <summary>The version with the product name, such as <c>Shubbak 0.9.0</c>.</summary>
    public static string Banner => "Shubbak " + Current;

    private static string Read()
    {
        Assembly assembly = typeof(ShubbakVersion).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return StripBuildMetadata(informational);

        // AssemblyName.Version is always populated, so this is a fallback that in
        // practice never runs - but "unknown" in a bug report is worse than 0.9.0.0.
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Removes the <c>+&lt;commit&gt;</c> suffix that source-link builds append.
    /// </summary>
    /// <remarks>
    /// SemVer calls everything after <c>+</c> build metadata and excludes it from
    /// comparison. Keeping it would make <c>--version</c> disagree with the git tag
    /// for a reason nobody reading the output could be expected to know.
    /// </remarks>
    private static string StripBuildMetadata(string version)
    {
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
