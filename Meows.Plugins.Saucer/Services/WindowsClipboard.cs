using System.Runtime.InteropServices;
using System.Text;

namespace Meows.Plugins.Saucer.Services;

/// <summary>What was last put on the clipboard.</summary>
public sealed record Clipping(ClippingKind Kind, string? Text, byte[]? Image)
{
    public bool IsImage => Kind == ClippingKind.Image && Image is { Length: > 0 };
}

public enum ClippingKind
{
    Nothing,
    Text,
    Image,
}

/// <summary>
/// Reads the Windows clipboard directly.
///
/// Uses the Win32 calls rather than the toolkit, because image support is the patchiest part of
/// any cross platform clipboard abstraction and images are the point here.
/// </summary>
public static class WindowsClipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIB = 8;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(nint handle);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint handle);

    private const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>
    /// Changes whenever anything is put on the clipboard and costs nothing to read. Much better
    /// than polling the clipboard itself to see if it looks different.
    /// </summary>
    public static uint SequenceNumber() => OperatingSystem.IsWindows() ? GetClipboardSequenceNumber() : 0;

    public static Clipping Read()
    {
        if (!OperatingSystem.IsWindows())
            return new Clipping(ClippingKind.Nothing, null, null);

        // The clipboard is a shared, single owner thing, and whoever wrote it last may still
        // have it open. Failing to get it is ordinary rather than exceptional.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(nint.Zero))
            {
                try
                {
                    return ReadWhileOpen();
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(30);
        }

        return new Clipping(ClippingKind.Nothing, null, null);
    }

    private static Clipping ReadWhileOpen()
    {
        // Browsers usually offer a real PNG alongside the bitmap, which saves converting one.
        var png = RegisterClipboardFormat("PNG");
        if (png != 0 && IsClipboardFormatAvailable(png))
        {
            var bytes = Bytes(GetClipboardData(png));
            if (bytes is { Length: > 8 })
                return new Clipping(ClippingKind.Image, null, bytes);
        }

        if (IsClipboardFormatAvailable(CF_DIB))
        {
            var dib = Bytes(GetClipboardData(CF_DIB));
            var bmp = BmpFromDib(dib);
            if (bmp is not null)
                return new Clipping(ClippingKind.Image, null, bmp);
        }

        if (IsClipboardFormatAvailable(CF_UNICODETEXT))
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            var text = TextFrom(handle);
            if (!string.IsNullOrEmpty(text))
                return new Clipping(ClippingKind.Text, text, null);
        }

        return new Clipping(ClippingKind.Nothing, null, null);
    }

    private static byte[]? Bytes(nint handle)
    {
        if (handle == nint.Zero)
            return null;

        var pointer = GlobalLock(handle);
        if (pointer == nint.Zero)
            return null;

        try
        {
            var size = (int)GlobalSize(handle);
            if (size <= 0)
                return null;

            var buffer = new byte[size];
            Marshal.Copy(pointer, buffer, 0, size);
            return buffer;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? TextFrom(nint handle)
    {
        if (handle == nint.Zero)
            return null;

        var pointer = GlobalLock(handle);
        if (pointer == nint.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// A clipboard bitmap is a bare DIB. The clipboard leaves off the file header that would
    /// make it a .bmp, so putting one back on the front is the entire conversion.
    /// </summary>
    public static byte[]? BmpFromDib(byte[]? dib)
    {
        if (dib is null || dib.Length < 40)
            return null;

        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize is < 12 or > 200 || headerSize > dib.Length)
            return null;

        var bitCount = BitConverter.ToInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var coloursUsed = BitConverter.ToInt32(dib, 32);

        var paletteEntries = coloursUsed != 0
            ? coloursUsed
            : bitCount <= 8 ? 1 << bitCount : 0;

        var paletteBytes = paletteEntries * 4;

        // BI_BITFIELDS on a plain 40 byte header puts three masks before the pixels.
        if (compression == 3 && headerSize == 40)
            paletteBytes += 12;

        var offset = 14 + headerSize + paletteBytes;
        if (offset > dib.Length + 14)
            return null;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(offset).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        return file;
    }

    /// <summary>
    /// Puts text back on the clipboard. Here rather than in the toolkit for the same reason as
    /// reading: one place owning the clipboard is easier to follow than two.
    /// </summary>
    public static bool SetText(string text)
    {
        if (!OperatingSystem.IsWindows() || text is null)
            return false;

        // Null terminated UTF-16, which is what CF_UNICODETEXT means.
        var bytes = (text.Length + 1) * 2;
        var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
        if (memory == nint.Zero)
            return false;

        var pointer = GlobalLock(memory);
        if (pointer == nint.Zero)
        {
            GlobalFree(memory);
            return false;
        }

        try
        {
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, text.Length * 2, 0);
        }
        finally
        {
            GlobalUnlock(memory);
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(nint.Zero))
            {
                Thread.Sleep(30);
                continue;
            }

            try
            {
                EmptyClipboard();

                // On success the system owns the block, so it must not be freed here. On
                // failure it is still ours and freeing it is the only way not to leak it.
                if (SetClipboardData(CF_UNICODETEXT, memory) != nint.Zero)
                    return true;

                GlobalFree(memory);
                return false;
            }
            finally
            {
                CloseClipboard();
            }
        }

        GlobalFree(memory);
        return false;
    }

    /// <summary>A short, safe file name for a clipping, with the moment it was taken.</summary>
    public static string SuggestName(DateTime when, string extension)
    {
        var stamp = when.ToString("yyyy-MM-dd HHmmss");
        return $"clip {stamp}{extension}";
    }

    /// <summary>The first line of a text clipping, tidied enough to show in a list.</summary>
    public static string Summarise(string text, int limit = 90)
    {
        var builder = new StringBuilder();
        foreach (var character in text.Trim())
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
            if (builder.Length >= limit)
            {
                builder.Append('…');
                break;
            }
        }

        return builder.ToString().Trim();
    }
}
