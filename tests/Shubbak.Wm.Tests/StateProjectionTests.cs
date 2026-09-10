using System.Text.Json;
using Shubbak.Core.Geometry;
using Shubbak.Core.Tree;
using Shubbak.Core.Wm;
using Shubbak.Ipc;

namespace Shubbak.Wm.Tests;

/// <summary>
/// What the window manager says about itself over the pipe, for the facts that
/// used to stay inside the process.
/// </summary>
/// <remarks>
/// <para>
/// Three things the daemon knew and told nobody: that an application had taken its
/// own window full-screen, that the session had become remote or the shell had
/// declared a presentation, and that a bound chord had fired. Each is now an event
/// and, where it is state rather than an occurrence, a field on the snapshot.
/// </para>
/// <para>
/// The shape is what these pin. A client written against the payload today must read
/// the same JSON tomorrow, and an older client must be able to ignore the additions -
/// which is what keeps the protocol version where it is.
/// </para>
/// </remarks>
public sealed class StateProjectionTests
{
    private static WindowManager WithOneWorkspace(out MonitorNode monitor)
    {
        var wm = new WindowManager();

        var bounds = new Rect(0, 0, 1920, 1080);
        monitor = new MonitorNode("\\\\.\\DISPLAY1", bounds, bounds, 96);
        wm.AddMonitor(monitor);
        wm.AddWorkspace(new WorkspaceNode("1"), monitor);
        wm.ActivateWorkspace(monitor.Workspaces[0]);

        return wm;
    }

    private static WindowNode Managed(WindowManager wm, nint handle, string title = "player")
    {
        var node = new WindowNode(handle, new WindowIdentity
        {
            Title = title, ProcessName = "test", ClassName = "TestClass",
        });

        wm.ManageWindow(node);
        return node;
    }

    // ---- native full-screen ---------------------------------------------------

    [Fact]
    public void AWindowDescriptionCarriesWhetherItTookItselfFullScreen()
    {
        WindowManager wm = WithOneWorkspace(out _);
        WindowNode window = Managed(wm, 0x100);

        Assert.False(StateProjection.Describe(window, null).NativeFullscreen);

        window.IsNativeFullscreen = true;

        WindowInfo described = StateProjection.Describe(window, null);

        Assert.True(described.NativeFullscreen);

        // Not a state. The window is still tiled in the tree and goes back to its tile
        // the moment the application lets go; a client showing "fullscreen" for it
        // would be offering a toggle that cannot be toggled.
        Assert.Equal("tiling", described.State);
    }

    [Fact]
    public void TheFullScreenAnnouncementIsTheWindowWithTheFlagOnIt()
    {
        WindowManager wm = WithOneWorkspace(out _);
        WindowNode window = Managed(wm, 0x100);
        window.IsNativeFullscreen = true;

        string payload = StateProjection.Payload(new WindowNativeFullscreenChanged(window, true), wm);

        WindowInfo? read = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.WindowInfo);

