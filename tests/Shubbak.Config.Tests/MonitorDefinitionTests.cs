using Shubbak.Core.Commands;
using Shubbak.Core.Geometry;

namespace Shubbak.Config.Tests;

/// <summary>
/// Naming displays by what they are, and binding workspaces to the names.
/// </summary>
/// <remarks>
/// <para>
/// <c>monitor=1</c> names a position in the enumeration, and Windows reorders the
/// enumeration on replug, on DisplayPort wake and on a driver restart - which is how a
/// workspace bound to "the right-hand screen" ends up on the left one after a dock.
/// A top-level <c>monitor "name" { ... }</c> names the screen instead, by what the
/// display configuration reports about it, and <c>monitor="name"</c> on a workspace
/// refers to it.
/// </para>
/// <para>
/// The loader cannot know what is attached, so what it checks is what it can: that the
/// block is well-formed, that a name a workspace or a command refers to was declared,
/// and that a definition has something to match on. Whether a definition fits a real
/// display is the daemon's to report.
/// </para>
/// </remarks>
public sealed class MonitorDefinitionTests
{
    private static ConfigLoadResult Load(string source) => ConfigLoader.Load(source);

    private static MonitorAttributes Dell(string path = @"\\?\DISPLAY#DELA124#5&38500b75&0&UID4355#{e6f07b5f}") =>
        new(@"\\.\DISPLAY1", "DELL U3219Q", path, IsInternal: false, IsPrimary: true);

    private static MonitorAttributes Laptop() =>
        new(@"\\.\DISPLAY2", null, @"\\?\DISPLAY#SHP1523#4&1c3f2a1&0&UID8388688#{e6f07b5f}", IsInternal: true, IsPrimary: false);

    // ---- the block --------------------------------------------------------------

    [Fact]
    public void ADefinitionReadsTheSameWayAnAppDoes()
    {
        ConfigLoadResult result = Load("""
            monitor "dell-left" {
                name ~= "U3219"
                path *= "UID4355"
            }
            """);

        Assert.Empty(result.Diagnostics);

        MonitorDefinition dell = Assert.Single(result.Config.Monitors.Values);

        Assert.Equal("dell-left", dell.Name);
        Assert.Equal(2, dell.Matchers.Count);
        Assert.Equal(MonitorMatchTarget.FriendlyName, dell.Matchers[0].Target);
        Assert.Equal(MatchOperator.Regex, dell.Matchers[0].Operator);
        Assert.Equal(MonitorMatchTarget.DevicePath, dell.Matchers[1].Target);
        Assert.Equal(MatchOperator.Contains, dell.Matchers[1].Operator);

        Assert.True(dell.Matches(Dell()));
        Assert.False(dell.Matches(Dell(path: @"\\?\DISPLAY#DELA133#5&38500b75&0&UID4357#{e6f07b5f}")));
        Assert.False(dell.Matches(Laptop()));
    }

    [Fact]
    public void TwinsAreToldApartByPath()
    {
        // The case that decided the design: two DELL U3219Qs on one desk, identical in
        // every way the friendly name can express. Only the connector path differs.
        ConfigLoadResult result = Load("""
            monitor "left"  { path *= "UID4355" }
            monitor "right" { path *= "UID4357" }
            """);

        Assert.Empty(result.Diagnostics);

        MonitorAttributes left = Dell();
        MonitorAttributes right = Dell(path: @"\\?\DISPLAY#DELA133#5&38500b75&0&UID4357#{e6f07b5f}");

        Assert.True(result.Config.Monitors["left"].Matches(left));
        Assert.False(result.Config.Monitors["left"].Matches(right));
        Assert.True(result.Config.Monitors["right"].Matches(right));
        Assert.False(result.Config.Monitors["right"].Matches(left));
    }

