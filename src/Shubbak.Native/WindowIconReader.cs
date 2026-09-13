using System.Collections.Concurrent;
using System.Diagnostics;
using Shubbak.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Shubbak.Native;

/// <summary>
/// An icon as pixels: top-down rows of blue-green-red-alpha, colour premultiplied by
/// alpha, which is what a compositor consumes directly.
/// </summary>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
/// <param name="Bgra"><c>Width * Height * 4</c> bytes.</param>
/// <param name="Source">
/// Where it came from: <c>window</c>, <c>class</c> or <c>file</c>, in the order they
/// are tried.
/// </param>
public sealed record IconPixels(int Width, int Height, byte[] Bgra, string Source)
{
    public const int BytesPerPixel = 4;
}

/// <summary>
/// Reads a window's icon as pixels, for a client on the other side of a pipe.
/// </summary>
/// <remarks>
/// <para>
/// An icon handle belongs to the process that asked for it, so the only form an icon
/// can take across the pipe is its pixels. This asks the window - the way the taskbar
/// and Alt-Tab do - then its class, then the executable it belongs to, and turns the
/// first answer into a premultiplied BGRA bitmap the compositor can draw directly.
/// </para>
/// <para>
/// Bounded in every direction that matters on a daemon thread: the message to the
/// window has a short timeout and gives up at once on a window already known to be
/// hung, reads of the class and the file cannot block on another process at all, and
/// nothing here touches the window tree, so a caller on a pipe thread need not hop to
/// the loop. The cost of a read is a few kilobytes and a few hundred microseconds;
/// <see cref="WindowIconCache"/> is what keeps it from being paid twice.
/// </para>
/// </remarks>
public static class WindowIconReader
{
    /// <summary>How long a window gets to answer <c>WM_GETICON</c>.</summary>
    /// <remarks>
    /// Long enough for any application that is going to answer at all, short enough
    /// that one which is merely slow cannot hold a pipe thread hostage. Windows that
    /// are hung return immediately regardless, which is what the flag is for.
    /// </remarks>
    private const uint AnswerTimeoutMs = 100;

    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;

    /// <summary>
    /// The window's icon, or null when nothing anyone could ask has one.
    /// </summary>
    /// <param name="handle">The window.</param>
    /// <param name="preferredSize">
    /// Roughly how large the caller means to draw it, in pixels. Decides whether the
    /// large or the small variant is asked for first; the pixels come at whatever
    /// size the answer is, and the caller scales.
    /// </param>
    public static IconPixels? Read(nint handle, int preferredSize)
    {
        var window = new HWND(handle);

        if (!PInvoke.IsWindow(window)) return null;

        bool big = preferredSize > 20;

        // The window first and the class second, because that is the order they take
        // effect in: an application that calls WM_SETICON is deliberately overriding
        // whatever its class says, usually per document or per profile, and the class
        // icon is the fallback the system itself uses. The file last, for windows that
        // never set one at all - which still show the right thing on the taskbar.
        foreach (int which in big ? (ReadOnlySpan<int>)[IconBig, IconSmall2, IconSmall] : [IconSmall2, IconSmall, IconBig])
        {
            if (Ask(window, which) is { IsNull: false } icon && Convert(handle, icon, "window") is { } fromWindow)
                return fromWindow;
        }

        foreach (GET_CLASS_LONG_INDEX which in big
                     ? (ReadOnlySpan<GET_CLASS_LONG_INDEX>)[GET_CLASS_LONG_INDEX.GCLP_HICON, GET_CLASS_LONG_INDEX.GCLP_HICONSM]
                     : [GET_CLASS_LONG_INDEX.GCLP_HICONSM, GET_CLASS_LONG_INDEX.GCLP_HICON])
        {
            // A plain read of the window class structure. It cannot block, cannot
            // reach another process's code, and is the reason a hung application
            // still usually manages to show an icon.
            var icon = new HICON((nint)PInvoke.GetClassLongPtr(window, which));

            if (!icon.IsNull && Convert(handle, icon, "class") is { } fromClass)
                return fromClass;
        }

        return FromFile(handle, big);
    }

