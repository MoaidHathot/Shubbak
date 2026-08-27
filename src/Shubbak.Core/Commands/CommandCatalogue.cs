namespace Shubbak.Core.Commands;

/// <summary>
/// The shape of one argument a command verb accepts.
/// </summary>
/// <remarks>
/// Coarse on purpose. This exists so that a client can offer sensible completions -
/// a workspace argument should be completed from the workspace list, a layout
/// argument from the layout registry - not so that anything can be validated from
/// it. Validation is the parser's job and stays there.
/// </remarks>
public enum CommandArgument
{
    /// <summary>A compass direction: left, right, up, down.</summary>
    Direction,

    /// <summary>Horizontal or vertical.</summary>
    Axis,

    /// <summary>A signed size change.</summary>
    Amount,

    /// <summary>The name of a workspace.</summary>
    WorkspaceName,

    /// <summary>The name of a layout, as known to the layout registry.</summary>
    LayoutName,

    /// <summary>A scratchpad slot name.</summary>
    ScratchpadSlot,

    /// <summary>The name of a binding mode declared in the config.</summary>
    BindingMode,

    /// <summary>A native window handle, decimal or <c>0x</c>-prefixed.</summary>
    WindowHandle,

    /// <summary>An arbitrary name announced to subscribed clients.</summary>
    SignalName,

    /// <summary>Everything after the verb, taken verbatim.</summary>
    CommandLine,
}

/// <summary>One command verb, as a user types it.</summary>
/// <param name="Verb">The word that begins the command.</param>
/// <param name="Summary">One line, in the imperative, for a menu or for help.</param>
/// <param name="Arguments">What follows the verb, in order.</param>
/// <param name="Aliases">Other spellings the parser accepts for the same thing.</param>
public sealed record CommandSpec(
    string Verb,
    string Summary,
    IReadOnlyList<CommandArgument> Arguments,
    IReadOnlyList<string> Aliases)
{
    /// <summary>Whether the verb stands alone.</summary>
    public bool TakesNoArguments => Arguments.Count == 0;
}

/// <summary>
/// Every command verb, described once.
/// </summary>
/// <remarks>
/// <para>
/// The command set was previously stated in three places that had to agree and had
/// no way of noticing when they did not: the parser's switch, a hand-written array of
/// strings used only to suggest corrections for typos, and the executor's switch. The
/// array was the one that rotted, because nothing fails when a suggestion list is
/// incomplete - the user simply gets no hint for the command that was added last.
/// </para>
/// <para>
/// This is now the single description, and the correspondence is enforced by tests
/// rather than by discipline: one asserts every verb here parses, another asserts
/// every verb the parser accepts is described here.
/// </para>
/// <para>
/// <b>It is deliberately not on the parsing path.</b> Routing the parser through this
/// would be the obvious next step and would be a mistake: parsing a config runs the
/// switch once per binding, and a compiled switch on a string is faster than a
/// dictionary built at first touch. So the table is built lazily and read only by
/// typo suggestion - which runs when a command has already failed - by the
/// <c>query commands</c> method, and by tests. A user who never mistypes a command
/// and never asks for the list never builds it at all, and
/// <see cref="IsBuilt"/> exists so a test can prove that stays true.
/// </para>
/// </remarks>
public static class CommandCatalogue
{
    private static Lazy<IReadOnlyList<CommandSpec>> s_commands = new(Build);

    /// <summary>Every verb, in a stable order suitable for display.</summary>
    public static IReadOnlyList<CommandSpec> Commands => s_commands.Value;

    /// <summary>Every verb and alias, for spell-checking an unknown word.</summary>
    public static IReadOnlyList<string> Verbs =>
        [.. Commands.SelectMany(c => new[] { c.Verb }.Concat(c.Aliases))];

    /// <summary>
    /// Whether the table has been built yet.
    /// </summary>
    /// <remarks>
    /// Not useful at runtime. It exists so that
    /// <c>CommandCatalogueTests.ParsingAValidConfigDoesNotBuildTheCatalogue</c> can
    /// hold the parser to the promise made above, rather than that promise being a
    /// comment somebody eventually optimises away.
    /// </remarks>
    public static bool IsBuilt => s_commands.IsValueCreated;

