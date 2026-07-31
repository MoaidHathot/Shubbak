namespace Shubbak.Spike;

internal static class ArgUtil
{
    public static bool HasFlag(string[] args, string name) =>
        Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    public static string? GetString(string[] args, string name, string? fallback = null)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return fallback;
    }

    public static int GetInt(string[] args, string name, int fallback)
        => int.TryParse(GetString(args, name), out int v) ? v : fallback;

    public static double GetDouble(string[] args, string name, double fallback)
        => double.TryParse(GetString(args, name), out double v) ? v : fallback;
}