    [Theory]
    [InlineData("monitor \"laptop\" { internal }", true)]
    [InlineData("monitor \"laptop\" { internal #true }", true)]
    [InlineData("monitor \"laptop\" internal=#true", true)]
    [InlineData("monitor \"laptop\" { !internal }", false)]
    [InlineData("monitor \"laptop\" { internal #false }", false)]
    public void TheInternalFlagIsReadInEverySpelling(string source, bool wanted)
    {
        ConfigLoadResult result = Load(source);

        Assert.Empty(result.Diagnostics);

        MonitorDefinition laptop = result.Config.Monitors["laptop"];

        Assert.Equal(wanted, laptop.Internal);
        Assert.Equal(wanted, laptop.Matches(Laptop()));
        Assert.Equal(!wanted, laptop.Matches(Dell()));
    }

    [Fact]
    public void AnUnknownKindMatchesNeitherWayOnInternal()
    {
        // A remote session's display reports no connector type. A definition that asks
        // "built in?" either way must not claim it, or a workspace would be bound to a
        // screen on the strength of a fact nobody has.
        ConfigLoadResult result = Load("""
            monitor "laptop" { internal }
            monitor "external" { !internal }
            """);

        var unknown = new MonitorAttributes(@"\\.\DISPLAY1", null, null, IsInternal: null, IsPrimary: true);

        Assert.False(result.Config.Monitors["laptop"].Matches(unknown));
        Assert.False(result.Config.Monitors["external"].Matches(unknown));
    }

    [Fact]
    public void PrimaryIsAConditionToo()
    {
        ConfigLoadResult result = Load("""
            monitor "main" { primary }
            """);

        Assert.True(result.Config.Monitors["main"].Matches(Dell()));
        Assert.False(result.Config.Monitors["main"].Matches(Laptop()));
    }

    [Fact]
    public void TheDeviceNameCanBeMatchedForWhatItIsWorth()
    {
        // Positional, and reused by Windows - but sometimes it is all a person has, and
        // refusing it would send them back to indices.
        ConfigLoadResult result = Load("""
            monitor "second" { device = "\\\\.\\DISPLAY2" }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.True(result.Config.Monitors["second"].Matches(Laptop()));
        Assert.False(result.Config.Monitors["second"].Matches(Dell()));
    }

    [Fact]
    public void ADefinitionWithNothingToCheckMatchesNothingAndSaysSo()
    {
        ConfigLoadResult result = Load("""
            monitor "anything" { }
            """);

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0440");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("anything", warning.Message, StringComparison.Ordinal);

        // Nothing rather than everything: a definition that quietly fit every display
        // would also, quietly, take over every workspace bound to it.
        Assert.False(result.Config.Monitors["anything"].Matches(Dell()));
        Assert.False(result.Config.Monitors["anything"].Matches(Laptop()));
    }

    [Fact]
    public void AnUnnamedDefinitionIsAnError()
    {
        ConfigLoadResult result = Load("""
            monitor { path *= "UID4355" }
            """);

        Diagnostic error = Assert.Single(result.Diagnostics, d => d.Code == "SHB0438");

        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Empty(result.Config.Monitors);
    }

    [Fact]
    public void AnUnknownMatcherIsNamedRatherThanDropped()
    {
        ConfigLoadResult result = Load("""
            monitor "dell" { friendly ~= "U3219" }
            """);

        Diagnostic error = Assert.Single(result.Diagnostics, d => d.Code == "SHB0439");

        Assert.Contains("friendly", error.Message, StringComparison.Ordinal);
        Assert.Contains("name, path, or device", error.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateKeepsTheFirstAndSaysSo()
    {
        ConfigLoadResult result = Load("""
            monitor "dell" { path *= "UID4355" }
            monitor "dell" { path *= "UID4357" }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "SHB0441");
        Assert.Equal("UID4355", result.Config.Monitors["dell"].Matchers[0].Pattern);
    }

