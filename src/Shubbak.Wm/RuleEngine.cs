using Shubbak.Config;
using Shubbak.Core.Commands;

namespace Shubbak.Wm;

/// <summary>
/// Decides which window rules apply to a window.
/// </summary>
/// <remarks>
/// <para>
/// Only the deciding. What a matched rule then does - moving focus to the window it
/// matched, running its commands, putting focus back - stays in the daemon, because
/// it needs the window manager, the IPC server and the platform, and pulling those
/// in here would make the one genuinely testable part of rule handling untestable
/// again.
/// </para>
/// <para>
/// Rule <i>parsing</i> has always been well covered. Rule <i>evaluation</i> had no
/// tests at all, which is how <c>on="title-change"</c> and <c>on="focus"</c> came to
/// be parsed, stored, and then dispatched from nowhere - a rule written against a
/// title that only appears once an application has loaded silently never ran.
/// </para>
/// <para>
/// Rules are indexed by trigger when the config is loaded rather than filtered on
/// every lookup. Building the attributes a rule matches on costs four Win32 calls and
/// a process handle, and title changes arrive continuously from browsers, terminals
/// and media players - so the question asked on that path is "are there any rules for
/// this trigger at all?", and it has to be free.
/// </para>
/// </remarks>
internal sealed class RuleEngine
{
    private static readonly WindowRule[] s_none = [];

    private IReadOnlyList<WindowRule> _onManage = s_none;
    private IReadOnlyList<WindowRule> _onTitleChange = s_none;
    private IReadOnlyList<WindowRule> _onFocus = s_none;

    private IReadOnlyDictionary<string, AppDefinition> _apps =
        new Dictionary<string, AppDefinition>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rebuilds the index from config.</summary>
    public void Load(ShubbakConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _apps = config.Apps;

        List<WindowRule>? onManage = null;
        List<WindowRule>? onTitleChange = null;
        List<WindowRule>? onFocus = null;

        foreach (WindowRule rule in config.Rules)
        {
            switch (rule.Trigger)
            {
                case RuleTrigger.OnManage: (onManage ??= []).Add(rule); break;
                case RuleTrigger.OnTitleChange: (onTitleChange ??= []).Add(rule); break;
                case RuleTrigger.OnFocus: (onFocus ??= []).Add(rule); break;
                default: break;
            }
        }

        // Left as the shared empty array when a trigger has no rules, which is the
        // common case for two of the three, so asking about them allocates nothing.
        _onManage = onManage ?? (IReadOnlyList<WindowRule>)s_none;
        _onTitleChange = onTitleChange ?? (IReadOnlyList<WindowRule>)s_none;
        _onFocus = onFocus ?? (IReadOnlyList<WindowRule>)s_none;
    }

    /// <summary>The rules declared for a trigger, in the order they were written.</summary>
    public IReadOnlyList<WindowRule> For(RuleTrigger trigger) => trigger switch
    {
        RuleTrigger.OnManage => _onManage,
        RuleTrigger.OnTitleChange => _onTitleChange,
        RuleTrigger.OnFocus => _onFocus,
        _ => s_none,
    };

    /// <summary>
    /// Whether any rule is waiting on this trigger.
    /// </summary>
    /// <remarks>
    /// Asked before the attributes are built, because building them is the expensive
    /// part and almost every configuration has no rule on title change or focus. It
    /// was previously two bool fields recomputed by a separate indexing pass; being
    /// derived from the index means the two cannot disagree.
    /// </remarks>
    public bool HasRulesFor(RuleTrigger trigger) => For(trigger).Count > 0;

    /// <summary>Whether a rule's matchers accept a window.</summary>
    public bool Matches(WindowRule rule, WindowAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.Matches(attributes, _apps);
    }

    /// <summary>Whether a rule asks for this window to be left alone.</summary>
    public bool ShouldIgnore(WindowAttributes attributes) =>
        HasAdoptionRule<IgnoreCommand>(attributes);

    /// <summary>Whether a rule asks for a window the built-in filter passed over.</summary>
    public bool ShouldForceManage(WindowAttributes attributes) =>
        HasAdoptionRule<ManageCommand>(attributes);

    /// <summary>
    /// Whether any matching rule carries a command of the given kind.
    /// </summary>
    /// <remarks>
    /// Only <see cref="RuleTrigger.OnManage"/> rules are consulted. Both questions
    /// this answers are asked while deciding whether to adopt a window, a moment the
    /// later triggers have not reached yet.
    /// </remarks>
    private bool HasAdoptionRule<TCommand>(WindowAttributes attributes)
        where TCommand : WmCommand
    {
        for (int i = 0; i < _onManage.Count; i++)
        {
            WindowRule rule = _onManage[i];

            if (!rule.Matches(attributes, _apps)) continue;

            foreach (WmCommand command in rule.Commands)
                if (command is TCommand) return true;
        }

        return false;
    }
}
