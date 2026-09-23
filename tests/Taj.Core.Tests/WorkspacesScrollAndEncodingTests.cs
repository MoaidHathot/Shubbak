using Shubbak.Config;
using Shubbak.Ui.Layout;
using Taj.Core.Widgets;

namespace Taj.Core.Tests;

/// <summary>
/// The workspace strip under the wheel, and the wire format it reads.
/// </summary>
/// <remarks>
/// <para>
/// Turning the wheel over the strip moves to the neighbouring workspace - the gesture
/// every other bar has and this one lacked. It is the strip's own scroll rather than
/// a pill's: the commands are computed from the list, name the neighbours of the
/// active workspace, wrap at the ends, and are quoted the way the click commands are.
/// </para>
/// <para>
/// The format the widget reads is a string, since every source carries strings. Its
/// separators are ordinary characters, so a name containing one has to be escaped or
/// the record it sits in falls apart - and, worse, so does every record after it.
/// </para>
/// </remarks>
public sealed class WorkspacesScrollAndEncodingTests
{
    private static VisualNode Build(WorkspacesWidget widget, params ReadOnlySpan<WorkspacesWidget.WorkspaceEntry> entries) =>
        widget.Build(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["workspaces"] = WorkspacesWidget.Encode(entries.ToArray()),
        });

    [Fact]
    public void TheWheelNamesTheNeighboursOfTheActiveWorkspace()
    {
        VisualNode node = Build(new WorkspacesWidget("workspaces"),
            new("1", "1", Active: false, HasWindows: true),
            new("2", "2", Active: true, HasWindows: true),
            new("3", "3", Active: false, HasWindows: true));

        Assert.Equal("focus --workspace 1", node.OnScrollUp);
        Assert.Equal("focus --workspace 3", node.OnScrollDown);
    }

    [Fact]
    public void TheWheelWrapsAtEitherEnd()
    {
        VisualNode first = Build(new WorkspacesWidget("workspaces"),
            new("a", "a", Active: true, HasWindows: true),
            new("b", "b", Active: false, HasWindows: true),
            new("c", "c", Active: false, HasWindows: true));

        Assert.Equal("focus --workspace c", first.OnScrollUp);
        Assert.Equal("focus --workspace b", first.OnScrollDown);

        VisualNode last = Build(new WorkspacesWidget("workspaces"),
            new("a", "a", Active: false, HasWindows: true),
            new("b", "b", Active: false, HasWindows: true),
            new("c", "c", Active: true, HasWindows: true));

        Assert.Equal("focus --workspace b", last.OnScrollUp);
        Assert.Equal("focus --workspace a", last.OnScrollDown);
    }

    [Fact]
    public void TheWheelSeesEveryWorkspaceEvenWhenEmptyOnesAreHidden()
    {
        // hide-empty is about what is drawn. Scrolling past an empty workspace as if
        // it were not there would make the wheel and the keyboard disagree about
        // what "next" means.
        VisualNode node = Build(new WorkspacesWidget("workspaces") { HideEmpty = true },
            new("1", "1", Active: true, HasWindows: true),
            new("2", "2", Active: false, HasWindows: false),
            new("3", "3", Active: false, HasWindows: true));

        Assert.Equal(2, node.Children.Count);
        Assert.Equal("focus --workspace 2", node.OnScrollDown);
    }

    [Fact]
    public void TheNeighboursAreQuotedLikeTheClicks()
    {
        VisualNode node = Build(new WorkspacesWidget("workspaces"),
            new("Second Monitor", "Two", Active: false, HasWindows: true),
            new("'", "AI", Active: true, HasWindows: true));

        Assert.Equal("focus --workspace \"Second Monitor\"", node.OnScrollUp);
        Assert.Equal("focus --workspace \"Second Monitor\"", node.OnScrollDown);
    }

    [Fact]
    public void OneWorkspaceHasNoNeighbours()
    {
        VisualNode node = Build(new WorkspacesWidget("workspaces"),
            new WorkspacesWidget.WorkspaceEntry("only", "only", Active: true, HasWindows: true));

        Assert.Null(node.OnScrollUp);
        Assert.Null(node.OnScrollDown);
    }

    [Fact]
    public void WithoutAnActiveWorkspaceTheWheelDoesNothing()
    {
        // The snapshot before the first report, or a monitor that has no workspace
        // yet: there is no "next" to name.
        VisualNode node = Build(new WorkspacesWidget("workspaces"),
            new("1", "1", Active: false, HasWindows: true),
            new("2", "2", Active: false, HasWindows: true));

        Assert.Null(node.OnScrollUp);
        Assert.Null(node.OnScrollDown);
    }

    [Fact]
    public void ScrollingCanBeTurnedOff()
    {
        VisualNode node = Build(new WorkspacesWidget("workspaces") { Scrolls = false },
            new("1", "1", Active: true, HasWindows: true),
            new("2", "2", Active: false, HasWindows: true));

        Assert.Null(node.OnScrollUp);
        Assert.Null(node.OnScrollDown);
    }

    [Fact]
    public void TheLoaderReadsScrollOnTheWorkspacesNode()
    {
        (TajConfig config, IReadOnlyList<Diagnostic> diagnostics) = TajConfigLoader.Load("""
            bar {
                profile "default" {
                    zone "left" { workspaces id="ws" scroll=#false }
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "TAJ0016");

        var widget = Assert.IsType<WorkspacesWidget>(config.Default.Zones[0].Widgets[0]);

        Assert.False(widget.Scrolls);
        Assert.True(new WorkspacesWidget("x").Scrolls);
    }

    [Fact]
    public void ANameWithASeparatorInItSurvivesTheWire()
    {
        // `web | mail` split into two half-records that were each too short to decode,
        // so the widget showed nothing for it and the wrong things for the rest.
        WorkspacesWidget.WorkspaceEntry[] entries =
        [
            new("web | mail", "web | mail", Active: true, HasWindows: true),
            new("tab\there", "T", Active: false, HasWindows: false),
            new(@"C:\", "Drive", Active: false, HasWindows: true, Focused: true),
            new(@"back\|slash", @"a\tb", Active: false, HasWindows: false),
        ];

        WorkspacesWidget.WorkspaceEntry[] decoded = [.. WorkspacesWidget.Decode(WorkspacesWidget.Encode(entries))];

        Assert.Equal(entries, decoded);
    }

    [Fact]
    public void PlainNamesAreEncodedAsTheyWere()
    {
        // The escaping is only for the names that need it; everything else on the
        // wire reads as it always did, so a log line showing the value stays legible.
        string encoded = WorkspacesWidget.Encode(
        [
            new("1", "Firefox", Active: true, HasWindows: true),
            new("2", "2", Active: false, HasWindows: false),
        ]);

        Assert.Equal("1|Firefox|1|1|0\t2|2|0|0|0", encoded);
    }

    [Fact]
    public void ARecordWithTooFewFieldsIsSkippedNotFatal()
    {
        WorkspacesWidget.WorkspaceEntry[] decoded =
            [.. WorkspacesWidget.Decode("broken\tok|OK|1|1|0\t\ta|b|1")];

        WorkspacesWidget.WorkspaceEntry entry = Assert.Single(decoded);

        Assert.Equal("ok", entry.Name);
        Assert.True(entry.Active);
    }
}
