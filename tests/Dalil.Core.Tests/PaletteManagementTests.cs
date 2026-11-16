using Dalil.Core;
using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Ipc;

namespace Dalil.Core.Tests;

public sealed class PaletteManagementTests
{
    private static WindowReport Report(bool managed = false, WindowManagementInfo? management = null) =>
        new(1773010, "Recap | Microsoft Teams", "TeamsWebView", "ms-teams", null,
            0, 0, 800, 600, 0, 0, true, "None", false, true, "manageable", "manageable",
            managed, management?.Rule is not null, null, [], [], Management: management);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowAndInspectActionsOfferAllThreeExplicitChoicesWithoutAFocusRace(bool managed)
    {
        var window = new WindowCandidate(1773010, "Recap", "TeamsWebView", "ms-teams", 42,
            false, managed, "excluded", null, "none", null, false, null, false, false, 0);

        IReadOnlyList<PaletteAction> actions = PaletteActions.For(window, "1");
        IReadOnlyList<PaletteEntry> report = PaletteEntries.ForReport(Report(managed));

        foreach (string mode in new[] { "managed", "unmanaged", "default" })
        {
            string expected = $"set-managed {mode} --handle 1773010";
            PaletteAction action = Assert.Single(actions, a => a.Command == expected);
            Assert.False(action.Destructive);
            Assert.Contains(report, e => e.Command == expected && !e.Destructive);
            Assert.True(CommandParser.TryParse(action.Command, default, out WmCommand? command, out _));
            Assert.Equal(1773010, Assert.IsType<SetManagedCommand>(command).Handle);
        }

        Assert.Equal(managed ? "Stop managing it" : "Manage it",
            Assert.Single(actions, a => a.Chord == "Ctrl+Shift+A").Name);
        Assert.DoesNotContain(actions, a => a.Command.Contains("toggle-managed", StringComparison.Ordinal));
    }

    [Fact]
    public void RecapsManualReleaseIsExplainedBeforeTheAttributesAndHasADirectWayBack()
    {
        var management = new WindowManagementInfo("unmanaged",
            "Released by a per-window override, not by a rule.", "released by you");
        List<PaletteEntry> rows = [.. PaletteEntries.ForReport(Report(management: management))];

        Assert.Equal("no - released by you", rows[0].Primary);
        Assert.Equal(management.Reason, rows[1].Primary);
        Assert.True(rows.FindIndex(e => e.Primary == "Manage it") < rows.FindIndex(e => e.Secondary == "handle"));
        Assert.True(rows.FindIndex(e => e.Primary == "Restore default management") < rows.FindIndex(e => e.Secondary == "handle"));
        Assert.DoesNotContain(rows, e => e.Primary.Contains("excluded by a rule", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExcludingRuleIsNamedAndRemovableEvenWhenItNoLongerMatchesTheTitle()
    {
        var rule = new RuleReport("ignore loading", 289, true, ["ignore"], "manage");
        WindowReport report = Report(management: new WindowManagementInfo("default",
            "Excluded by rule \"ignore loading\" (line 289).", "rule \"ignore loading\" (line 289)", rule))
            with { Rules = [rule with { Matched = false }] };
        List<PaletteEntry> rows = [.. PaletteEntries.ForReport(report)];

        Assert.Contains("ignore loading", rows[0].Primary, StringComparison.Ordinal);
        PaletteEntry source = Assert.Single(rows, e => e.Secondary == "excluding rule");
        Assert.Contains("line 289", source.Primary, StringComparison.Ordinal);
        Assert.Equal(new RuleToRemove("ignore loading", 289, 1773010),
            Assert.Single(source.ResolveActions(), a => a.Removes is not null).Removes);
        Assert.Contains(rows, e => e.Primary.Contains("no longer matches", StringComparison.Ordinal));
        Assert.True(rows.IndexOf(source) < rows.FindIndex(e => e.Secondary == "style"));
    }
}
