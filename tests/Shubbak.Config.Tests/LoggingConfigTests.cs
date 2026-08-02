using Shubbak.Core.Diagnostics;

namespace Shubbak.Config.Tests;

/// <summary>
/// Tests for the <c>logging</c> section.
/// </summary>
/// <remarks>
/// Both the window manager and the bar read this, and a bar with no logging cannot
/// answer any question about itself - which is exactly what happened: instrumentation
/// added to diagnose a bar problem produced nothing, because the bar had never been
/// told to write anything down.
/// </remarks>
public sealed class LoggingConfigTests
{
    private static ShubbakConfig LoadOk(string source)
    {
        ConfigLoadResult result = ConfigLoader.Load(source);

        Assert.False(
            result.HasErrors,
            "Unexpected errors:\n" + string.Join("\n", result.Errors.Select(d => d.ToString())));

        return result.Config;
    }

    [Fact]
    public void NoLoggingSectionMeansNoFile()
    {
        Assert.Null(LoadOk("general { }").LogFile);
    }

    [Fact]
    public void AnEmptyPathResolvesToTheStandardLocation()
    {
        // Writing file "" is the documented way of saying "somewhere sensible", and
        // it is resolved here rather than left for each process to interpret - one of
        // which opened a file literally named "".
        ShubbakConfig config = LoadOk("""
            logging {
                level "debug"
                file ""
            }
            """);

        Assert.Equal(Log.DefaultLogPath, config.LogFile);
        Assert.Equal(LogLevel.Debug, config.LogLevel);
    }

    [Fact]
    public void AnExplicitPathIsKept()
    {
        ShubbakConfig config = LoadOk("""
            logging {
                file "C:\\logs\\shubbak.log"
            }
            """);

        Assert.Equal(@"C:\logs\shubbak.log", config.LogFile);
    }

    [Fact]
    public void TheLevelIsReadWithoutAFile()
    {
        ShubbakConfig config = LoadOk("""
            logging {
                level "trace"
            }
            """);

        Assert.Equal(LogLevel.Trace, config.LogLevel);
        Assert.Null(config.LogFile);
    }
}
