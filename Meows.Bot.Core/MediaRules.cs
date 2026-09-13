using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Meows.Bot;

public enum MediaKind
{
    Unsupported,
    Photo,
    Video,
    Animation,
    Document,
    Comic,
}

/// <summary>
/// bot.py's file rules, copied here. Keep them in step. If these drift, the panel shows a
/// next-up the bot would never actually choose.
/// </summary>
public static partial class MediaRules
{
    private static readonly Dictionary<string, MediaKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MediaKind.Photo,
        [".jpeg"] = MediaKind.Photo,
        [".png"] = MediaKind.Photo,
        [".webp"] = MediaKind.Photo,
        [".jfif"] = MediaKind.Photo,
        [".bmp"] = MediaKind.Photo,
        [".mp4"] = MediaKind.Video,
        [".mov"] = MediaKind.Video,
        [".avi"] = MediaKind.Video,
        [".mkv"] = MediaKind.Video,
        [".gif"] = MediaKind.Animation,
        [".pdf"] = MediaKind.Document,
        [".zip"] = MediaKind.Comic,
        [".cbz"] = MediaKind.Comic,
    };

    /// <summary>Telegram only allows photos and videos in one media group, so pages are limited to those.</summary>
    private static readonly MediaKind[] ComicPageKinds = [MediaKind.Photo, MediaKind.Video];

    public const int MediaGroupLimit = 10;

    /// <summary>
    /// What the Bot API will take from a bot, in decimal megabytes as Telegram writes them.
    ///
    /// A photo over the line does not get skipped politely: the post fails at the moment it
    /// fires, which is three in the morning. sendPhoto stops at 10 MB and also refuses a
    /// picture whose width plus height passes 10000 or whose sides differ by more than twenty
    /// to one; everything else stops at 50 MB. Checked against the Bot API reference for
    /// sendPhoto and sendDocument on 2026-09-13; Telegram moves these occasionally.
    /// </summary>
    public const long PhotoLimitBytes = 10_000_000;

    public const long FileLimitBytes = 50_000_000;

    public const int PhotoMaxDimensionSum = 10_000;

    public const int PhotoMaxRatio = 20;

    /// <summary>
    /// The byte limit for a file of this kind, or null when there is not one that applies to
    /// the file itself: a comic is unpacked and its pages are what get sent.
    /// </summary>
    public static long? ByteLimit(MediaKind kind) => kind switch
    {
        MediaKind.Photo => PhotoLimitBytes,
        MediaKind.Video or MediaKind.Animation or MediaKind.Document => FileLimitBytes,
        _ => null,
    };

    /// <summary>Whether the file itself weighs more than the bot may send. Comics are not judged here.</summary>
    public static bool IsOverByteLimit(string path, long size) =>
        ByteLimit(KindOf(path)) is { } limit && size > limit;

    /// <summary>Whether a picture of this shape would be refused by sendPhoto regardless of its weight.</summary>
    public static bool IsPhotoTooLarge(int width, int height) =>
        width > 0 && height > 0 &&
        (width + height > PhotoMaxDimensionSum ||
         Math.Max(width, height) > PhotoMaxRatio * Math.Min(width, height));

    /// <summary>
    /// Opens a file for reading without stopping anyone else moving or deleting it.
    /// Thumbnails and previews load in the background while the very same files are being
    /// queued or posted, and a plain File.OpenRead holds enough of a lock to make that move
    /// fail with "used by another process".
    /// </summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static MediaKind KindOf(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var kind) ? kind : MediaKind.Unsupported;

    public static bool IsPostable(string path) => KindOf(path) != MediaKind.Unsupported;

    public static bool IsComic(string path) => KindOf(path) == MediaKind.Comic;

    /// <summary>
    /// Whether this file could be a page inside a comic. A media group takes photos and videos
    /// only, so a gif, a pdf or another archive has to be posted on its own.
    /// </summary>
    public static bool CanBeComicPage(string path) => ComicPageKinds.Contains(KindOf(path));

    /// <summary>Something Avalonia can decode. Video and pdf just get a glyph.</summary>
    public static bool IsRenderableImage(string path) =>
        KindOf(path) is MediaKind.Photo or MediaKind.Animation;

    /// <summary>
    /// A picture of a queued file for the screen: the file itself for a photo, the first page
    /// for a comic, nothing for a video or a document. Null on anything that cannot be read,
    /// because that is a display problem and not a posting one. Every tab that shows a queue
    /// used to carry its own copy of this.
    /// </summary>
    public static Avalonia.Media.Imaging.Bitmap? Thumbnail(string path, int? width, string orderMode = "name")
    {
        try
        {
            if (IsComic(path))
                return Media.Thumbnails.FromBytes(ComicCover(path, orderMode), width);

            return IsRenderableImage(path) ? Media.Thumbnails.FromFile(path, width) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex DigitRuns();

    /// <summary>bot.py's natural_key. Digits compare as numbers, so page2 comes before page10.</summary>
    public static int CompareNatural(string left, string right)
    {
        var a = DigitRuns().Split(left.ToLowerInvariant());
        var b = DigitRuns().Split(right.ToLowerInvariant());

        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var bothNumeric = a[i].Length > 0 && char.IsDigit(a[i][0]) &&
                              b[i].Length > 0 && char.IsDigit(b[i][0]);

            int cmp;
            if (bothNumeric && long.TryParse(a[i], out var na) && long.TryParse(b[i], out var nb))
                cmp = na.CompareTo(nb);
            else
                cmp = string.CompareOrdinal(a[i], b[i]);

            if (cmp != 0)
                return cmp;
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>Pages inside an archive, in the order the bot would post them.</summary>
    public static IReadOnlyList<string> ComicPages(string archivePath, string orderMode = "name")
    {
        try
        {
            using var zip = new ZipArchive(OpenShared(archivePath), ZipArchiveMode.Read);
            var entries = zip.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Where(e => !e.FullName.Contains("__MACOSX", StringComparison.Ordinal))
                .Where(e => !e.Name.StartsWith(".", StringComparison.Ordinal))
                .Where(e => ComicPageKinds.Contains(KindOf(e.Name)))
                .ToList();

            IEnumerable<ZipArchiveEntry> ordered = orderMode switch
            {
                "zip_order" => entries,
                "date" => entries
                    .OrderBy(e => e.LastWriteTime)
                    .ThenBy(e => e.Name, Comparer<string>.Create(CompareNatural)),
                _ => entries.OrderBy(e => e.Name, Comparer<string>.Create(CompareNatural)),
            };

            return ordered.Select(e => e.FullName).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>First page as raw bytes, to use as a cover.</summary>
    public static byte[]? ComicCover(string archivePath, string orderMode = "name")
    {
        var first = ComicPages(archivePath, orderMode).FirstOrDefault(IsRenderableImage);
        if (first is null)
            return null;

        try
        {
            using var zip = new ZipArchive(OpenShared(archivePath), ZipArchiveMode.Read);
            var entry = zip.GetEntry(first);
            if (entry is null)
                return null;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
