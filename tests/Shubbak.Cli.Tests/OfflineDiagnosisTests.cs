namespace Shubbak.Cli.Tests;

/// <summary>
/// <c>shubbak diagnose</c> with no window manager to ask: the report from what is on
/// disk.
/// </summary>
/// <remarks>
/// A report is most wanted when the window manager has died, and that was the one
/// case it refused. What is on disk is what a crash report is for: whether the config
/// parses and what every loader says, the tails of the logs, the crash report the
/// process wrote on its way out, the session it last saved. Tested against a scratch
/// state directory so the machine's own logs and crashes never enter into it.
/// </remarks>
public sealed class OfflineDiagnosisTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), $"shubbak-offline-{Guid.NewGuid():N}");
    private readonly string _config;

    public OfflineDiagnosisTests()
    {
        Directory.CreateDirectory(_state);
        _config = Path.Combine(_state, "shubbak.kdl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_state, recursive: true); }
        catch (IOException) { }
    }

    private string Report(string? config = null, DateTime? now = null) =>
        OfflineDiagnosis.Build("test", config, _state, now);

    [Fact]
    public void TheReportSaysAtTheTopThatItWasMadeWithoutAWindowManager()
    {
        string report = Report();

        Assert.StartsWith("# Shubbak diagnostic report", report, StringComparison.Ordinal);
        Assert.Contains("## Made without a window manager", report, StringComparison.Ordinal);
        Assert.Contains("No window manager was running when this report was made", report, StringComparison.Ordinal);
        Assert.Contains("shubbak diagnose -o report.md", report, StringComparison.Ordinal);

        // And still carries the environment the live report carries.
        Assert.Contains("## Environment", report, StringComparison.Ordinal);
        Assert.Contains("**Executable**", report, StringComparison.Ordinal);
        Assert.Contains("## Installed binaries", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConfigurationIsReadByEveryLoaderAndShownWithItsDiagnostics()
    {
        File.WriteAllText(_config, """
            general { default-layout "splith" }
            keybindings { bind "alt+h" { focus --direction left } }
            bar { source "x" kind="cmmand" command="a.exe"; profile "default" { height 30 } }
            dalil { widht 700 }
            ayn { cmaera #false }
            """);

        string report = Report(_config);

        Assert.Contains("## Configuration", report, StringComparison.Ordinal);
        Assert.Contains($"**Path**: {_config}", report, StringComparison.Ordinal);
        Assert.Contains("1 keybindings", report, StringComparison.Ordinal);

        // One warning per loader, each rendered with the caret the loaders draw.
        Assert.Contains("## Configuration diagnostics", report, StringComparison.Ordinal);
        Assert.Contains("TAJ0027", report, StringComparison.Ordinal);
        Assert.Contains("DAL0001", report, StringComparison.Ordinal);
        Assert.Contains("AYN0001", report, StringComparison.Ordinal);

        // And the file itself, so the line a caret points at can be read.
        Assert.Contains("## Configuration file", report, StringComparison.Ordinal);
        Assert.Contains("kind=\"cmmand\"", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingConfigurationIsSaidRatherThanThrown()
    {
        string report = Report(Path.Combine(_state, "no-such.kdl"));

        Assert.Contains("No configuration file was found", report, StringComparison.Ordinal);
        Assert.Contains("shubbak setup", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewestCrashReportOfTheWeekIsIncludedWholeAndOlderOnesAreNot()
    {
        DateTime now = new(2026, 9, 26, 12, 0, 0);

        string old = Path.Combine(_state, "crash-20260901-120000.md");
        string recent = Path.Combine(_state, "crash-20260925-090000.md");
        string newest = Path.Combine(_state, "crash-20260926-080000.md");

        File.WriteAllText(old, "# old crash");
        File.WriteAllText(recent, "# recent crash");
        File.WriteAllText(newest, "# the newest crash\nwith the stack in it");

        File.SetLastWriteTime(old, now.AddDays(-20));
        File.SetLastWriteTime(recent, now.AddDays(-1));
        File.SetLastWriteTime(newest, now.AddHours(-4));

        string report = Report(now: now);

        Assert.Contains("## Crash reports (2 in the last 7 days)", report, StringComparison.Ordinal);
        Assert.Contains("crash-20260926-080000.md", report, StringComparison.Ordinal);
        Assert.Contains("crash-20260925-090000.md", report, StringComparison.Ordinal);
        Assert.DoesNotContain("crash-20260901-120000.md", report, StringComparison.Ordinal);

        Assert.Contains("## Newest crash report: crash-20260926-080000.md", report, StringComparison.Ordinal);
        Assert.Contains("with the stack in it", report, StringComparison.Ordinal);
        Assert.DoesNotContain("# recent crash", report, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCrashReportsIsSaidSo()
    {
        Assert.Contains("None from the last 7 days", Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogsTailsAreCarriedWithTheirRotations()
    {
        // 500 lines: only the last 400 are worth carrying, and the count says so.
        File.WriteAllLines(Path.Combine(_state, "shubbak.log"), Enumerable.Range(1, 500).Select(i => $"line {i}"));
        File.WriteAllText(Path.Combine(_state, "shubbak.log.1"), "the run before\nended here");
        File.WriteAllText(Path.Combine(_state, "taj.log"), "bar says hello");

        string report = Report();

        Assert.Contains("## Log: shubbak-wm (last 400 of 500 lines", report, StringComparison.Ordinal);
        Assert.Contains("line 500", report, StringComparison.Ordinal);
        Assert.Contains("line 101", report, StringComparison.Ordinal);
        Assert.DoesNotContain("line 100\n", report, StringComparison.Ordinal);

        Assert.Contains("## Log: shubbak-wm.1 (last 2 of 2 lines", report, StringComparison.Ordinal);
        Assert.Contains("ended here", report, StringComparison.Ordinal);

        Assert.Contains("## Log: taj (last 1 of 1 lines", report, StringComparison.Ordinal);
        Assert.DoesNotContain("## Log: dalil", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogAnotherProcessIsStillWritingIsReadAllTheSame()
    {
        // The bar, the palette and the watcher hold their logs open for writing for as
        // long as they run - which is exactly when a report about the window manager
        // dying is written. The first live report came back with two logs marked
        // unreadable for want of this.
        string path = Path.Combine(_state, "dalil.log");

        using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            writer.Write("held open by the palette\n"u8);
            writer.Flush();

            string report = Report();

            Assert.Contains("## Log: dalil (last 1 of 1 lines", report, StringComparison.Ordinal);
            Assert.Contains("held open by the palette", report, StringComparison.Ordinal);
            Assert.DoesNotContain("unreadable", report, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSessionFileIsIncludedWhenThereIsOne()
    {
        Assert.Contains("(no session file", Report(), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(_state, "session.json"), "{\"version\":3}");

        string report = Report();

        Assert.Contains("## Session (saved ", report, StringComparison.Ordinal);
        Assert.Contains("{\"version\":3}", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AStateDirectoryThatDoesNotExistIsNotAnError()
    {
        string report = OfflineDiagnosis.Build("test", null, Path.Combine(_state, "never-made"));

        Assert.Contains("has never run as this user", report, StringComparison.Ordinal);
        Assert.Contains("## Reproducing", report, StringComparison.Ordinal);
    }
}
