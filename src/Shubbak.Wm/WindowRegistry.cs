using Shubbak.Core.Tree;

namespace Shubbak.Wm;

/// <summary>
/// Which windows Shubbak manages, and which it has decided not to.
/// </summary>
/// <remarks>
/// <para>
/// Four sets that have to agree with one another. Nearly every window-lifecycle bug
/// this program has had lived in the interplay between them rather than in any one
/// of them, so they are kept together with the operations that move a window between
/// them - and the orderings those operations depend on are stated here rather than
/// as a comment at each call site.
/// </para>
/// <para>
/// Two of the sets are verdicts and one is a moment, which is the distinction that
/// took longest to see:
/// </para>
/// <list type="bullet">
///   <item><b>Managed</b> - in the tree, sized by the layout.</item>
///   <item><b>Excluded</b> - a decision about the window. A rule refused it, or the
///   user let go of it by hand. Remembered so the same window is not re-judged on
///   every one of the many events it will raise.</item>
///   <item><b>Set aside</b> - a decision about a <i>moment</i>. The window was
///   concealed when Shubbak started and no session entry claimed it, so it was not
///   ours to reveal. That says nothing about the window itself, and the instant it
///   shows itself the evidence arrives and it is reconsidered.</item>
///   <item><b>Arriving</b> - taken on since the last layout, so it is placed rather
///   than animated into its first rectangle.</item>
/// </list>
/// <para>
/// Recording a set-aside window as excluded instead was wrong in a way that took a
/// while to see: an application sitting in the tray at startup was never managed
/// again however many times it was opened, and closing and reopening it worked -
/// because that made a new window with a handle the set had never heard of, which is
/// exactly how the fault was reported.
/// </para>
/// </remarks>
internal sealed class WindowRegistry
{
    private readonly Dictionary<nint, WindowNode> _managed = [];
    private readonly HashSet<nint> _excluded = [];
    private readonly HashSet<nint> _setAside = [];
    private readonly HashSet<nint> _arriving = [];

    public int ManagedCount => _managed.Count;

    public int ExcludedCount => _excluded.Count;

    public bool IsManaged(nint handle) => _managed.ContainsKey(handle);

    public bool IsExcluded(nint handle) => _excluded.Contains(handle);

    public bool TryGet(nint handle, out WindowNode window) => _managed.TryGetValue(handle, out window!);

    /// <summary>Every managed handle, for read-only iteration.</summary>
    public IReadOnlyCollection<nint> Handles => _managed.Keys;

    /// <summary>
    /// A copy of every managed handle.
    /// </summary>
    /// <remarks>
    /// For callers that release windows while walking, which mutates the dictionary
    /// being enumerated. Copying is the caller's protection and is stated as its own
    /// method so it cannot be forgotten.
    /// </remarks>
    public nint[] HandlesSnapshot() => [.. _managed.Keys];

    /// <summary>
    /// Every managed window, paired with its handle.
    /// </summary>
    /// <remarks>
    /// Returns the dictionary's own struct enumerator rather than an interface, so a
    /// <c>foreach</c> over the registry allocates nothing. That matters because this
    /// is walked from the tick, which may not allocate
    /// (docs/adr/0001-language-choice.md, constraint 2) - going through
    /// <see cref="Handles"/> would box an enumerator on every pass.
    /// <para>
    /// The dictionary must not be modified during the walk. Callers that release
    /// windows want <see cref="HandlesSnapshot"/> instead.
    /// </para>
    /// </remarks>
    public Dictionary<nint, WindowNode>.Enumerator GetEnumerator() => _managed.GetEnumerator();

    /// <summary>
    /// Whether a verdict has already been reached about this window.
    /// </summary>
    /// <remarks>
    /// The guard at the top of adoption. A window that is already managed, already
    /// refused, or set aside for now is not looked at again - which is what stops the
    /// many events a window raises from re-running the whole decision each time.
    /// </remarks>
    public bool AlreadyDecided(nint handle) =>
        _managed.ContainsKey(handle) || _excluded.Contains(handle) || _setAside.Contains(handle);

    /// <summary>Takes a window on.</summary>
    /// <remarks>
    /// Clears any exclusion, because adopting it is the newer decision, and marks it
    /// arriving so the first layout places it rather than animating it from whatever
    /// size the application happened to open at.
    /// </remarks>
    public void Adopt(nint handle, WindowNode window)
    {
        _excluded.Remove(handle);
        _managed[handle] = window;
        _arriving.Add(handle);
    }

    /// <summary>
    /// Lets go of a window and forgets every verdict about it.
    /// </summary>
    /// <remarks>
    /// <b>Forgetting comes first, and is the subtlety.</b> A caller that wants the
    /// window to stay released must say so - see <see cref="Release"/>'s
    /// <c>thenExclude</c> - because clearing the exclusion here means the very next
    /// event the window raises would take it straight back.
    /// </remarks>
    /// <returns>The node that was managed, or null if it was not.</returns>
    public WindowNode? Release(nint handle, bool thenExclude = false)
    {
        _excluded.Remove(handle);
        _setAside.Remove(handle);
        _arriving.Remove(handle);

        _ = _managed.Remove(handle, out WindowNode? window);

        // After the clearing, never before. This is the ordering the two callers that
        // want it used to have to remember for themselves.
        if (thenExclude) _excluded.Add(handle);

        return window;
    }

    /// <summary>Refuses a window without it having been managed.</summary>
    public void Exclude(nint handle) => _excluded.Add(handle);

    /// <summary>Forgets a refusal, so the window is judged afresh.</summary>
    public void Reconsider(nint handle) => _excluded.Remove(handle);

    /// <summary>
    /// Leaves a concealed window alone for now, without judging the window itself.
    /// </summary>
    public void SetAside(nint handle) => _setAside.Add(handle);

    /// <summary>
    /// Notes that a set-aside window has shown itself, so it can be judged again.
    /// </summary>
    /// <remarks>
    /// Coming out of the tray is the evidence that was missing at startup.
    /// </remarks>
    public void NoLongerSetAside(nint handle) => _setAside.Remove(handle);

    /// <summary>
    /// Whether this window is being placed for the first time, clearing the mark.
    /// </summary>
    /// <remarks>
    /// Cleared as it is read, so a window is only ever exempt for the single pass
    /// that first gives it a rectangle.
    /// </remarks>
    public bool TakeArriving(nint handle) => _arriving.Remove(handle);

    /// <summary>
    /// Forgets every refusal and every set-aside, and reports how many there were.
    /// </summary>
    /// <remarks>
    /// For a config reload. Both sets are caches of past verdicts and the verdicts
    /// have just changed: keeping them meant deleting an ignore rule and reloading did
    /// nothing at all until the window was closed and reopened, which reads as the
    /// reload not working. Windows released by hand are re-examined too, which is the
    /// honest reading of a reload - and toggle-managed is one key away for anything
    /// that should go back.
    /// </remarks>
    public int ForgetVerdicts()
    {
        int forgotten = _excluded.Count + _setAside.Count;

        _excluded.Clear();
        _setAside.Clear();

        return forgotten;
    }
}