    [Fact]
    public void ARegexIsValidatedLikeAnyOther()
    {
        ConfigLoadResult result = Load("""
            monitor "dell" { name ~= "[unclosed" }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "SHB0415");
    }

    [Fact]
    public void ADefinitionIsNotAnUnknownSection()
    {
        ConfigLoadResult result = Load("""
            monitor "dell" { path *= "UID4355" }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SHB0427");
    }

    // ---- workspaces bound by name ---------------------------------------------

    [Fact]
    public void AWorkspaceCanBeBoundToADeclaredName()
    {
        ConfigLoadResult result = Load("""
            monitor "dell-right" { path *= "UID4357" }

            workspaces {
                workspace "/" display-name="Second" monitor="dell-right"
            }
            """);

        Assert.Empty(result.Diagnostics);

        WorkspaceConfig second = Assert.Single(result.Config.Workspaces);

        Assert.Equal("dell-right", second.BindToMonitorName);
        Assert.Null(second.BindToMonitor);
    }

    [Fact]
    public void AWorkspaceBoundByPositionStillWorks()
    {
        ConfigLoadResult result = Load("""
            workspaces {
                workspace "/" monitor=1
            }
            """);

        Assert.Empty(result.Diagnostics);

        WorkspaceConfig second = Assert.Single(result.Config.Workspaces);

        Assert.Equal(1, second.BindToMonitor);
        Assert.Null(second.BindToMonitorName);
    }

    [Fact]
    public void AQuotedNumberIsAPositionUnlessAMonitorIsCalledThat()
    {
        // The value type is not a reliable signal of what was meant: workspace names
        // are written both ways in this project's own config.
        ConfigLoadResult positional = Load("""
            workspaces { workspace "/" monitor="1" }
            """);

        Assert.Empty(positional.Diagnostics);
        Assert.Equal(1, positional.Config.Workspaces[0].BindToMonitor);
        Assert.Null(positional.Config.Workspaces[0].BindToMonitorName);

        ConfigLoadResult named = Load("""
            monitor "1" { path *= "UID4357" }
            workspaces { workspace "/" monitor="1" }
            """);

        Assert.Empty(named.Diagnostics);
        Assert.Equal("1", named.Config.Workspaces[0].BindToMonitorName);
        Assert.Null(named.Config.Workspaces[0].BindToMonitor);
    }

    [Fact]
    public void AWorkspaceBoundToAnUndeclaredNameIsAnErrorWithASuggestion()
    {
        ConfigLoadResult result = Load("""
            monitor "dell-right" { path *= "UID4357" }

            workspaces {
                workspace "/" monitor="dell-rigth"
            }
            """);

        Diagnostic error = Assert.Single(result.Diagnostics, d => d.Code == "SHB0442");

        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("dell-rigth", error.Message, StringComparison.Ordinal);
        Assert.Contains("dell-right", error.Hint!, StringComparison.Ordinal);

        // The workspace is still declared - loading is total - just unbound, so it
        // lands on the primary rather than vanishing along with its keybinding.
        WorkspaceConfig second = Assert.Single(result.Config.Workspaces);
        Assert.Null(second.BindToMonitorName);
        Assert.Null(second.BindToMonitor);
    }

    [Fact]
    public void AWorkspaceBoundToANameWhenNoneAreDeclaredExplainsBothRoutes()
    {
        ConfigLoadResult result = Load("""
            workspaces {
                workspace "/" monitor="DISPLAY2-ish"
            }
            """);

        Diagnostic error = Assert.Single(result.Diagnostics, d => d.Code == "SHB0442");

        Assert.Contains("monitor \"DISPLAY2-ish\"", error.Hint!, StringComparison.Ordinal);
        Assert.Contains("monitor=1", error.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceNameOnAWorkspaceIsAcceptedAsAPosition()
    {
        // The spelling that "has never worked" according to the old error. It works
        // now, for what it is worth: the tree resolves it against what is attached.
        ConfigLoadResult result = Load("""
            workspaces {
                workspace "/" monitor="DISPLAY2"
            }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("DISPLAY2", result.Config.Workspaces[0].BindToMonitorName);
    }

    [Fact]
    public void ANegativePositionIsStillRefused()
    {
        ConfigLoadResult result = Load("""
            workspaces { workspace "/" monitor=-1 }
            """);

        Assert.Single(result.Diagnostics, d => d.Code == "SHB0430");
        Assert.Null(result.Config.Workspaces[0].BindToMonitor);
    }

    [Fact]
    public void AMisspeltWorkspaceSettingIsReported()
    {
        // bind-to-monitor is GlazeWM's name for the same thing and the one people
        // reach for by analogy. It was accepted and ignored.
        ConfigLoadResult result = Load("""
            workspaces { workspace "/" bind-to-monitor=1 }
            """);

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0428");

        Assert.Contains("bind-to-monitor", warning.Message, StringComparison.Ordinal);

        // And a near miss gets the suggestion the far one above is too far away for.
        ConfigLoadResult nearMiss = Load("""
            workspaces { workspace "/" montior=1 }
            """);

        Diagnostic hinted = Assert.Single(nearMiss.Diagnostics, d => d.Code == "SHB0428");

        Assert.Contains("monitor", hinted.Hint!, StringComparison.Ordinal);
    }

    // ---- move-workspace --monitor ---------------------------------------------

    [Fact]
    public void MoveWorkspaceParsesAMonitorByName()
    {
        Assert.True(CommandParser.TryParse("move-workspace --monitor dell-right", default, out WmCommand? command, out _));

        var move = Assert.IsType<MoveWorkspaceToMonitorCommand>(command);

        Assert.Equal("dell-right", move.Monitor);
        Assert.Null(move.Direction);
    }

    [Fact]
    public void MoveWorkspaceStillParsesADirection()
    {
        Assert.True(CommandParser.TryParse("move-workspace --direction left", default, out WmCommand? command, out _));

        var move = Assert.IsType<MoveWorkspaceToMonitorCommand>(command);

        Assert.Equal(Direction.Left, move.Direction);
        Assert.Null(move.Monitor);
    }

    [Fact]
    public void MoveWorkspaceRefusesBothAtOnce()
    {
        Assert.False(CommandParser.TryParse(
            "move-workspace --direction left --monitor dell-right", default, out _, out Diagnostic? diagnostic));

        Assert.Equal("SHB0315", diagnostic!.Code);
    }

    [Fact]
    public void MoveWorkspaceWithNeitherNamesBothRoutes()
    {
        Assert.False(CommandParser.TryParse("move-workspace", default, out _, out Diagnostic? diagnostic));

        Assert.Equal("SHB0310", diagnostic!.Code);
        Assert.Contains("--monitor", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void ABindingThatMovesToAnUndeclaredMonitorIsReported()
    {
        ConfigLoadResult result = Load("""
            monitor "dell-right" { path *= "UID4357" }

            keybindings {
                bind "alt+shift+m" { move-workspace --monitor "dell-rigth" }
            }
            """);

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0443");

        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("alt+shift+m", warning.Message, StringComparison.Ordinal);
        Assert.Contains("dell-right", warning.Hint!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("DISPLAY2")]
    [InlineData(@"\\\\.\\DISPLAY2")]
    public void ABindingThatMovesToAPositionIsNotSecondGuessed(string reference)
    {
        // The loader cannot know what is attached, so a position is left for the
        // daemon to check.
        ConfigLoadResult result = Load($$"""
            keybindings {
                bind "alt+shift+m" { move-workspace --monitor "{{reference}}" }
            }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "SHB0443");
    }

    [Fact]
    public void ARuleThatMovesToAnUndeclaredMonitorIsReportedToo()
    {
        ConfigLoadResult result = Load("""
            rules {
                rule "slides go right" {
                    match { process = "POWERPNT" }
                    do { move-workspace --monitor "projector" }
                }
            }
            """);

        Diagnostic warning = Assert.Single(result.Diagnostics, d => d.Code == "SHB0443");

        Assert.Contains("slides go right", warning.Message, StringComparison.Ordinal);
        Assert.Contains("No monitors are declared", warning.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCatalogueKnowsTheMonitorArgument()
    {
        // The palette completes --monitor from this, so a verb spec that did not carry
        // it would leave the flag uncompletable while the parser accepted it.
        Assert.Contains(CommandArgument.MonitorName, Enum.GetValues<CommandArgument>());
        Assert.NotNull(CommandCatalogue.Find("move-workspace"));
    }
}