        Assert.NotNull(read);
        Assert.Equal(0x100, read.Handle);
        Assert.True(read.NativeFullscreen);
        Assert.Contains("\"native_fullscreen\":true", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowDescriptionWrittenBeforeTheFlagExistedStillReads()
    {
        // The protocol version did not move, so this is the promise being kept: a
        // payload from a daemon that has never heard of the flag is read as "not
        // full-screen" rather than refused.
        const string older =
            "{\"id\":1,\"handle\":256,\"title\":\"t\",\"class_name\":\"C\",\"process_name\":\"p\"," +
            "\"state\":\"tiling\",\"focused\":false,\"x\":0,\"y\":0,\"width\":10,\"height\":10}";

        WindowInfo? read = JsonSerializer.Deserialize(older, IpcJsonContext.Default.WindowInfo);

        Assert.NotNull(read);
        Assert.False(read.NativeFullscreen);
    }

    // ---- the session --------------------------------------------------------

    [Fact]
    public void TheEnvironmentAnnouncementNamesBothFacts()
    {
        WindowManager wm = WithOneWorkspace(out _);

        string payload = StateProjection.Payload(
            new EnvironmentChanged(RemoteSession: true, UserActivity.Presenting), wm);

        Assert.Equal("{\"remote_session\":true,\"activity\":\"presenting\"}", payload);
    }

    [Fact]
    public void TheSnapshotCarriesTheSessionWhenTheDaemonHasReadIt()
    {
        WindowManager wm = WithOneWorkspace(out _);

        StateSnapshot snapshot = StateProjection.Snapshot(
            wm, suspended: false, new StateProjection.SessionInfo(true, UserActivity.FullScreenApp));

        Assert.True(snapshot.RemoteSession);
        Assert.Equal("fullscreen-app", snapshot.Activity);

        // The same word the event uses, so a client reads one vocabulary in both places.
        Assert.Equal(
            snapshot.Activity,
            UserActivity.FullScreenApp.Wire());
    }

    [Fact]
    public void TheSnapshotSaysNotAskedYetRatherThanInventingAnAnswer()
    {
        WindowManager wm = WithOneWorkspace(out _);

        StateSnapshot beforeAnyRead = StateProjection.Snapshot(
            wm, suspended: false, new StateProjection.SessionInfo(false, null));

        Assert.Null(beforeAnyRead.Activity);

        // And a caller that passes nothing - the tests that predate the field - gets
        // the same absence, not "ordinary".
        Assert.Null(StateProjection.Snapshot(wm).Activity);
        Assert.False(StateProjection.Snapshot(wm).RemoteSession);

        // On the wire, absent means absent: the field is not written, so an older
        // client sees exactly the JSON it saw before.
        string json = JsonSerializer.Serialize(beforeAnyRead, IpcJsonContext.Default.StateSnapshot);

        Assert.DoesNotContain("activity", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ASnapshotWrittenBeforeTheSessionFieldsExistedStillReads()
    {
        const string older =
            "{\"monitors\":[],\"workspaces\":[],\"windows\":[],\"binding_mode\":null,\"paused\":false}";

        StateSnapshot? read = JsonSerializer.Deserialize(older, IpcJsonContext.Default.StateSnapshot);

        Assert.NotNull(read);
        Assert.False(read.Suspended);
        Assert.False(read.RemoteSession);
        Assert.Null(read.Activity);
    }

    [Theory]
    [InlineData(UserActivity.Unknown, "unknown")]
    [InlineData(UserActivity.Ordinary, "ordinary")]
    [InlineData(UserActivity.FullScreenGame, "fullscreen-game")]
    [InlineData(UserActivity.FullScreenApp, "fullscreen-app")]
    [InlineData(UserActivity.Presenting, "presenting")]
    [InlineData(UserActivity.QuietTime, "quiet-time")]
    [InlineData(UserActivity.Away, "away")]
    public void EveryActivityHasAStableWireNameThatReadsBack(UserActivity activity, string wire)
    {
        // Spelt out rather than derived, so renaming a member cannot change what a
        // subscriber reads. The round trip is what a config file will lean on later.
        Assert.Equal(wire, activity.Wire());
        Assert.Equal(activity, UserActivityNames.Parse(wire));
    }

    [Fact]
    public void EveryActivityIsNamed()
    {
        // A member added to the enum without a wire name would read as "unknown" on
        // the wire, which is a different claim from the one being made.
        foreach (UserActivity activity in Enum.GetValues<UserActivity>())
        {
            if (activity == UserActivity.Unknown) continue;

            Assert.NotEqual("unknown", activity.Wire());
        }

        Assert.Null(UserActivityNames.Parse("presentation"));
        Assert.Null(UserActivityNames.Parse(null));
    }

    // ---- monitors -------------------------------------------------------------

    [Fact]
    public void AMonitorDescriptionCarriesWhatTheDisplayIsWhenKnown()
    {
        WithOneWorkspace(out MonitorNode monitor);

        // Before the platform layer has said anything: absent, not invented.
        MonitorInfoDto before = StateProjection.Describe(monitor);

        Assert.Null(before.FriendlyName);
        Assert.Null(before.DevicePath);
        Assert.Null(before.Internal);

        string json = JsonSerializer.Serialize(before, IpcJsonContext.Default.MonitorInfoDto);

        Assert.DoesNotContain("friendly_name", json, StringComparison.Ordinal);
        Assert.DoesNotContain("internal", json, StringComparison.Ordinal);

        monitor.FriendlyName = "DELL U3219Q";
        monitor.DevicePath = @"\\?\DISPLAY#DELA124#5&38500b75&0&UID4355#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        monitor.IsInternal = false;

        MonitorInfoDto after = StateProjection.Describe(monitor);

        Assert.Equal("DELL U3219Q", after.FriendlyName);
        Assert.StartsWith(@"\\?\DISPLAY#DELA124", after.DevicePath, StringComparison.Ordinal);
        Assert.False(after.Internal);

        // The GDI name is still the key clients already use; nothing moved.
        Assert.Equal(@"\\.\DISPLAY1", after.DeviceId);
    }

    [Fact]
    public void AMonitorDescriptionWrittenBeforeTheIdentityFieldsExistedStillReads()
    {
        const string older =
            "{\"id\":1,\"device_id\":\"\\\\\\\\.\\\\DISPLAY1\",\"primary\":true,\"dpi\":96," +
            "\"x\":0,\"y\":0,\"width\":1920,\"height\":1080,\"active_workspace\":\"1\"}";

        MonitorInfoDto? read = JsonSerializer.Deserialize(older, IpcJsonContext.Default.MonitorInfoDto);

        Assert.NotNull(read);
        Assert.Null(read.FriendlyName);
        Assert.Null(read.DevicePath);
        Assert.Null(read.Internal);
    }

    // ---- contexts -------------------------------------------------------------

    [Fact]
    public void AContextAnnouncementCarriesNameStateSourceAndReason()
    {
        WindowManager wm = WithOneWorkspace(out _);

        string payload = StateProjection.Payload(
            new ContextChanged("presenting", true, "detected", "window app=\"slides\""), wm);

        Assert.Equal(
            "{\"name\":\"presenting\",\"active\":true,\"source\":\"detected\",\"reason\":\"window app=\\u0022slides\\u0022\"}",
            payload);
        Assert.Equal("context.changed", new ContextChanged("x", false, "pinned", "r").Topic);
        Assert.Contains("context.changed", IpcProtocol.Topics);
    }

    [Fact]
    public void ARestoredArrangementIsAnnouncedWithItsAccount()
    {
        WindowManager wm = WithOneWorkspace(out _);

        string payload = StateProjection.Payload(new ArrangementRestored("demo", "1", 3, 1, 2), wm);

        Assert.Equal(
            "{\"name\":\"demo\",\"workspace\":\"1\",\"placed\":3,\"missing\":1,\"kept\":2}",
            payload);
        Assert.Equal("arrangement.restored", new ArrangementRestored("d", "1", 0, 0, 0).Topic);
        Assert.Contains("arrangement.restored", IpcProtocol.Topics);
    }

    [Fact]
    public void TheSnapshotCarriesTheActiveContextsWhenGivenThem()
    {
        WindowManager wm = WithOneWorkspace(out _);

        StateSnapshot snapshot = StateProjection.Snapshot(wm, contexts: ["presenting", "docked"]);
        Assert.Equal(["presenting", "docked"], snapshot.Contexts);

        // Not given: absent on the wire, as every appended field is.
        string json = JsonSerializer.Serialize(StateProjection.Snapshot(wm), IpcJsonContext.Default.StateSnapshot);
        Assert.DoesNotContain("contexts", json, StringComparison.Ordinal);

        // Given and empty: present and empty, so a client can tell "none hold" from
        // "this daemon predates contexts".
        string none = JsonSerializer.Serialize(StateProjection.Snapshot(wm, contexts: []), IpcJsonContext.Default.StateSnapshot);
        Assert.Contains("\"contexts\":[]", none, StringComparison.Ordinal);
    }

    // ---- bindings -------------------------------------------------------------

    [Fact]
    public void AFiredBindingReadsLikeAnEntryOfQueryBindings()
    {
        WindowManager wm = WithOneWorkspace(out _);

        string payload = StateProjection.Payload(
            new BindingFired("alt+shift+h", "resize", ["resize", "equalise"]), wm);

        // Read back with the type query bindings already uses, which is the point of
        // matching its shape. Compared as values rather than as text, because the
        // serialiser is free to escape a plus sign and does.
        BindingInfo? read = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.BindingInfo);

        Assert.NotNull(read);
        Assert.Equal("alt+shift+h", read.Key);
        Assert.Equal("resize", read.Mode);
        Assert.Equal(["resize", "equalise"], read.Commands);
    }

    [Fact]
    public void AFiredBindingInTheDefaultTableSaysSo()
    {
        WindowManager wm = WithOneWorkspace(out _);

        string payload = StateProjection.Payload(new BindingFired("alt+h", null, ["focus"]), wm);

        // Null, written out. A client distinguishing "the default table" from "a mode"
        // needs the key to be present with a null in it, not absent.
        Assert.Contains("\"mode\":null", payload, StringComparison.Ordinal);

        BindingInfo? read = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.BindingInfo);

        Assert.NotNull(read);
        Assert.Equal("alt+h", read.Key);
        Assert.Null(read.Mode);
        Assert.Equal(["focus"], read.Commands);
    }

    [Fact]
    public void AFiredBindingEscapesWhatItCarries()
    {
        // The key display is user-shaped text from the config. A quote in it must not
        // break the JSON.
        WindowManager wm = WithOneWorkspace(out _);

        string payload = StateProjection.Payload(new BindingFired("alt+\"", null, []), wm);

        BindingInfo? read = JsonSerializer.Deserialize(payload, IpcJsonContext.Default.BindingInfo);

        Assert.NotNull(read);
        Assert.Equal("alt+\"", read.Key);
        Assert.Empty(read.Commands);
    }

    // ---- topics ---------------------------------------------------------------

    [Fact]
    public void TheNewTopicsSitInTheNamespacesTheirSubjectsBelongTo()
    {
        // window.* is about a window, wm.* is about the window manager itself, and the
        // binding topics share a prefix so a client can take both or neither.
        WindowManager wm = WithOneWorkspace(out _);
        WindowNode window = Managed(wm, 0x100);

        Assert.Equal("window.native_fullscreen", new WindowNativeFullscreenChanged(window, true).Topic);
        Assert.Equal("wm.environment", new EnvironmentChanged(false, UserActivity.Ordinary).Topic);
        Assert.Equal("binding.fired", new BindingFired("alt+h", null, ["focus"]).Topic);

        Assert.Contains("window.native_fullscreen", IpcProtocol.Topics);
        Assert.Contains("wm.environment", IpcProtocol.Topics);
        Assert.Contains("binding.fired", IpcProtocol.Topics);
    }
}
