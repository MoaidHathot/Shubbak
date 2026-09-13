using System.Text.Json;
using Shubbak.Ipc;

namespace Shubbak.Wm.Tests;

/// <summary>
/// The shape of a window's icon on the pipe.
/// </summary>
/// <remarks>
/// The daemon answers <c>window-icon</c> with this, and the bar - and anything else
/// that asks - reads it back. What these pin is the wire: the field names a client
/// written today will read tomorrow, and that the pixels survive the trip.
/// </remarks>
public sealed class WindowIconWireTests
{
    private static readonly byte[] Pixels = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 0, 0];

    [Fact]
    public void ItRoundTripsThroughTheSourceGeneratedContext()
    {
        var sent = new WindowIcon(0x1234, 2, 2, Convert.ToBase64String(Pixels), "window");

        string json = JsonSerializer.Serialize(sent, IpcJsonContext.Default.WindowIcon);
        WindowIcon? received = JsonSerializer.Deserialize(json, IpcJsonContext.Default.WindowIcon);

        Assert.Equal(sent, received);
        Assert.Equal(Pixels, received!.Decode());
    }

    [Fact]
    public void TheFieldNamesAreSnakeCaseLikeTheRestOfTheProtocol()
    {
        string json = JsonSerializer.Serialize(
            new WindowIcon(1, 2, 2, Convert.ToBase64String(Pixels), "class"), IpcJsonContext.Default.WindowIcon);

        Assert.Contains("\"handle\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"width\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"height\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"pixels\":\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"class\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ABitmapThatDoesNotMatchItsSizeDecodesToNothing()
    {
        // The daemon will never send one, but a client must not trust the pipe with
        // its array bounds.
        Assert.Null(new WindowIcon(1, 4, 4, Convert.ToBase64String(Pixels), "window").Decode());
        Assert.Null(new WindowIcon(1, 2, 2, "not base64!", "window").Decode());
        Assert.Null(new WindowIcon(1, 0, 0, string.Empty, "window").Decode());
    }

    [Fact]
    public void TheMethodIsNamedWhereClientsCanFindIt() =>
        Assert.Equal("window-icon", WindowIcon.Method);
}
