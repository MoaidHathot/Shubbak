using System.Globalization;
using System.Text;
using Shubbak.Ipc;

namespace Shubbak.Cli;

/// <summary>
/// Printing what each display is, with a definition ready to paste into the config.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <c>inspect</c>'s "write a rule for it". Binding a workspace to a
/// display by name needs a <c>monitor</c> block that matches that display and no other,
/// and the fact that tells two of the same model apart - the connector's device path -
/// is a hundred characters of hexadecimal that nobody should be asked to transcribe.
/// So this writes the block, matching on the shortest part of the path that is
/// distinctive, with the friendly name commented beside it for the reader.
/// </para>
/// <para>
/// Pure, so it can be tested against a made-up desktop. Everything it prints comes
/// from <see cref="MonitorInfoDto"/>, which is what <c>query monitors</c> returns.
/// </para>
/// </remarks>
internal static class MonitorReportText
{
    /// <summary>The whole report: one section per display.</summary>
    public static string Format(IReadOnlyList<MonitorInfoDto> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        if (monitors.Count == 0) return "no displays attached" + Environment.NewLine;

        var text = new StringBuilder();

        for (int index = 0; index < monitors.Count; index++)
        {
            if (index > 0) text.AppendLine();
            Describe(text, monitors[index], index, monitors);
        }

        return text.ToString();
    }

    private static void Describe(StringBuilder text, MonitorInfoDto monitor, int index, IReadOnlyList<MonitorInfoDto> all)
    {
        string kind = monitor.Internal switch
        {
            true => "built-in",
            false => "external",
            null => "unknown kind",
        };

        text.Append(CultureInfo.InvariantCulture, $"{index}  {monitor.DeviceId}");
        if (monitor.Primary) text.Append(", primary");
        text.Append(CultureInfo.InvariantCulture, $"  {monitor.Width}x{monitor.Height} at ({monitor.X},{monitor.Y}), {monitor.Dpi} dpi, {kind}");
        text.AppendLine();

        text.Append("   name:    ").AppendLine(monitor.FriendlyName is { Length: > 0 } name ? name : "(none reported)");
        text.Append("   path:    ").AppendLine(monitor.DevicePath is { Length: > 0 } path ? path : "(none reported)");
        text.Append("   showing: ").AppendLine(monitor.ActiveWorkspace is { Length: > 0 } ws ? ws : "(nothing)");

        text.Append("   called:  ").AppendLine(monitor.Names is { Count: > 0 } names
            ? string.Join(", ", names.Select(n => $"\"{n}\""))
            : "(no declared monitor matches it)");

        text.AppendLine();
        text.Append(Definition(monitor, index, all));
    }

    /// <summary>
    /// A <c>monitor</c> block that matches this display and, as far as can be told from
    /// here, no other attached one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path is preferred because it is what survives a replug. The whole path is
    /// unwieldy, so the block matches on its most distinctive segment: the part between
    /// the last <c>&amp;</c> and the <c>#</c> before the interface GUID, which on a
    /// modern machine reads <c>UID4355</c> and differs between two otherwise identical
    /// panels. Falling back to more of the path only if that segment is not unique
    /// among what is attached.
    /// </para>
    /// <para>
    /// A display with no path - a remote session's - gets <c>internal</c> or the device
    /// name, whichever says something, with a comment that the device name is a
    /// position and will not survive renumbering.
    /// </para>
    /// </remarks>
    public static string Definition(MonitorInfoDto monitor, int index, IReadOnlyList<MonitorInfoDto> all)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(all);

