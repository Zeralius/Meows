using System.IO.Compression;
using Meows.Media;

namespace Meows.Bot;

// Lived in Portion until 2.7.0. Moved here because Kibble wants the same verdict at the click
// and Telegram Poster wants it on a queue row, and three copies of what the Bot API refuses is
// how one of them gets a fix and the others do not.

/// <summary>Why the bot would refuse it.</summary>
public enum Trouble
{
    /// <summary>Heavier than the Bot API takes for this kind of file.</summary>
    OverBytes,

    /// <summary>A photo whose width plus height passes what sendPhoto allows.</summary>
    TooManyPixels,

    /// <summary>A photo more than twenty times as wide as it is tall, or the other way. Scaling does not fix this.</summary>
    OddRatio,

    /// <summary>A comic with pages the bot could not send as photos.</summary>
    HeavyPages,

    /// <summary>A comic with no page the bot can post. It fails outright.</summary>
    EmptyComic,

    /// <summary>Pages named as pictures that are not pictures inside. The bot will fail on the batch holding them.</summary>
    BadPages,

    /// <summary>Files inside that are neither photo nor video, which the bot skips without a word.</summary>
    ForeignFiles,

    /// <summary>Long enough to go out as many separate posts rather than the one that was expected.</summary>
    ManyBatches,
}

/// <summary>One file in a queue that will not go as it is.</summary>
public sealed record Heavy(
    GroupConfig Group,
    string Path,
    MediaKind Kind,
    long Size,
    long Limit,
    IReadOnlyList<Trouble> Troubles,
    int? Width,
    int? Height,
    int Pages,
    int HeavyPageCount,
    int BadPageCount = 0,
    int ForeignCount = 0)
{
    /// <summary>
    /// Whether Portion can do anything about it. Pictures and comic pages can be made smaller;
    /// a video needs ffmpeg, which is a different tool, a picture with an odd ratio needs
    /// cropping, which is a decision rather than a conversion, and a comic with bad or missing
    /// pages needs a person to look inside it.
    /// </summary>
    public bool CanShrink =>
        Kind switch
        {
            MediaKind.Photo => !Troubles.Contains(Trouble.OddRatio),
            MediaKind.Comic => Troubles.Contains(Trouble.HeavyPages) &&
                               !Troubles.Contains(Trouble.EmptyComic) &&
                               !Troubles.Contains(Trouble.BadPages),
            _ => false,
        };

    /// <summary>Whether the bot would fail on it, as opposed to merely doing something surprising.</summary>
    public bool WillFail => Troubles.Any(t => t is Trouble.OverBytes or Trouble.TooManyPixels or Trouble.OddRatio
        or Trouble.HeavyPages or Trouble.EmptyComic or Trouble.BadPages);

    public int Batches => Pages == 0 ? 0 : (int)Math.Ceiling(Pages / (double)MediaRules.MediaGroupLimit);
}

/// <summary>
/// Finds what will not post, or will not post the way it was meant to, across every queue.
///
/// Weighing is cheap: the size comes from the directory and the dimensions from a picture's
/// header without decoding it. Comics are the expensive part, since each one has to be opened
/// to look at its pages, which is why the whole walk runs as background work. While a comic is
/// open it is judged on everything the bot will judge it on, not only weight: whether it has
/// any postable page at all, whether its pages are what their names say, what is inside that
/// the bot will skip, and how many separate posts it becomes.
/// </summary>
public static class Weigher
{
    /// <summary>More batches than this is worth saying out loud before the bot sends them one by one.</summary>
    public const int BatchesWorthMentioning = 3;

    /// <summary>What a page must fit, being sent as a photo in a media group.</summary>
    public static readonly MediaLimits PhotoLimits = new(
        0, MediaRules.PhotoLimitBytes, MediaRules.PhotoMaxDimensionSum / 2,
        [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP]);

    public static IReadOnlyList<Heavy> Weigh(BotWorkspace workspace, BotConfig config, Action<string>? report, CancellationToken token)
    {
        var found = new List<Heavy>();

        foreach (var group in config.Groups)
        {
            token.ThrowIfCancellationRequested();
            report?.Invoke(group.Name);

            foreach (var path in workspace.Scan(workspace.ToSendFolder(group)))
            {
                token.ThrowIfCancellationRequested();
                if (Inspect(group, path) is { } heavy)
                    found.Add(heavy);
            }
        }

        return found.OrderByDescending(h => h.Size).ToList();
    }

    /// <summary>One file's verdict, or null when the bot would take it.</summary>
    public static Heavy? Inspect(GroupConfig group, string path)
    {
        var kind = MediaRules.KindOf(path);
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return null;
        }

