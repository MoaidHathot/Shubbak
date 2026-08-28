using System.Runtime.InteropServices;
using Shubbak.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;

namespace Shubbak.Native;

/// <summary>
/// Puts text on the Windows clipboard.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the palette that first wanted it, because the command line has
/// the same need - an inspection report is most useful in an issue, and both places
/// that produce one should hand it over the same way.
/// </para>
/// <para>
/// Every failure is a warning and a <c>false</c>, never an exception. The clipboard is
/// a single system-wide resource that any process may hold open at any moment, so
/// failing to get it is ordinary rather than exceptional - and a palette that tore
/// itself down because a copy did not land would be trading a minor disappointment
/// for a real one.
/// </para>
/// </remarks>
public static class Clipboard
{
    /// <summary>How many times to wait for whoever else has it open.</summary>
    /// <remarks>
    /// Clipboard managers, remote desktop clients and browsers all open it briefly on
    /// a copy, so losing the first attempt is common and losing five in a row is not.
    /// Short enough that a failure still feels immediate.
    /// </remarks>
    private const int Attempts = 5;

    private const int RetryDelayMilliseconds = 20;

    /// <summary>Replaces the clipboard's contents with <paramref name="text"/>.</summary>
    /// <param name="text">What to copy. Empty is accepted and clears the clipboard.</param>
    /// <param name="owner">
    /// The window to associate the clipboard with. Zero works and is what a process
    /// with no window of its own must pass; naming the real window is better, because
    /// Windows reports the owner to clipboard viewers.
    /// </param>
    /// <returns>Whether the text is now on the clipboard.</returns>
    public static unsafe bool SetText(string text, nint owner = 0)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!Open(owner)) return false;

        try
        {
            if (!PInvoke.EmptyClipboard())
            {
                Log.Warn(LogCategory.Wm, $"could not empty the clipboard: {Marshal.GetLastWin32Error()}");
                return false;
            }

            // Room for a null terminator, because CF_UNICODETEXT is a C string: the
            // block is handed to the system rather than described to it, so there is
            // nowhere to say how long it is.
            nuint bytes = (nuint)((text.Length + 1) * sizeof(char));

            // GMEM_MOVEABLE, not fixed. SetClipboardData takes ownership of the block
            // and the system later frees it with GlobalFree, which is only valid for
            // a moveable allocation.
            HGLOBAL block = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, bytes);

            if (block == 0)
            {
                Log.Warn(LogCategory.Wm, "could not allocate a clipboard block");
                return false;
            }

            void* target = PInvoke.GlobalLock(block);

            if (target is null)
            {
                _ = PInvoke.GlobalFree(block);
                Log.Warn(LogCategory.Wm, "could not lock the clipboard block");
                return false;
            }

            try
            {
                text.AsSpan().CopyTo(new Span<char>(target, text.Length));
                ((char*)target)[text.Length] = '\0';
            }
            finally
            {
                _ = PInvoke.GlobalUnlock(block);
            }

            // Ownership passes to the system on success, so the block must not be
            // freed here. On failure it has not, so it must be - and getting that
            // backwards either leaks a block per copy or hands the shell a pointer
            // to memory that has been given back.
            if (PInvoke.SetClipboardData((uint)CLIPBOARD_FORMAT.CF_UNICODETEXT, (HANDLE)(nint)block) == 0)
            {
                _ = PInvoke.GlobalFree(block);
                Log.Warn(LogCategory.Wm, $"could not set clipboard data: {Marshal.GetLastWin32Error()}");
                return false;
            }

            return true;
        }
        finally
        {
            _ = PInvoke.CloseClipboard();
        }
    }

    /// <summary>Takes the clipboard, waiting briefly for whoever else has it.</summary>
    private static bool Open(nint owner)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            if (PInvoke.OpenClipboard((HWND)owner)) return true;

            Thread.Sleep(RetryDelayMilliseconds);
        }

        Log.Warn(LogCategory.Wm, "another process is holding the clipboard open");
        return false;
    }
}