        string suggested = SuggestedName(monitor, index, all);
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"   monitor \"{suggested}\" {{").AppendLine();

        if (monitor.FriendlyName is { Length: > 0 } name)
            text.Append(CultureInfo.InvariantCulture, $"       // {name}").AppendLine();

        if (monitor.DevicePath is { Length: > 0 } path)
        {
            string segment = DistinctiveSegment(path, all.Where(m => !ReferenceEquals(m, monitor)).Select(m => m.DevicePath));
            text.Append(CultureInfo.InvariantCulture, $"       path *= \"{segment}\"").AppendLine();
        }
        else if (monitor.Internal is { } internalKnown)
        {
            text.Append(CultureInfo.InvariantCulture, $"       {(internalKnown ? "internal" : "!internal")}").AppendLine();
        }
        else
        {
            text.Append("       // No device path or connector type was reported, so this matches").AppendLine();
            text.Append("       // the device name - which is a position, and changes on replug.").AppendLine();
            text.Append(CultureInfo.InvariantCulture, $"       device = \"{Escape(monitor.DeviceId)}\"").AppendLine();
        }

        text.Append("   }").AppendLine();
        text.Append(CultureInfo.InvariantCulture, $"   // then: workspace \"...\" monitor=\"{suggested}\"").AppendLine();

        return text.ToString();
    }

    /// <summary>
    /// A name to suggest for the block: the model, lower-cased and dashed, or a position
    /// word - made unique among the attached displays, because two of the same model
    /// would otherwise be handed the same name and the second pasted block would be a
    /// duplicate.
    /// </summary>
    /// <remarks>
    /// Twins are told apart by where they sit: <c>-left</c> and <c>-right</c> when they
    /// differ horizontally, <c>-top</c> and <c>-bottom</c> when only vertically, and a
    /// number when they somehow overlap. Where a person put a monitor is the thing they
    /// already know about it.
    /// </remarks>
    public static string SuggestedName(MonitorInfoDto monitor, int index, IReadOnlyList<MonitorInfoDto> all)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(all);

        string stem = Stem(monitor, index);

        List<MonitorInfoDto> sharing = [];

        for (int i = 0; i < all.Count; i++)
            if (string.Equals(Stem(all[i], i), stem, StringComparison.Ordinal)) sharing.Add(all[i]);

        if (sharing.Count <= 1) return stem;

        bool differentX = sharing.Any(m => m.X != monitor.X);
        bool differentY = sharing.Any(m => m.Y != monitor.Y);

        if (differentX)
        {
            // Leftmost is "left", rightmost is "right"; anything between gets its rank.
            int rank = sharing.OrderBy(m => m.X).ToList().FindIndex(m => ReferenceEquals(m, monitor));
            if (rank == 0) return stem + "-left";
            if (rank == sharing.Count - 1) return stem + "-right";
            return stem + "-" + (rank + 1).ToString(CultureInfo.InvariantCulture);
        }

        if (differentY)
        {
            int rank = sharing.OrderBy(m => m.Y).ToList().FindIndex(m => ReferenceEquals(m, monitor));
            if (rank == 0) return stem + "-top";
            if (rank == sharing.Count - 1) return stem + "-bottom";
            return stem + "-" + (rank + 1).ToString(CultureInfo.InvariantCulture);
        }

        return stem + "-" + (sharing.FindIndex(m => ReferenceEquals(m, monitor)) + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static string Stem(MonitorInfoDto monitor, int index)
    {
        if (monitor.Internal == true) return "laptop";

        if (monitor.FriendlyName is { Length: > 0 } name)
        {
            var slug = new StringBuilder(name.Length);

            foreach (char c in name.ToLowerInvariant())
            {
                if (char.IsAsciiLetterOrDigit(c)) slug.Append(c);
                else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
            }

            string result = slug.ToString().Trim('-');
            if (result.Length > 0) return result;
        }

        return index == 0 ? "main" : $"display-{index}";
    }

    /// <summary>
    /// The shortest tail of a device path that no other attached display's path shares.
    /// </summary>
    /// <remarks>
    /// Paths look like <c>\\?\DISPLAY#DELA124#5&amp;38500b75&amp;0&amp;UID4355#{guid}</c>.
    /// The segment before the GUID, after its last <c>&amp;</c>, is the connector's
    /// unique id and is the usual answer. When two displays share even that - which
    /// should not happen, but the world is wide - the search widens leftwards one
    /// separator at a time until it is unique, and gives up at the whole path.
    /// </remarks>
    public static string DistinctiveSegment(string path, IEnumerable<string?> others)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(others);

        string[] rivals = [.. others.Where(o => o is { Length: > 0 })!];

        // The part before the interface GUID.
        int brace = path.IndexOf("#{", StringComparison.Ordinal);
        string body = brace >= 0 ? path[..brace] : path;

        // Walk separators right to left; each candidate is the tail from that point.
        for (int i = body.Length - 1; i >= 0; i--)
        {
            if (body[i] is not ('&' or '#')) continue;

            string candidate = body[(i + 1)..];

            if (candidate.Length > 0 && IsUnique(candidate, rivals)) return Escape(candidate);
        }

        return Escape(body);
    }

    private static bool IsUnique(string candidate, string[] rivals)
    {
        foreach (string rival in rivals)
            if (rival.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    /// <summary>Backslashes doubled, so the text is a valid KDL string.</summary>
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);
}