        return kind switch
        {
            MediaKind.Comic => InspectComic(group, path, size),
            MediaKind.Photo => InspectPhoto(group, path, size),
            _ when MediaRules.ByteLimit(kind) is { } limit && size > limit =>
                new Heavy(group, path, kind, size, limit, [Trouble.OverBytes], null, null, 0, 0),
            _ => null,
        };
    }

    private static Heavy? InspectPhoto(GroupConfig group, string path, long size)
    {
        var troubles = new List<Trouble>();
        if (size > MediaRules.PhotoLimitBytes)
            troubles.Add(Trouble.OverBytes);

        int? width = null, height = null;
        try
        {
            // Only the header is needed for the size, and only for the formats whose header
            // says. Anything else is judged on weight alone.
            var report = Metadata.Inspect(Head(path));
            if (report.HasSize)
            {
                width = report.Width;
                height = report.Height;
                if (report.Width + report.Height > MediaRules.PhotoMaxDimensionSum)
                    troubles.Add(Trouble.TooManyPixels);
                if (Math.Max(report.Width, report.Height) > MediaRules.PhotoMaxRatio * Math.Min(report.Width, report.Height))
                    troubles.Add(Trouble.OddRatio);
            }
        }
        catch (Exception)
        {
        }

        return troubles.Count == 0
            ? null
            : new Heavy(group, path, MediaKind.Photo, size, MediaRules.PhotoLimitBytes, troubles, width, height, 0, 0);
    }

    private static Heavy? InspectComic(GroupConfig group, string path, long size)
    {
        try
        {
            var pages = MediaRules.ComicPages(path, group.ComicOrder ?? "name");
            var troubles = new List<Trouble>();
            var heavyPages = 0;
            var badPages = 0;
            var foreign = 0;

            using (var archive = ZipFile.OpenRead(path))
            {
                var pageSet = new HashSet<string>(pages, StringComparer.Ordinal);
                foreach (var entry in archive.Entries)
                {
                    // Folder entries are structure, not content.
                    if (entry.FullName.EndsWith('/') || entry.Length == 0 && entry.Name.Length == 0)
                        continue;

                    if (!pageSet.Contains(entry.FullName))
                    {
                        foreign++;
                        continue;
                    }

                    var kind = MediaRules.KindOf(entry.FullName);
                    if (kind == MediaKind.Photo && entry.Length > MediaRules.PhotoLimitBytes)
                        heavyPages++;
                    else if (kind == MediaKind.Video && entry.Length > MediaRules.FileLimitBytes)
                        heavyPages++;

                    // A picture that is not a picture. The name says jpg; the bytes say otherwise,
                    // and Telegram will say so too, on the batch holding it.
                    if (kind == MediaKind.Photo && !LooksLikeAPicture(entry))
                        badPages++;
                }
            }

            if (pages.Count == 0)
                troubles.Add(Trouble.EmptyComic);
            if (heavyPages > 0)
                troubles.Add(Trouble.HeavyPages);
            if (badPages > 0)
                troubles.Add(Trouble.BadPages);
            if (foreign > 0)
                troubles.Add(Trouble.ForeignFiles);
            if (pages.Count > MediaRules.MediaGroupLimit * BatchesWorthMentioning)
                troubles.Add(Trouble.ManyBatches);

            return troubles.Count == 0
                ? null
                : new Heavy(group, path, MediaKind.Comic, size, MediaRules.PhotoLimitBytes, troubles, null, null, pages.Count, heavyPages, badPages, foreign);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The first bytes of an entry, sniffed. Enough to tell a JPEG from a text file wearing its name.</summary>
    private static bool LooksLikeAPicture(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            var head = new byte[64];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (Metadata.Sniff(head.AsSpan(0, read)) != ImageFormat.Unknown)
                return true;

            // BMP and JFIF variants are pictures the bot posts that the sniffer does not name.
            return read >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M';
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Enough of a file for its header. A JPEG's size marker can sit behind a large embedded
    /// preview, so this is generous rather than minimal, but it is still nothing next to reading
    /// a 12 MB photo in full.
    /// </summary>
    private static byte[] Head(string path)
    {
        using var stream = MediaRules.OpenShared(path);
        var buffer = new byte[(int)Math.Min(stream.Length, 512 * 1024)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read == buffer.Length ? buffer : buffer[..read];
    }
}

/// <summary>How a shrink went.</summary>
public sealed record SlimOutcome(bool Ok, string? Path, long Before, long After, string? Error)
{
    public static SlimOutcome Failed(string error, long before) => new(false, null, before, before, error);
}

/// <summary>
/// Makes a heavy file light enough to post.
///
/// Never in place. The new file is written beside the original under a temporary name, checked
/// to be smaller and to decode, and only then does the original go to the Recycle Bin and the
/// new one take its name. An encoder that fails halfway and leaves a truncated file in a queue
/// is worse than the oversized file was.
///
/// The modified time is carried over. The bot orders a queue by it, and a shrink that stamped
/// "now" on the file would quietly send it to the back of the line.
/// </summary>
public static class Slimmer
{
    /// <summary>
    /// How an original is put out of the way once its replacement is proven. The Recycle Bin
    /// unless a test says otherwise; nothing in the app ever passes anything else.
    /// </summary>
    public static Func<string, string?> RemoveOriginal { get; set; } = path =>
    {
        var outcome = Meows.Disk.RecycleBin.Send([path]);
        return outcome.Failed > 0 ? outcome.FailureReason ?? "The original could not be sent to the Recycle Bin." : null;
    };

    public static SlimOutcome Shrink(Heavy heavy)
    {
        if (!heavy.CanShrink)
            return SlimOutcome.Failed("Not something Portion can shrink.", heavy.Size);

        return heavy.Kind == MediaKind.Comic ? ShrinkComic(heavy) : ShrinkPhoto(heavy);
    }

    private static SlimOutcome ShrinkPhoto(Heavy heavy)
    {
        try
        {
            var original = File.ReadAllBytes(heavy.Path);
            var fitted = Preparer.Fit(Preparer.Clean(original), Weigher.PhotoLimits);

            if (fitted.Unreadable || fitted.Bytes.LongLength > MediaRules.PhotoLimitBytes || fitted.Bytes.LongLength >= original.LongLength)
                return SlimOutcome.Failed("Could not make it small enough.", heavy.Size);

            if (Preparer.Decode(fitted.Bytes) is not { } check)
                return SlimOutcome.Failed("The shrunk picture did not decode, so the original was left alone.", heavy.Size);
            check.Dispose();

            var final = Path.ChangeExtension(heavy.Path, fitted.Extension);
            return Replace(heavy.Path, final, fitted.Bytes, heavy.Size);
        }
        catch (Exception ex)
        {
            return SlimOutcome.Failed(ex.Message, heavy.Size);
        }
    }

    /// <summary>
    /// The archive rebuilt with every heavy page fitted and everything else copied through
    /// byte for byte, in the same order under the same names, so the bot's page order and its
    /// resume state still mean the same thing.
    /// </summary>
    private static SlimOutcome ShrinkComic(Heavy heavy)
    {
        try
        {
            var temp = heavy.Path + ".portion";
            using (var source = ZipFile.OpenRead(heavy.Path))
            using (var target = new ZipArchive(File.Create(temp), ZipArchiveMode.Create))
            {
                foreach (var entry in source.Entries)
                {
                    using var input = entry.Open();
                    using var buffer = new MemoryStream();
                    input.CopyTo(buffer);
                    var bytes = buffer.ToArray();

                    if (MediaRules.KindOf(entry.FullName) == MediaKind.Photo && bytes.LongLength > MediaRules.PhotoLimitBytes)
                    {
                        var fitted = Preparer.Fit(Preparer.Clean(bytes), Weigher.PhotoLimits);
                        if (fitted.Unreadable || fitted.Bytes.LongLength > MediaRules.PhotoLimitBytes)
                            throw new InvalidOperationException($"Could not make page {entry.FullName} small enough.");

                        // Keep the name, and the extension the page had, unless the format
                        // genuinely changed: a renamed page is a different page to the bot's
                        // resume state.
                        var name = fitted.Converted ? Path.ChangeExtension(entry.FullName, fitted.Extension) : entry.FullName;
                        bytes = fitted.Bytes;
                        Write(target, name, bytes, entry.LastWriteTime);
                    }
                    else
                    {
                        Write(target, entry.FullName, bytes, entry.LastWriteTime);
                    }
                }
            }

            var after = new FileInfo(temp).Length;
            var pagesAfter = MediaRules.ComicPages(temp, heavy.Group.ComicOrder ?? "name").Count;
            if (pagesAfter != heavy.Pages)
            {
                File.Delete(temp);
                return SlimOutcome.Failed($"The rebuilt comic read back with {pagesAfter} of {heavy.Pages} pages, so the original was left alone.", heavy.Size);
            }

            return Swap(heavy.Path, heavy.Path, temp, heavy.Size, after);
        }
        catch (Exception ex)
        {
            return SlimOutcome.Failed(ex.Message, heavy.Size);
        }
    }

    private static void Write(ZipArchive target, string name, byte[] bytes, DateTimeOffset when)
    {
        var entry = target.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = when;
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static SlimOutcome Replace(string original, string final, byte[] bytes, long before)
    {
        var temp = original + ".portion";
        File.WriteAllBytes(temp, bytes);
        return Swap(original, final, temp, before, bytes.LongLength);
    }

    /// <summary>Original to the bin, temp to the final name, modified time carried over.</summary>
    private static SlimOutcome Swap(string original, string final, string temp, long before, long after)
    {
        var modified = File.GetLastWriteTimeUtc(original);

        var failure = RemoveOriginal(original);
        if (failure is not null || File.Exists(original))
        {
            File.Delete(temp);
            return SlimOutcome.Failed(failure ?? "The original is still there, so the new file was not put in its place.", before);
        }

        File.Move(temp, final, overwrite: false);
        File.SetLastWriteTimeUtc(final, modified);
        return new SlimOutcome(true, final, before, after, null);
    }
}
