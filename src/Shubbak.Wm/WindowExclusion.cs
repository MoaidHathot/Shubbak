using Shubbak.Config;
using Shubbak.Ipc;

namespace Shubbak.Wm;

/// <summary>A remembered refusal, including the rule that made it when there was one.</summary>
internal sealed record WindowExclusion(string Reason, string Summary, RuleReport? Rule = null)
{
    public static WindowExclusion ByRule(WindowRule rule) => new(
        $"Excluded by rule \"{rule.Name}\" (line {rule.Span.Start.Line}), which runs ignore on manage. " +
        "Choose Manage it to override it for this window, or Restore default management to re-evaluate it.",
        $"rule \"{rule.Name}\" (line {rule.Span.Start.Line})",
        new RuleReport(rule.Name, rule.Span.Start.Line, true,
            [.. rule.Commands.Select(c => c.Name)], WmDaemon.TriggerName(rule.Trigger)));
}
