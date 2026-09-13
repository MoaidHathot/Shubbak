using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native.Tests;

/// <summary>
/// Reading a window's icon as pixels, against a real window given a known icon.
/// </summary>
/// <remarks>
/// Whether an icon handle unpacks into the bytes it was made from is a question only
/// Windows can answer, so the icon is built here from four pixels whose values are
/// known, set on a throwaway window, and read back through the same path the daemon
/// uses for the pipe.
/// </remarks>
public sealed class WindowIconReaderTests
{
    /// <summary>
    /// A 2x2 icon with an alpha channel: opaque red, opaque green, half-transparent
    /// blue, and a transparent pixel. Set on the window with <c>WM_SETICON</c>.
    /// </summary>
    private static unsafe HICON MakeIcon()
    {
        // Straight (not premultiplied) BGRA, the way an icon resource carries it.
        byte[] colour =
        [
            0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 128,
            0, 0, 0, 0,
        ];

        byte[] mask = [0, 0];   // 1bpp, two rows, each padded to a 16-bit boundary: all zero = nothing masked

        HBITMAP colourBitmap;
        HBITMAP maskBitmap;

        fixed (byte* c = colour)
            colourBitmap = PInvoke.CreateBitmap(2, 2, 1, 32, c);

        fixed (byte* m = mask)
            maskBitmap = PInvoke.CreateBitmap(2, 2, 1, 1, m);

        try
        {
            var info = new ICONINFO
            {
                fIcon = true,
                hbmColor = colourBitmap,
                hbmMask = maskBitmap,
            };

            return PInvoke.CreateIconIndirect(&info);
        }
        finally
        {
            // CreateIconIndirect copies both.
            PInvoke.DeleteObject(colourBitmap);
            PInvoke.DeleteObject(maskBitmap);
        }
    }

    [Fact]
    public unsafe void TheWindowsOwnIconComesBackAsItsPixels()
    {
        using var window = new TestWindow("icon test");

        HICON icon = MakeIcon();
        Assert.False(icon.IsNull);

        try
        {
            PInvoke.SendMessage(new HWND(window.Handle), PInvoke.WM_SETICON, new WPARAM(PInvoke.ICON_BIG), new LPARAM((nint)icon.Value));

            IconPixels? pixels = WindowIconReader.Read(window.Handle, preferredSize: 32);

            Assert.NotNull(pixels);
            Assert.Equal("window", pixels.Source);
            Assert.Equal(2, pixels.Width);
            Assert.Equal(2, pixels.Height);
            Assert.Equal(16, pixels.Bgra.Length);

            // Opaque pixels come through untouched, blue-green-red-alpha.
            Assert.Equal([0, 0, 255, 255], pixels.Bgra[0..4]);
            Assert.Equal([0, 255, 0, 255], pixels.Bgra[4..8]);

            // The half-transparent one is premultiplied on the way out.
            Assert.Equal(128, pixels.Bgra[11]);
            Assert.InRange(pixels.Bgra[8], 127, 129);
            Assert.Equal(0, pixels.Bgra[9]);
            Assert.Equal(0, pixels.Bgra[10]);

            // And the transparent one is nothing at all.
            Assert.Equal([0, 0, 0, 0], pixels.Bgra[12..16]);
        }
        finally
        {
            PInvoke.SendMessage(new HWND(window.Handle), PInvoke.WM_SETICON, new WPARAM(PInvoke.ICON_BIG), new LPARAM(0));
            PInvoke.DestroyIcon(icon);
        }
    }

    [Fact]
    public unsafe void AWindowWithoutAnIconOfItsOwnAnswersWithItsFilesOrNothing()
    {
        // A window that set no icon gets its executable's, the way its taskbar button
        // does - which for a test window means the test host's, and whether the test
        // host has one is the host's business. So the expectation is read off the
        // file, and the reader is held to agreeing with it.
        using var window = new TestWindow("plain window");

        uint inFile;
        fixed (char* path = Environment.ProcessPath!)
            inFile = PInvoke.ExtractIconEx(path, -1, null, null, 0);

        IconPixels? pixels = WindowIconReader.Read(window.Handle, preferredSize: 32);

        if (inFile == 0)
        {
            Assert.Null(pixels);
            return;
        }

        Assert.NotNull(pixels);
        Assert.Equal("file", pixels.Source);
        Assert.True(pixels.Width > 0 && pixels.Height > 0);
        Assert.Equal(pixels.Width * pixels.Height * IconPixels.BytesPerPixel, pixels.Bgra.Length);
    }

    [Fact]
    public void AWindowThatHasGoneHasNoIcon()
    {
        nint handle;

        using (var window = new TestWindow("gone"))
            handle = window.Handle;

        Assert.Null(WindowIconReader.Read(handle, preferredSize: 32));
    }

    [Fact]
    public unsafe void TheCacheAnswersFromMemoryAndForgetsOnRequest()
    {
        using var window = new TestWindow("cached");
        HICON icon = MakeIcon();

        try
        {
            PInvoke.SendMessage(new HWND(window.Handle), PInvoke.WM_SETICON, new WPARAM(PInvoke.ICON_BIG), new LPARAM((nint)icon.Value));

            var cache = new WindowIconCache(lifetime: TimeSpan.FromMinutes(5));

            IconPixels? first = cache.Get(window.Handle, 32);
            IconPixels? second = cache.Get(window.Handle, 32);

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Equal(1, cache.Count);

            cache.Forget(window.Handle);
            Assert.Equal(0, cache.Count);

            // Read again after forgetting: a fresh object, same picture.
            IconPixels? third = cache.Get(window.Handle, 32);
            Assert.NotNull(third);
            Assert.NotSame(first, third);
            Assert.Equal(first.Bgra, third.Bgra);
        }
        finally
        {
            PInvoke.SendMessage(new HWND(window.Handle), PInvoke.WM_SETICON, new WPARAM(PInvoke.ICON_BIG), new LPARAM(0));
            PInvoke.DestroyIcon(icon);
        }
    }

    [Fact]
    public void TheCacheRemembersThatThereWasNothing()
    {
        // A window without an icon must not be asked on every focus change for as
        // long as it lives; the negative answer is an answer.
        nint handle;

        using (var window = new TestWindow("nothing"))
            handle = window.Handle;

        var cache = new WindowIconCache();

        Assert.Null(cache.Get(handle, 32));
        Assert.Equal(1, cache.Count);
        Assert.Null(cache.Get(handle, 32));
    }
}
