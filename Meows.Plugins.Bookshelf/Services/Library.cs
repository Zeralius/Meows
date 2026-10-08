using Meows.Disk;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Bookshelf.Services;

/// <summary>Where a book stands. Unread is the default; reading is started but not finished.</summary>
public enum BookStatus
{
    Unread,
    Reading,
    Finished,
}

/// <summary>One watched folder: where, and whether it is on.</summary>
public sealed class BookFolder
{
    public string Path { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>What the shelf thinks about one file. Keyed by path; gone files are pruned on scan.</summary>
public sealed class BookState
{
    public string Path { get; set; } = "";

    public BookStatus Status { get; set; } = BookStatus.Unread;
}

/// <summary>The last scan, as kept: enough for the Home line with no scan in memory.</summary>
public sealed class BookSummary
{
    public DateTime ScannedUtc { get; set; }

    public int Books { get; set; }

    public long Bytes { get; set; }

    public int Unfinished { get; set; }

    public int Duplicates { get; set; }

    public long DuplicateBytes { get; set; }
}

/// <summary>One ebook found, with what the scan worked out about it.</summary>
public sealed class Book
{
    public string Path { get; set; } = "";

    public string Title { get; set; } = "";

    public string Author { get; set; } = "";

    public string Format { get; set; } = "";

    public long Size { get; set; }

    /// <summary>When it was last opened, by the filesystem's own stamp. Never read by us.</summary>
    public DateTime LastOpenedUtc { get; set; }

    public string? Hash { get; set; }

    /// <summary>True for every copy but the most recently opened of identical bytes.</summary>
    public bool IsDuplicate { get; set; }

    public BookStatus Status { get; set; } = BookStatus.Unread;
}

/// <summary>
/// The shelf: what ebooks are on disk, which are the same book twice, and which were started
/// but never finished. Reading only, never opening: the last-opened stamp is the truth, and
/// hashing puts it back afterwards, so a scan is not the books being used.
/// </summary>
public static class Library
{
    /// <summary>What counts as an ebook.</summary>
    public static readonly string[] Extensions = [".epub", ".pdf", ".mobi", ".azw", ".azw3"];

    /// <summary>
    /// Where ebooks live by default: a Calibre library under the profile. Only folders that
    /// exist are named; everything else is added by hand on the tab.
    /// </summary>
    public static IReadOnlyList<string> FindSources(string profileRoot)
    {
        var found = new List<string>();
        foreach (var candidate in new[] { Path.Combine(profileRoot, "Calibre Library") })
        {
            try
            {
                if (Directory.Exists(candidate) &&
                    !found.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    found.Add(candidate);
            }
            catch (Exception)
            {
                // A folder that cannot even be asked about is not a source.
            }
        }
        return found;
    }

    /// <summary>
    /// Folds freshly found sources into the kept folders: new ones arrive switched on, and
    /// nothing kept is ever dropped because a drive is unplugged today.
    /// </summary>
    public static int MergeFolders(List<BookFolder> kept, IReadOnlyList<string> found)
    {
        var added = 0;
        foreach (var folder in found)
        {
            if (kept.Any(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase)))
                continue;
            kept.Add(new BookFolder { Path = folder, Enabled = true });
            added++;
        }
        return added;
    }

    /// <summary>
    /// A file name as author and title. Ebooks are usually shelved as "Author - Title", so the
    /// first " - " splits them; anything else is a title with no author. Underscores become
    /// spaces; the casing is left as shelved.
    /// </summary>
    public static (string Author, string Title) ParseName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Trim();
        var split = name.IndexOf(" - ", StringComparison.Ordinal);
        if (split > 0)
            return (name[..split].Trim(), name[(split + 3)..].Trim());

        // "Author.Title" with dots is the same habit in another punctuation.
        var dotted = name.IndexOf('.');
        if (dotted > 0 && name.Length > dotted + 1 && name.IndexOf(' ') < 0)
            return (name[..dotted].Trim(), name[(dotted + 1)..].Trim().Replace('.', ' '));

        return ("", name);
    }

    /// <summary>
    /// Every ebook under the switched-on folders, joined with what the shelf thinks. States for
    /// files that are gone are forgotten here, quietly. Folders that are gone today give nothing
    /// rather than an error.
    /// </summary>
    public static List<Book> Scan(IReadOnlyList<BookFolder> folders, List<BookState> states)
    {
        var books = new List<Book>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders.Where(f => f.Enabled))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                string full;
                FileInfo info;
                try
                {
                    full = Path.GetFullPath(file);
                    info = new FileInfo(full);
                    if (!info.Exists)
                        continue;
                }
                catch (Exception)
                {
                    continue;
                }
                if (!seen.Add(full))
                    continue;

                DateTime opened;
                try
                {
                    opened = File.GetLastAccessTimeUtc(full);
                }
                catch (Exception)
                {
                    continue;
                }

                var (author, title) = ParseName(full);
                books.Add(new Book
                {
                    Path = full,
                    Title = title,
                    Author = author,
                    Format = Path.GetExtension(full).TrimStart('.').ToLowerInvariant(),
                    Size = info.Length,
                    LastOpenedUtc = opened,
                });
            }
        }

        var present = new HashSet<string>(books.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        states.RemoveAll(s => !present.Contains(s.Path));

        var byPath = states.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var book in books)
            if (byPath.TryGetValue(book.Path, out var state))
                book.Status = state.Status;

        MarkDuplicates(books);
        return books
            .OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Identical bytes, staged the cheap way: sizes first, then the first 64 KB, then the whole
    /// file only where that still agrees. The most recently opened of each set stays the book;
    /// every copy after it is the duplicate.
    /// </summary>
    public static void MarkDuplicates(List<Book> books)
    {
        foreach (var sizeGroup in books.GroupBy(b => b.Size).Where(g => g.Count() > 1))
        {
            var partials = new Dictionary<string, List<Book>>(StringComparer.Ordinal);
            foreach (var book in sizeGroup)
            {
                var partial = ContentHash.Partial(book.Path);
                if (partial is null)
                    continue;
                if (!partials.TryGetValue(partial, out var group))
                    partials[partial] = group = [];
                group.Add(book);
            }

            foreach (var partial in partials.Values.Where(g => g.Count > 1))
            {
                var fulls = new Dictionary<string, List<Book>>(StringComparer.Ordinal);
                foreach (var book in partial)
                {
                    var full = ContentHash.Full(book.Path);
                    if (full is null)
                        continue;
                    book.Hash = full;
                    if (!fulls.TryGetValue(full, out var group))
                        fulls[full] = group = [];
                    group.Add(book);
                }

                foreach (var full in fulls.Values.Where(g => g.Count > 1))
                    foreach (var copy in full.OrderByDescending(b => b.LastOpenedUtc).Skip(1))
                        copy.IsDuplicate = true;
            }
        }
    }

    /// <summary>Started but never finished, least recently opened first: the Catnip order.</summary>
    public static IReadOnlyList<Book> Unfinished(IEnumerable<Book> books) =>
        books.Where(b => b.Status == BookStatus.Reading).OrderBy(b => b.LastOpenedUtc).ToList();

    /// <summary>Copies but the most recently opened of every identical set.</summary>
    public static IReadOnlyList<Book> DuplicateVictims(IReadOnlyList<Book> books) =>
        books.Where(b => b.IsDuplicate).OrderByDescending(b => b.LastOpenedUtc).ToList();

    /// <summary>
    /// Copies books into the staging folder for the ereader: what is already there under the
    /// same name and size is left alone, the rest is copied over. Nothing is ever moved.
    /// </summary>
    public static (int Copied, int Skipped) Stage(IReadOnlyList<string> paths, string stagingFolder)
    {
        var copied = 0;
        var skipped = 0;
        foreach (var path in paths)
        {
            try
            {
                var target = Path.Combine(stagingFolder, Path.GetFileName(path));
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(path).Length)
                {
                    skipped++;
                    continue;
                }
                Directory.CreateDirectory(stagingFolder);
                File.Copy(path, target, overwrite: true);
                copied++;
            }
            catch (Exception)
            {
                skipped++;
            }
        }
        return (copied, skipped);
    }

    public static BookSummary Summarise(IReadOnlyList<Book> books, DateTime scannedUtc)
    {
        var dups = books.Where(b => b.IsDuplicate).ToList();
        return new BookSummary
        {
            ScannedUtc = scannedUtc,
            Books = books.Count,
            Bytes = books.Sum(b => b.Size),
            Unfinished = books.Count(b => b.Status == BookStatus.Reading),
            Duplicates = dups.Count,
            DuplicateBytes = dups.Sum(b => b.Size),
        };
    }

    /// <summary>The line under the header: what is unfinished, and what copies hold.</summary>
    public static string SummaryOf(BookSummary? summary, IMeowsText text)
    {
        if (summary is null || summary.Books == 0)
            return "";

        if (summary.Duplicates > 0)
            return text.Format("bookshelf.summary.dups", summary.Books, summary.Unfinished,
                summary.Duplicates, Humanise(summary.DuplicateBytes));
        return text.Format("bookshelf.summary.clear", summary.Books, summary.Unfinished);
    }

    /// <summary>The Home line. Red while copies are holding room.</summary>
    public static Glance? GlanceOf(BookSummary? summary, IMeowsText text)
    {
        var line = SummaryOf(summary, text);
        return line.Length == 0 ? null : new Glance(line, (summary?.Duplicates ?? 0) > 0);
    }

    /// <summary>How long ago, in the one unit that reads naturally.</summary>
    public static string Ago(DateTime whenUtc, DateTime nowUtc, IMeowsText text)
    {
        var days = (nowUtc - whenUtc).TotalDays;
        return days switch
        {
            < 1 => text["bookshelf.ago.today"],
            < 2 => text["bookshelf.ago.yesterday"],
            < 60 => text.Format("bookshelf.ago.days", (int)days),
            < 730 => text.Format("bookshelf.ago.months", (int)(days / 30.44)),
            _ => text.Format("bookshelf.ago.years", (int)(days / 365.25)),
        };
    }

    /// <summary>Bytes as a person would say them.</summary>
    public static string Humanise(long bytes) =>
        bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        };

    /// <summary>The key for a word rather than the word. Nothing here knows any language.</summary>
    public static string Describe(BookStatus status) => "bookshelf.state." + status.ToString().ToLowerInvariant();
}