    /// <summary>One <c>WM_GETICON</c>, bounded and never fatal.</summary>
    private static unsafe HICON Ask(HWND window, int which)
    {
        nuint result = 0;

        LRESULT sent = PInvoke.SendMessageTimeout(
            window,
            PInvoke.WM_GETICON,
            new WPARAM((nuint)which),
            new LPARAM(0),
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK,
            AnswerTimeoutMs,
            &result);

        return sent == 0 ? HICON.Null : new HICON((nint)result);
    }

    /// <summary>The icon the window's executable shows on the taskbar, if it has one.</summary>
    private static unsafe IconPixels? FromFile(nint handle, bool big)
    {
        string? path = Win32Window.GetProcessPath(Win32Window.GetProcessId(handle));
        if (path is not { Length: > 0 }) return null;

        HICON large = default;
        HICON small = default;

        uint found;

        fixed (char* file = path)
            found = PInvoke.ExtractIconEx(file, 0, &large, &small, 1);

        try
        {
            if (found == 0) return null;

            HICON first = big ? large : small;
            HICON second = big ? small : large;

            return (first.IsNull ? null : Convert(handle, first, "file"))
                ?? (second.IsNull ? null : Convert(handle, second, "file"));
        }
        finally
        {
            // These two are this process's own, unlike the ones a window answers with.
            if (!large.IsNull) PInvoke.DestroyIcon(large);
            if (!small.IsNull) PInvoke.DestroyIcon(small);
        }
    }

    /// <summary>
    /// An icon handle as a premultiplied BGRA bitmap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An icon is two bitmaps: the colour and the mask. A modern icon carries its
    /// transparency in the colour bitmap's alpha channel and its mask is vestigial; an
    /// old one has no alpha at all and the mask is the only transparency there is. The
    /// two cannot be told apart by asking, only by looking: a colour bitmap whose alpha
    /// is zero everywhere is one that never had any, and the mask is used instead.
    /// </para>
    /// <para>
    /// Premultiplied on the way out, so a client can composite without a division per
    /// pixel and so the format on the wire is the one the compositor wants.
    /// </para>
    /// </remarks>
    private static unsafe IconPixels? Convert(nint handle, HICON icon, string source)
    {
        ICONINFO info;
        if (!PInvoke.GetIconInfo(icon, &info)) return null;

        try
        {
            // A monochrome icon keeps both halves in the mask and has no colour bitmap.
            // Rare enough - and ugly enough at any size - that the next candidate is a
            // better answer than a rendering of it.
            if (info.hbmColor.IsNull) return null;

            BITMAP shape;
            if (PInvoke.GetObject(info.hbmColor, sizeof(BITMAP), &shape) == 0) return null;

            int width = shape.bmWidth;
            int height = Math.Abs(shape.bmHeight);

            if (width <= 0 || height <= 0 || width > 512 || height > 512) return null;

            byte[]? colour = Bits(info.hbmColor, width, height);
            if (colour is null) return null;

            bool hasAlpha = false;

            for (int i = 3; i < colour.Length; i += 4)
            {
                if (colour[i] != 0) { hasAlpha = true; break; }
            }

            if (!hasAlpha)
            {
                byte[]? mask = info.hbmMask.IsNull ? null : Bits(info.hbmMask, width, height);

                // The mask is white where the icon is transparent. With no mask
                // either, the icon is opaque, which is at least a picture.
                for (int i = 0; i < colour.Length; i += 4)
                    colour[i + 3] = mask is not null && mask[i] != 0 ? (byte)0 : (byte)255;
            }

            for (int i = 0; i < colour.Length; i += 4)
            {
                int alpha = colour[i + 3];
                if (alpha == 255) continue;

                colour[i] = (byte)(((colour[i] * alpha) + 127) / 255);
                colour[i + 1] = (byte)(((colour[i + 1] * alpha) + 127) / 255);
                colour[i + 2] = (byte)(((colour[i + 2] * alpha) + 127) / 255);
            }

            return new IconPixels(width, height, colour, source);
        }
        finally
        {
            // GetIconInfo hands out copies, and they are the caller's to delete.
            if (!info.hbmColor.IsNull) PInvoke.DeleteObject(info.hbmColor);
            if (!info.hbmMask.IsNull) PInvoke.DeleteObject(info.hbmMask);
        }
    }

