namespace Shubbak.Core.Wm;

/// <summary>How a tag operation changes membership.</summary>
public enum TagMode
{
    /// <summary>Add the tag if absent.</summary>
    Add,

    /// <summary>Remove the tag if present.</summary>
    Remove,

    /// <summary>Add if absent, remove if present.</summary>
    Toggle,
}
