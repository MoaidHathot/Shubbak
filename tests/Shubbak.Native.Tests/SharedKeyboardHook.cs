namespace Shubbak.Native.Tests;

/// <summary>
/// Serialises every test that installs a keyboard hook.
/// </summary>
/// <remarks>
/// <para>
/// Only one <c>KeyboardSource</c> can be active at a time - the callback is static, so
/// a second hook silently wins it - and <c>Start</c> throws rather than allow that.
/// xUnit runs test classes in parallel unless they share a collection, so without this
/// two classes that each install a hook race, and the loser fails for a reason that
/// has nothing to do with what it was testing.
/// </para>
/// <para>
/// Tests within a single class already run sequentially, which is why this only became
/// necessary when a second hook-installing class was added.
/// </para>
/// <para>
/// Not named for the xUnit convention of ending in "Collection", because the analysers
/// reserve that suffix for types that are one.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SharedKeyboardHook
{
    public const string Name = "keyboard hook";
}
