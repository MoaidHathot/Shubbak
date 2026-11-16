using Shubbak.Core.Commands;

namespace Shubbak.Wm;

/// <summary>
/// Applies explicit choices to one handle. Native operations are supplied by the
/// daemon so closed targets, repeated requests and failed adoption can be exercised
/// without changing the user's desktop.
/// </summary>
internal sealed class WindowManagementController(
    WindowRegistry windows,
    Func<nint, bool> exists,
    Func<nint, string?> manage,
    Action<nint> release,
    Action<nint> restoreDefault)
{
    /// <summary>Null on success; otherwise the reason the choice could not be applied.</summary>
    public string? Set(nint handle, WindowManagementMode mode)
    {
        if (!exists(handle)) return "The selected window has closed or is no longer available.";

        WindowManagementMode previous = windows.OverrideFor(handle);
        WindowExclusion? previousExclusion = windows.ExclusionFor(handle);
        windows.SetOverride(handle, mode);

        switch (mode)
        {
            case WindowManagementMode.Managed:
                if (manage(handle) is { } refusal)
                {
                    // A failed request must not discard a release or a rule's explanation.
                    windows.SetOverride(handle, previous);
                    if (previousExclusion is not null) windows.Exclude(handle, previousExclusion);
                    return refusal;
                }
                break;

            case WindowManagementMode.Unmanaged:
                release(handle);
                break;

            case WindowManagementMode.Default:
                restoreDefault(handle);
                break;
        }

        return null;
    }
}
