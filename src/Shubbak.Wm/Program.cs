using Shubbak.Config;

namespace Shubbak.Wm;

/// <summary>The Shubbak window manager daemon.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
        {
            PrintUsage();
            return 0;
        }

        string? configPath = ResolveConfigPath(args);

        if (args.Contains("--check-config", StringComparer.Ordinal))
            return CheckConfig(configPath);

        using var daemon = new WmDaemon();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            daemon.Stop();
        };

        try
        {
            daemon.Run(configPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fatal: {ex}");
            return 1;
        }
    }

    /// <summary>
    /// Validates config and exits, without touching a single window.
    /// </summary>
    /// <remarks>
    /// Exists so a config can be checked from an editor or a pre-commit hook. Every
    /// diagnostic is rendered with a caret, and the exit code is non-zero only for
    /// errors - warnings are informative, not fatal.
    /// </remarks>
    private static int CheckConfig(string? path)
    {
        if (path is null)
        {
            Console.Error.WriteLine("no config file found");
            return 1;
        }

        ConfigLoadResult result = ConfigLoader.LoadFile(path);
        string source = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        foreach (Diagnostic diagnostic in result.Diagnostics)
            Console.Error.Write(diagnostic.Render(source, path));

        int errors = result.Errors.Count();
        int warnings = result.Warnings.Count();

        Console.WriteLine(
            errors == 0 && warnings == 0
                ? $"{path}: ok - {result.Config.Keybindings.Count} keybindings, " +
                  $"{result.Config.Workspaces.Count} workspaces, {result.Config.Rules.Count} rules"
                : $"{path}: {errors} error(s), {warnings} warning(s)");

        return errors == 0 ? 0 : 1;
    }

    /// <summary>
    /// Finds the config file.
    /// </summary>
    /// <remarks>
    /// Search order: an explicit <c>--config</c>, then <c>SHUBBAK_CONFIG</c>, then
    /// the standard location. The environment variable exists because the author
    /// keeps dotfiles on a separate drive and symlinks them per machine.
    /// </remarks>
    private static string? ResolveConfigPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--config", StringComparison.Ordinal))
                return args[i + 1];

        if (Environment.GetEnvironmentVariable("SHUBBAK_CONFIG") is { Length: > 0 } fromEnvironment)
            return fromEnvironment;

        string standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "shubbak", "shubbak.kdl");

        return File.Exists(standard) ? standard : null;
    }

    private static void PrintUsage() => Console.WriteLine("""
        Shubbak - a tiling window manager for Windows

        USAGE
          shubbak-wm [options]

        OPTIONS
          --config <path>   Config file to load.
                            Defaults to $SHUBBAK_CONFIG, then
                            %USERPROFILE%\.config\shubbak\shubbak.kdl
          --check-config    Validate the config and exit without managing windows.
          --help            Show this message.

        NOTES
          Run elevated to manage windows belonging to elevated processes;
          without it those windows are detected but cannot be moved.
        """);
}