    /// <summary>
    /// Forgets the built table.
    /// </summary>
    /// <remarks>
    /// Internal for the same reason <c>Log.ResetForTests</c> is: this is process-wide
    /// state, tests must be able to return it to its initial condition, and production
    /// code must have no way to do so. Replacing the <see cref="Lazy{T}"/> rather than
    /// nulling a field keeps the build thread-safe.
    /// </remarks>
    internal static void ResetForTests() => s_commands = new Lazy<IReadOnlyList<CommandSpec>>(Build);

    /// <summary>Finds one verb, including by alias.</summary>
    public static CommandSpec? Find(string verb) => Commands.FirstOrDefault(c =>
        string.Equals(c.Verb, verb, StringComparison.OrdinalIgnoreCase) ||
        c.Aliases.Any(a => string.Equals(a, verb, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<CommandSpec> Build() =>
    [
        Spec("focus", "Move focus, or switch workspace", [CommandArgument.Direction]),
        Spec("focus-window", "Focus a window by handle, wherever it is", [CommandArgument.WindowHandle]),
        Spec("focus-recent-window", "Return to the window focused before this one"),
        Spec("move", "Move the focused window", [CommandArgument.Direction]),
        Spec("move-workspace", "Move this workspace to another monitor", [CommandArgument.Direction]),

        Spec("resize", "Grow or shrink the focused window", [CommandArgument.Axis, CommandArgument.Amount]),
        Spec("equalise", "Give siblings an equal share", aliases: ["equalize"]),

        Spec("split", "Split the container", [CommandArgument.LayoutName]),
        Spec("layout", "Set or cycle the container's layout", [CommandArgument.LayoutName]),
        Spec("toggle-tiling-direction", "Flip the container between rows and columns"),

        Spec("float", "Take the focused window out of the tiling flow"),
        Spec("tile", "Put the focused window back into the tiling flow"),
        Spec("toggle-floating", "Toggle the focused window between tiled and floating",
            aliases: ["toggle-tiling"]),
        Spec("toggle-fullscreen", "Toggle fullscreen for the focused window"),
        Spec("toggle-minimized", "Minimise the focused window, or restore it",
            aliases: ["toggle-minimised"]),
        Spec("close", "Ask the focused window to close"),

        Spec("tag", "Add, remove or set the focused window's workspace tags",
            [CommandArgument.WorkspaceName]),
        Spec("sticky", "Show the focused window on every workspace"),
        Spec("scratchpad", "Send the focused window to a scratchpad, or bring it back",
            [CommandArgument.ScratchpadSlot]),

        Spec("ignore", "Never manage this window (window rules only)"),
        Spec("manage", "Manage this window even if the filter would not (window rules only)"),
        Spec("toggle-managed", "Take on the foreground window, or release it"),

        Spec("signal", "Announce a named gesture to connected clients", [CommandArgument.SignalName]),
        Spec("shell-exec", "Run an external program", [CommandArgument.CommandLine]),

        Spec("wm-enable-binding-mode", "Enter a binding mode", [CommandArgument.BindingMode]),
        Spec("wm-disable-binding-mode", "Leave the current binding mode"),
        Spec("wm-toggle-pause", "Stop rearranging windows, or start again"),

        // Worded to make the difference from pause findable, because the two are one
        // word apart and do very different things. Pause keeps the keyboard; suspend
        // gives it back, which is what a game needs.
        Spec("wm-suspend", "Let go of the keyboard and stop managing windows"),
        Spec("wm-resume", "Take the keyboard back and manage windows again"),
        Spec("wm-toggle-suspend", "Let go of the keyboard, or take it back"),
        Spec("wm-reload-config", "Re-read the configuration file"),
        Spec("wm-redraw", "Force every window back to its computed rectangle"),
        Spec("wm-exit", "Shut the window manager down"),
    ];

    private static CommandSpec Spec(
        string verb,
        string summary,
        IReadOnlyList<CommandArgument>? arguments = null,
        IReadOnlyList<string>? aliases = null) =>
        new(verb, summary, arguments ?? [], aliases ?? []);
}
