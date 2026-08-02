using Shubbak.Core.Diagnostics;

namespace Shubbak.Core.Tests;

/// <summary>
/// Tests for the diagnostic report.
/// </summary>
/// <remarks>
/// The report had no tests, which mattered once: a session was spent arguing about
/// behaviour that the source did not have, because the binary being run was an older
/// build and nothing in the report said so. The binary identity lines exist to end
/// that class of confusion, so they are worth pinning down.
/// </remarks>
public sealed class DiagnosticReportTests
{
    [Fact]
    public void TheEnvironmentSectionNamesTheRunningExecutable()
    {
        string report = new DiagnosticReport("test").AddEnvironment().ToString();

        Assert.Contains("**Executable**", report, StringComparison.Ordinal);

        // The path of the test host, but a real path either way - the point is that
        // whatever is running says so rather than leaving it to be guessed.
        Assert.Contains(Environment.ProcessPath!, report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnvironmentSectionDatesTheBinary()
    {
        string report = new DiagnosticReport("test").AddEnvironment().ToString();

        Assert.Contains("**Built**", report, StringComparison.Ordinal);
        Assert.Contains("UTC", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportStatesItsReason()
    {
        string report = new DiagnosticReport("windows went missing").ToString();

        Assert.Contains("windows went missing", report, StringComparison.Ordinal);
    }
}