    /// <summary>A bitmap's pixels as 32-bit top-down BGRA, whatever its own format.</summary>
    private static unsafe byte[]? Bits(HBITMAP bitmap, int width, int height)
    {
        HDC dc = PInvoke.CreateCompatibleDC(HDC.Null);
        if (dc.IsNull) return null;

        try
        {
            var info = new BITMAPINFO();
            info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height;   // top-down
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;

            var bytes = new byte[width * height * IconPixels.BytesPerPixel];

            fixed (byte* p = bytes)
            {
                int lines = PInvoke.GetDIBits(dc, bitmap, 0, (uint)height, p, &info, DIB_USAGE.DIB_RGB_COLORS);
                if (lines == 0) return null;
            }

            return bytes;
        }
        finally
        {
            PInvoke.DeleteDC(dc);
        }
    }
}

/// <summary>
/// Remembers what <see cref="WindowIconReader"/> answered, for a while.
/// </summary>
/// <remarks>
/// <para>
/// A bar asks about the window in front every time focus moves, and focus moves
/// between the same few windows all day. The answer is a few kilobytes and rarely
/// changes; asking the window again each time would be a message to another process
/// per focus change for the sake of nothing.
/// </para>
/// <para>
/// A while rather than for ever, because icons do change - a mail client's badge, a
/// browser's profile - and a window handle is reused once its window has gone. Half a
/// minute is short enough that a change is seen soon and long enough that a burst of
/// focus changes costs one read. Bounded in size and emptied wholesale when full,
/// which is simpler than an eviction policy and, at these sizes, indistinguishable
/// from one. Answers that there is no icon are remembered too, or a window without
/// one would be asked on every focus change for as long as it lived.
/// </para>
/// <para>
/// Safe from any thread; the reads it wraps are as well.
/// </para>
/// </remarks>
public sealed class WindowIconCache
{
    private readonly record struct Key(long Handle, bool Big);

    private readonly record struct Entry(long ReadAt, IconPixels? Icon);

    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;

    public WindowIconCache(TimeSpan? lifetime = null, int capacity = 128)
    {
        _lifetime = lifetime ?? TimeSpan.FromSeconds(30);
        _capacity = capacity;
    }

    /// <summary>How many answers are held, for diagnostics.</summary>
    public int Count => _entries.Count;

    /// <summary>The window's icon, read now or remembered from recently.</summary>
    public IconPixels? Get(nint handle, int preferredSize)
    {
        var key = new Key(handle, preferredSize > 20);
        long now = Stopwatch.GetTimestamp();

        if (_entries.TryGetValue(key, out Entry entry) && Stopwatch.GetElapsedTime(entry.ReadAt, now) < _lifetime)
            return entry.Icon;

        IconPixels? icon;

        try
        {
            icon = WindowIconReader.Read(handle, preferredSize);
        }
        catch (Exception ex)
        {
            // A window that vanishes mid-read, a bitmap in a format nobody expected:
            // none of it is worth more than a line, and none of it may reach the pipe.
            Log.Debug(LogCategory.Ipc, $"could not read the icon of window {handle}: {ex.GetType().Name}: {ex.Message}");
            icon = null;
        }

        if (_entries.Count >= _capacity) _entries.Clear();
        _entries[key] = new Entry(now, icon);

        return icon;
    }

    /// <summary>Drops what is remembered about a window, when it is known to have gone.</summary>
    public void Forget(nint handle)
    {
        _entries.TryRemove(new Key(handle, true), out _);
        _entries.TryRemove(new Key(handle, false), out _);
    }
}
