using Shubbak.Config;
using Shubbak.Core.Commands;
using Shubbak.Core.Tree;

namespace Shubbak.Wm.Tests;

public sealed class WindowManagementControllerTests
{
    private static WindowNode Node(nint handle) => new(handle, new WindowIdentity
    {
        Title = "Recap", ProcessName = "ms-teams", ClassName = "TeamsWebView",
    });

    [Fact]
    public void RuleExclusionCanBeOverriddenAndRestoredForOnlyTheSelectedWindow()
    {
        ShubbakConfig config = ConfigLoader.Load("""
            rules { rule "leave Teams alone" { match { process "ms-teams" } do { ignore } } }
            """).Config;
        var rules = new RuleEngine();
        rules.Load(config);
        var attributes = new WindowAttributes("Recap", "TeamsWebView", "ms-teams", null);
        WindowExclusion exclusion = WindowExclusion.ByRule(rules.IgnoringRule(attributes)!);
        var registry = new WindowRegistry();
        registry.Exclude(42, exclusion);
        registry.Exclude(43, exclusion);
        List<nint> touched = [];

        var controller = new WindowManagementController(registry, _ => true,
            handle =>
            {
                touched.Add(handle);
                Assert.Equal(WindowManagementMode.Managed, registry.OverrideFor(handle));
                if (!registry.IsManaged(handle)) registry.Adopt(handle, Node(handle));
                return null;
            },
            handle => registry.Release(handle, forgetOverride: false),
            handle =>
            {
                touched.Add(handle);
                Assert.Equal(WindowManagementMode.Default, registry.OverrideFor(handle));
                registry.Release(handle, WindowExclusion.ByRule(rules.IgnoringRule(attributes)!), forgetOverride: false);
            });

        Assert.Null(controller.Set(42, WindowManagementMode.Managed));
        Assert.True(registry.IsManaged(42));
        Assert.Null(registry.ExclusionFor(42));
        Assert.NotNull(rules.IgnoringRule(attributes));

        // A stale "Manage it" row is an explicit choice, never a toggle back to unmanaged.
        Assert.Null(controller.Set(42, WindowManagementMode.Managed));
        Assert.True(registry.IsManaged(42));

        Assert.Null(controller.Set(42, WindowManagementMode.Default));
        Assert.False(registry.IsManaged(42));
        Assert.Equal("leave Teams alone", registry.ExclusionFor(42)?.Rule?.Name);
        Assert.Equal([42, 42, 42], touched);
        Assert.Same(exclusion, registry.ExclusionFor(43));
        Assert.Single(config.Rules);
    }

    [Fact]
    public void ManualReleaseCanBeResetAndManagedAgainWithoutChangingRules()
    {
        var registry = new WindowRegistry();
        registry.Adopt(42, Node(42));
        var controller = new WindowManagementController(registry, _ => true, _ => null,
            handle => registry.Release(handle, forgetOverride: false),
            handle =>
            {
                Assert.False(registry.AlreadyDecided(handle));
                registry.Adopt(handle, Node(handle));
            });

        Assert.Null(controller.Set(42, WindowManagementMode.Unmanaged));
        Assert.Null(controller.Set(42, WindowManagementMode.Unmanaged));
        registry.ForgetVerdicts();
        Assert.True(registry.AlreadyDecided(42));
        Assert.False(registry.IsManaged(42));

        Assert.Null(controller.Set(42, WindowManagementMode.Default));
        Assert.Equal(WindowManagementMode.Default, registry.OverrideFor(42));
        Assert.True(registry.IsManaged(42));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedAdoptionReturnsTheReasonAndKeepsTheEarlierDecision(bool manual)
    {
        var registry = new WindowRegistry();
        var exclusion = new WindowExclusion("The earlier decision", "earlier");
        if (manual) registry.SetOverride(42, WindowManagementMode.Unmanaged);
        else registry.Exclude(42, exclusion);

        var controller = new WindowManagementController(registry, _ => true,
            _ => "Windows refuses to move this elevated window.",
            _ => Assert.Fail("A failed adoption must not release another window."),
            _ => Assert.Fail("A failed adoption must not restore defaults."));

        Assert.Contains("elevated", controller.Set(42, WindowManagementMode.Managed), StringComparison.Ordinal);
        Assert.Equal(manual ? WindowManagementMode.Unmanaged : WindowManagementMode.Default, registry.OverrideFor(42));
        Assert.Same(manual ? null : exclusion, registry.ExclusionFor(42));
        Assert.True(registry.IsExcluded(42));
    }

    [Theory]
    [InlineData(WindowManagementMode.Managed)]
    [InlineData(WindowManagementMode.Unmanaged)]
    [InlineData(WindowManagementMode.Default)]
    public void AClosedTargetDoesNotActOnSomeOtherWindow(WindowManagementMode mode)
    {
        var registry = new WindowRegistry();
        registry.Adopt(99, Node(99));
        var controller = new WindowManagementController(registry, handle => handle == 99,
            _ => throw new InvalidOperationException("Must not adopt another window."),
            _ => Assert.Fail("Must not release another window."),
            _ => Assert.Fail("Must not reset another window."));

        Assert.Contains("closed", controller.Set(42, mode), StringComparison.Ordinal);
        Assert.True(registry.IsManaged(99));
        Assert.Equal(WindowManagementMode.Default, registry.OverrideFor(42));
    }
}
