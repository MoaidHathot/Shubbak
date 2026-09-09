using System.Globalization;
using Shubbak.Core.Tree;

namespace Shubbak.Core.Wm;

/// <summary>
/// How a monitor is referred to from a command or a config file, when not by a
/// declared name.
/// </summary>
/// <remarks>
/// <para>
/// Three spellings are positional - they describe where Windows put the display in
/// its enumeration rather than which display it is - and are resolved here, against
/// the tree, with no configuration involved: a number (<c>1</c>, counted from zero),
/// the GDI device name (<c>\\.\DISPLAY2</c>), and the tail of that name on its own
/// (<c>DISPLAY2</c>), because nobody types the prefix twice.
/// </para>
/// <para>
/// A display's friendly name is deliberately not one of them. Two of the same model
/// report the same name - this project's own desktop has two <c>DELL U3219Q</c>s -
/// so a command written against the name would pick one of them by accident and
/// look like it worked. A declared <c>monitor</c> that matches on the device path is
/// the way to say which.
/// </para>
/// </remarks>
public static class MonitorReference
{
    /// <summary>Whether the reference is one of the positional spellings.</summary>
    public static bool IsPositional(string reference) =>
        TryIndex(reference, out _) || DeviceNameTail(reference) is not null;

    /// <summary>Reads a plain number, in either the bare or the quoted spelling.</summary>
    public static bool TryIndex(string reference, out int index) =>
        int.TryParse(reference, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out index);

    /// <summary>
    /// The <c>DISPLAYn</c> part of a GDI device name, or null if the reference is not
    /// shaped like one.
    /// </summary>
    public static string? DeviceNameTail(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        string tail = reference.StartsWith(@"\\.\", StringComparison.Ordinal) ? reference[4..] : reference;

        if (tail.Length <= "DISPLAY".Length) return null;
        if (!tail.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)) return null;

        foreach (char c in tail.AsSpan("DISPLAY".Length))
            if (!char.IsAsciiDigit(c)) return null;

        return tail;
    }

    /// <summary>Resolves a positional reference against the attached monitors.</summary>
    /// <returns>The monitor, or null when the reference names none of them.</returns>
    public static MonitorNode? Resolve(RootNode root, string reference)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(reference);

        if (TryIndex(reference, out int index))
            return index >= 0 && index < root.Monitors.Count ? root.Monitors[index] : null;

        if (DeviceNameTail(reference) is { } tail)
        {
            foreach (MonitorNode monitor in root.Monitors)
            {
                string? candidate = DeviceNameTail(monitor.DeviceId);

                if (candidate is not null && string.Equals(candidate, tail, StringComparison.OrdinalIgnoreCase))
                    return monitor;
            }
        }

        return null;
    }
}
