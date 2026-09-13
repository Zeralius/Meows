using Avalonia.Media.Imaging;

namespace Meows.Media;

/// <summary>
/// One place that turns a file or a byte array into a picture for the screen.
///
/// Nine plugins had written the same fifteen lines: open the file without locking it, decode to
/// a width, return null on anything that goes wrong because a tile with no picture on it is
/// still a tile. Now they call this. Anything comic shaped sits one level up in
/// <c>Meows.Bot</c>, which knows what a cover is; this only knows pixels.
/// </summary>
public static class Thumbnails
{
    /// <summary>What Avalonia can decode. Video and PDF get a glyph from whoever asked.</summary>
    private static readonly string[] Renderable =
        [".jpg", ".jpeg", ".png", ".webp", ".jfif", ".bmp", ".gif"];

    public static bool IsRenderable(string path) =>
        Renderable.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The file decoded to <paramref name="width"/> pixels across, or at full size when that is
    /// null. Null when it is not a picture or cannot be read. Opened shared, so previewing a
    /// file never stops it being moved, queued or sent to the Recycle Bin at the same moment.
    /// </summary>
    public static Bitmap? FromFile(string path, int? width)
    {
        if (!IsRenderable(path))
            return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Decode(stream, width);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The bytes decoded, for pictures that came out of an archive, a clipboard or the network.</summary>
    public static Bitmap? FromBytes(byte[]? bytes, int? width)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            return Decode(stream, width);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Bitmap Decode(Stream stream, int? width) =>
        width is { } w ? Bitmap.DecodeToWidth(stream, w) : new Bitmap(stream);
}
