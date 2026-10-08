using System.Text;

namespace Meows.Disk;

/// <summary>What a file's first bytes say it is: a name for people and the extensions that are honest for it.</summary>
/// <param name="Name">"PNG", "a web page". Short, for a sentence like "says jpg, is PNG".</param>
/// <param name="Extensions">Every extension that fits the content, the usual one first, each with its dot.</param>
public sealed record Sniffed(string Name, IReadOnlyList<string> Extensions)
{
    /// <summary>The extension to rename to: the usual one for this content.</summary>
    public string Extension => Extensions[0];

    public bool Fits(string extension) => Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A file whose name says one thing and whose bytes say another.</summary>
/// <param name="Says">The extension it has, without the dot, lower case: "jpg".</param>
/// <param name="Is">What the bytes are.</param>
public sealed record Mismatch(string Says, Sniffed Is)
{
    /// <summary>Where the file would be if its name told the truth: the same folder and stem, the right extension.</summary>
    public string RenamedPath(string path) => Path.ChangeExtension(path, Is.Extension);
}

/// <summary>
/// Files whose extension lies about them, told by the first bytes: a .jpg that is a PNG, a .zip
/// that is a RAR, a .png that is HTML because the download was a login page. The posting bot
/// fails on exactly these, and nothing looking only at names can see them.
///
/// Only a verdict both sides of which are known is given. A file whose bytes are not
/// recognised, or whose extension is not one of those listed here, is never called a liar:
/// saying nothing is better than a confident wrong answer that gets a file renamed. And this is
/// the container, never the stream: an .mp4 holding a codec the bot cannot send is a different
/// question.
/// </summary>
public static class FileSniff
{
    /// <summary>How much of a file is read. Every signature here sits within the first few dozen bytes.</summary>
    public const int HeadLength = 64;

    private static readonly Sniffed Jpeg = new("JPEG", [".jpg", ".jpeg", ".jfif"]);
    private static readonly Sniffed Png = new("PNG", [".png"]);
    private static readonly Sniffed WebP = new("WebP", [".webp"]);
    private static readonly Sniffed Gif = new("GIF", [".gif"]);
    private static readonly Sniffed Bmp = new("BMP", [".bmp"]);

    // ISO media: MP4 and QuickTime share a container, and a .mov holding an mp4 brand is normal,
    // so any of the three names is taken as honest for either.
    private static readonly Sniffed Mp4 = new("MP4", [".mp4", ".m4v", ".mov"]);
    private static readonly Sniffed QuickTime = new("QuickTime", [".mov", ".mp4", ".m4v"]);
    private static readonly Sniffed Matroska = new("Matroska", [".mkv", ".webm"]);
    private static readonly Sniffed WebM = new("WebM", [".webm", ".mkv"]);
    private static readonly Sniffed Avi = new("AVI", [".avi"]);

    private static readonly Sniffed Zip = new("ZIP", [".zip", ".cbz"]);
    private static readonly Sniffed Rar = new("RAR", [".rar", ".cbr"]);
    private static readonly Sniffed SevenZip = new("7-Zip", [".7z", ".cb7"]);
    private static readonly Sniffed Pdf = new("PDF", [".pdf"]);
    private static readonly Sniffed Html = new("a web page", [".html", ".htm"]);

    /// <summary>The extensions a verdict can be given about: everything any signature here claims.</summary>
    private static readonly HashSet<string> Judged = new(
        new[] { Jpeg, Png, WebP, Gif, Bmp, Mp4, Matroska, Avi, Zip, Rar, SevenZip, Pdf, Html }.SelectMany(s => s.Extensions),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>What these bytes are, or null when they are nothing listed here.</summary>
    public static Sniffed? Of(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return Jpeg;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return Png;
        if (head.Length >= 12 && head.StartsWith("RIFF"u8))
        {
            if (head[8..12].SequenceEqual("WEBP"u8))
                return WebP;
            if (head[8..12].SequenceEqual("AVI "u8))
                return Avi;
            return null;
        }
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
            return Gif;
        if (head.Length >= 14 && head.StartsWith("BM"u8) && BitConverter.ToInt32(head[2..6]) > 0 && head[6] == 0 && head[7] == 0 && head[8] == 0 && head[9] == 0)
            return Bmp;
        if (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8))
            return head[8..12].SequenceEqual("qt  "u8) ? QuickTime : Mp4;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
            return head.IndexOf("webm"u8) >= 0 ? WebM : Matroska;
        if (head.StartsWith("PK\x03\x04"u8) || head.StartsWith("PK\x05\x06"u8))
            return Zip;
        if (head.StartsWith("Rar!\x1A\x07"u8))
            return Rar;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
            return SevenZip;
        if (head.StartsWith("%PDF-"u8))
            return Pdf;
        if (LooksLikeHtml(head))
            return Html;
        return null;
    }

    /// <summary>
    /// A login page saved as picture.png: text, after an optional byte order mark and some white
    /// space, that opens with a doctype or an html tag.
    /// </summary>
    private static bool LooksLikeHtml(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            head = head[3..];
        var start = 0;
        while (start < head.Length && head[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            start++;
        var text = Encoding.ASCII.GetString(head[start..]).ToLowerInvariant();
        return text.StartsWith("<!doctype html", StringComparison.Ordinal) || text.StartsWith("<html", StringComparison.Ordinal);
    }

    /// <summary>The first bytes of a file, read shared so a move elsewhere is not blocked. Empty when it cannot be read.</summary>
    public static byte[] Head(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeadLength];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return buffer[..read];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether this file's extension lies, and what it really is. Null when it tells the truth,
    /// when the bytes are not recognised, or when the extension is not one this can judge.
    /// </summary>
    public static Mismatch? Check(string path) => Check(path, Head(path));

    /// <summary>The same, for bytes already read, such as an entry inside an archive.</summary>
    public static Mismatch? Check(string name, ReadOnlySpan<byte> head)
    {
        var extension = Path.GetExtension(name);
        if (extension.Length == 0 || !Judged.Contains(extension))
            return null;
        if (Of(head) is not { } sniffed || sniffed.Fits(extension))
            return null;
        return new Mismatch(extension.TrimStart('.').ToLowerInvariant(), sniffed);
    }
}
