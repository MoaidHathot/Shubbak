using System.Text.Json;

namespace Shubbak.Ipc.Tests;

public sealed class WindowManagementWireTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogueAndInspectCarryTheSameDecisionThroughSourceGeneratedJson(bool byRule)
    {
        var management = new WindowManagementInfo(byRule ? "default" : "unmanaged",
            byRule ? "Excluded by a named rule." : "Released by a per-window override.",
            byRule ? "rule \"leave Teams alone\" (line 289)" : "released by you",
            byRule ? new RuleReport("leave Teams alone", 289, true, ["ignore"], "manage") : null);
        var candidate = new WindowCandidate(42, "Recap", "TeamsWebView", "ms-teams", 123,
            false, false, management.Reason, null, "none", null, false, null, false, false, 0,
            ExclusionSummary: management.Summary, Manageable: true, Management: management);
        var report = new WindowReport(42, "Recap", "TeamsWebView", "ms-teams", null,
            0, 0, 800, 600, 0, 0, true, "None", false, true, "manageable", "manageable",
            false, byRule, null, [], [], Management: management);

        WindowCandidate readCandidate = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(candidate, IpcJsonContext.Default.WindowCandidate), IpcJsonContext.Default.WindowCandidate)!;
        WindowReport readReport = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(report, IpcJsonContext.Default.WindowReport), IpcJsonContext.Default.WindowReport)!;

        Assert.Equal(management.Mode, readCandidate.Management?.Mode);
        Assert.Equal(management.Reason, readReport.Management?.Reason);
        Assert.Equal(management.Summary, readCandidate.ExclusionSummary);
        Assert.Equal(byRule, readReport.ExcludedByRule);
        Assert.Equal(management.Rule?.Name, readReport.Management?.Rule?.Name);
        Assert.Equal(management.Rule?.Line, readCandidate.Management?.Rule?.Line);
        if (byRule) Assert.Equal(["ignore"], readReport.Management!.Rule!.Does);
    }

    [Fact]
    public void OlderReportsLeaveProvenanceUnknown()
    {
        WindowReport report = JsonSerializer.Deserialize("""
            {"handle":42,"managed":false,"excluded_by_rule":true,"rules":[],"apps":[]}
            """, IpcJsonContext.Default.WindowReport)!;
        Assert.True(report.ExcludedByRule);
        Assert.Null(report.Management);
    }
}
