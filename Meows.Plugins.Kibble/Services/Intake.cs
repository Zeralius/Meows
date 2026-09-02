using System.IO.Compression;
using Meows.Bot;

namespace Meows.Plugins.Kibble.Services;

/// <summary>Which timestamp a newly queued file should carry.</summary>
public enum IntakeStamp
{
    /// <summary>Keep the source file's time, so genuinely older art posts first.</summary>
    KeepSource,

    /// <summary>Stamp the moment it was queued, so it is first in, first out.</summary>
    QueuedNow,
}

public enum IntakeOutcome
{
    Sent,
    AlreadyInGroup,

    /// <summary>A duplicate, set aside in the group's Duplicates folder rather than refused.</summary>
    MovedToDuplicates,

    NotPostable,
    EmptyComic,
    Failed,
}

/// <summary>What to do with a file the group already has.</summary>
public enum DuplicateHandling
{
    /// <summary>Refuse it and leave it where it is, for you to look at.</summary>
    Refuse,

    /// <summary>Move it into the group's Duplicates folder and carry on.</summary>
    MoveAside,
}

/// <summary>How the pages inside a new comic are ordered.</summary>
public enum PageOrder
{
    /// <summary>Sorted naturally by file name, so page2 lands before page10.</summary>
    ByName,

    /// <summary>Left in the order the caller handed them over, which is the order you picked them.</summary>
    AsPicked,
}

/// <summary>A file that went into a comic, with the timestamp it had before it did.</summary>
public sealed record BundledPage(string Path, DateTime Modified);

public sealed record IntakeResult(
    IntakeOutcome Outcome,
    string SourcePath,
    string? Destination,
    string? Detail,
    IReadOnlyList<BundledPage>? Bundled = null)
{
    /// <summary>
    /// Whether the file left where it was. True for a duplicate set aside as well as a send,
    /// because both empty the tile out of the grid and both are undoable.
    /// </summary>
    public bool Moved => Outcome is IntakeOutcome.Sent or IntakeOutcome.MovedToDuplicates;

    /// <summary>
    /// Whether it actually joined the queue. A duplicate moved aside did not, so it takes no
    /// place in the posting order and gets no queue timestamp.
    /// </summary>
    public bool Queued => Outcome == IntakeOutcome.Sent;

    /// <summary>True when this send bundled several files into one archive.</summary>
    public bool IsBundle => Bundled is { Count: > 0 };
}

/// <summary>
/// Moving a file into a group's queue, with the checks that are only useful at the moment you
/// click. Everything here happens before the file lands, so a rejected file stays where it was
/// and the grid still shows it.
/// </summary>
public static class Intake
{
    /// <summary>
    /// Checks a file against a destination without moving anything, so the UI can grey out or
    /// warn before the click. Null means it is fine to send.
    /// </summary>
    public static IntakeResult? Inspect(string source, BotWorkspace workspace, GroupConfig group)
    {
        if (!MediaRules.IsPostable(source))
            return new IntakeResult(IntakeOutcome.NotPostable, source, null,
                "The bot does not recognise this extension, so it would sit in the queue being skipped.");

        if (MediaRules.IsComic(source) && MediaRules.ComicPages(source, group.ComicOrder ?? "name").Count == 0)
            return new IntakeResult(IntakeOutcome.EmptyComic, source, null,
                "No postable pages inside this archive, so sending it would fail.");

        // Against the queue and the archive both: re-sending something already posted is the
        // mistake that actually shows.
        var existing = workspace.Scan(workspace.ToSendFolder(group))
            .Concat(workspace.Scan(workspace.AlreadySentFolder(group), recursive: true));

        var match = ContentHash.FindMatch(source, existing);
        if (match is not null)
        {
            var where = match.Contains(Path.DirectorySeparatorChar + "Already_Sent" + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)
                ? "already posted to this group"
                : "already in this group's queue";
            return new IntakeResult(IntakeOutcome.AlreadyInGroup, source, null,
                $"{where}, as {Path.GetFileName(match)}.");
        }

        return null;
    }

    /// <summary>Moves the file into the group's To_Send, having checked it first.</summary>
    public static IntakeResult Send(
        string source,
        BotWorkspace workspace,
        GroupConfig group,
        IntakeStamp stamp,
        DuplicateHandling duplicates = DuplicateHandling.Refuse)
    {
        var problem = Inspect(source, workspace, group);

        if (problem is { Outcome: IntakeOutcome.AlreadyInGroup } && duplicates == DuplicateHandling.MoveAside)
            return SetAside(source, workspace, group, problem.Detail);

        if (problem is not null)
            return problem;

        try
        {
            var folder = workspace.ToSendFolder(group);
            Directory.CreateDirectory(folder);

            var target = UniquePath(folder, Path.GetFileName(source));
            var sourceWritten = File.GetLastWriteTimeUtc(source);

            File.Move(source, target);

            // The bot orders the queue by modified time, so this is not cosmetic. Import a
            // few hundred files with the clock stamped and "oldest" stops meaning anything.
            File.SetLastWriteTimeUtc(target, stamp == IntakeStamp.KeepSource ? sourceWritten : DateTime.UtcNow);

            return new IntakeResult(IntakeOutcome.Sent, source, target, null);
        }
        catch (Exception ex)
        {
            return new IntakeResult(IntakeOutcome.Failed, source, null, ex.Message);
        }
    }

    /// <summary>
    /// Moves a file the group already has into its Duplicates folder.
    ///
    /// Moved rather than deleted, and never over the top of anything. The whole point of finding
    /// a duplicate is that a copy already exists somewhere, which is a poor reason to be careless
    /// with this one.
    /// </summary>
    private static IntakeResult SetAside(
        string source,
        BotWorkspace workspace,
        GroupConfig group,
        string? why)
    {
        try
        {
            var folder = workspace.DuplicatesFolder(group);
            Directory.CreateDirectory(folder);

            var target = UniquePath(folder, Path.GetFileName(source));
            var sourceWritten = File.GetLastWriteTimeUtc(source);

            File.Move(source, target);

            // Its own date, kept. Nothing reads these in order, and the date is the only clue
            // left about where the file came from.
            File.SetLastWriteTimeUtc(target, sourceWritten);

            return new IntakeResult(IntakeOutcome.MovedToDuplicates, source, target,
                why ?? "already in this group.");
        }
        catch (Exception ex)
        {
            return new IntakeResult(IntakeOutcome.Failed, source, null, ex.Message);
        }
    }

    /// <summary>Files to pick before bundling is worth doing at all.</summary>
    public const int MinBundle = 2;

    /// <summary>
    /// Checks a set of files that would become one comic. Null means it is fine to send.
    /// </summary>
    public static IntakeResult? InspectBundle(IReadOnlyList<string> sources)
    {
        if (sources.Count < MinBundle)
            return new IntakeResult(IntakeOutcome.NotPostable, sources.FirstOrDefault() ?? "", null,
                $"Pick at least {MinBundle} files to make a comic.");

        var offender = sources.FirstOrDefault(s => !MediaRules.CanBeComicPage(s));
        if (offender is not null)
            return new IntakeResult(IntakeOutcome.NotPostable, offender, null,
                $"{Path.GetFileName(offender)} cannot be a comic page. A media group takes photos and " +
                "videos only, so gifs, pdfs and archives have to be sent on their own.");

        return null;
    }

    /// <summary>
    /// Bundles several files into one .cbz in the group's queue, so they post as a single
    /// comic instead of as separate items.
    /// </summary>
    public static IntakeResult SendAsComic(
        IReadOnlyList<string> sources,
        BotWorkspace workspace,
        GroupConfig group,
        IntakeStamp stamp,
        string archiveName,
        PageOrder pageOrder = PageOrder.ByName)
    {
        var problem = InspectBundle(sources);
        if (problem is not null)
            return problem;

        // Page order has to survive whichever comic_order the destination group happens to
        // use, and Kibble does not control that setting. So whatever order is chosen here, the
        // archive is written to satisfy all three at once: each entry gets an index prefix so
        // "name" sorts them that way, entry times ascend so "date" agrees, and "zip_order" is
        // simply the order they were written.
        var ordered = pageOrder == PageOrder.ByName
            ? sources.OrderBy(x => Path.GetFileName(x) ?? x, Comparer<string>.Create(MediaRules.CompareNatural)).ToList()
            : sources.ToList();

        var pages = new List<BundledPage>();
        var temp = Path.Combine(Path.GetTempPath(), $"kibble_{Guid.NewGuid():N}.cbz");

        try
        {
            foreach (var page in ordered)
                pages.Add(new BundledPage(page, File.GetLastWriteTimeUtc(page)));

            var width = ordered.Count.ToString().Length;
            var entryTime = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                for (var i = 0; i < ordered.Count; i++)
                {
                    var name = $"{(i + 1).ToString().PadLeft(width, '0')}_{Path.GetFileName(ordered[i])}";

                    // No compression on purpose. These are jpgs, pngs and mp4s, all already
                    // compressed, so deflating them costs time and saves close to nothing.
                    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    entry.LastWriteTime = entryTime.AddMinutes(i);

                    using var into = entry.Open();
                    using var from = File.OpenRead(ordered[i]);
                    from.CopyTo(into);
                }
            }

            // Read it back through the same code the bot uses, before anything is deleted. If
            // the pages do not come out, the selection is still sitting in the grid.
            var readBack = MediaRules.ComicPages(temp, group.ComicOrder ?? "name");
            if (readBack.Count != ordered.Count)
                return new IntakeResult(IntakeOutcome.EmptyComic, ordered[0], null,
                    $"The archive read back with {readBack.Count} of {ordered.Count} pages, so nothing was queued.");

            var folder = workspace.ToSendFolder(group);
            Directory.CreateDirectory(folder);
            var target = UniquePath(folder, EnsureCbz(archiveName));

            File.Move(temp, target);

            // A comic is as old as the material in it, so keeping the source date takes the
            // oldest page. Using the newest would push a set of old art to the back.
            File.SetLastWriteTimeUtc(target,
                stamp == IntakeStamp.KeepSource ? pages.Min(x => x.Modified) : DateTime.UtcNow);

            foreach (var page in ordered)
                File.Delete(page);

            return new IntakeResult(IntakeOutcome.Sent, ordered[0], target, null, pages);
        }
        catch (Exception ex)
        {
            return new IntakeResult(IntakeOutcome.Failed, ordered.FirstOrDefault() ?? "", null, ex.Message);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                    // A leftover in the temp folder is not worth failing the send over.
                }
            }
        }
    }

    /// <summary>
    /// Moves several files into the queue one by one, in the order they were given. Every file
    /// is checked on its own, so one refusal does not stop the rest: the results come back in
    /// the same order and say individually what happened to each.
    /// </summary>
    public static IReadOnlyList<IntakeResult> SendMany(
        IReadOnlyList<string> sources,
        BotWorkspace workspace,
        GroupConfig group,
        IntakeStamp stamp,
        DuplicateHandling duplicates = DuplicateHandling.Refuse)
    {
        var results = new List<IntakeResult>();

        // Stamping a batch all at once would give every file the same modified time, and the
        // bot orders its queue by that, so the order they were sent in would dissolve into
        // whatever the filesystem felt like. A second apart keeps it.
        var queuedAt = DateTime.UtcNow;
        var position = 0;

        foreach (var source in sources)
        {
            var result = Send(source, workspace, group, stamp, duplicates);
            results.Add(result);

            // Queued, not merely moved. A duplicate set aside is not in the queue, so it takes
            // no place in the posting order and none of the timestamps below apply to it.
            if (!result.Queued)
                continue;

            if (stamp == IntakeStamp.QueuedNow)
            {
                try
                {
                    File.SetLastWriteTimeUtc(result.Destination!, queuedAt.AddSeconds(position));
                }
                catch (Exception)
                {
                    // The file is queued either way. Only its place in the order is at stake.
                }
            }

            position++;
        }

        return results;
    }

    /// <summary>Puts a file, or a whole bundled comic, back where it came from.</summary>
    public static bool Undo(IntakeResult result)
    {
        if (!result.Moved || result.Destination is null)
            return false;

        if (result.Bundled is { Count: > 0 } bundled)
            return UndoComic(result.Destination, bundled);

        try
        {
            if (!File.Exists(result.Destination) || File.Exists(result.SourcePath))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(result.SourcePath)!);
            File.Move(result.Destination, result.SourcePath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Unpacks a comic back into the files it was made from, then removes the archive. The
    /// bytes come out of the archive itself rather than a copy kept aside, so an undo cannot
    /// restore something subtly different from what went in.
    /// </summary>
    private static bool UndoComic(string archive, IReadOnlyList<BundledPage> pages)
    {
        try
        {
            if (!File.Exists(archive))
                return false;

            using (var zip = ZipFile.OpenRead(archive))
            {
                // Entries were written in the same order as the recorded pages. If that no
                // longer holds, something else has touched the archive, and unpacking it
                // would put the wrong bytes under the wrong names.
                var entries = zip.Entries;
                if (entries.Count != pages.Count)
                    return false;

                for (var i = 0; i < pages.Count; i++)
                {
                    if (File.Exists(pages[i].Path))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(pages[i].Path)!);
                    entries[i].ExtractToFile(pages[i].Path);

                    // The entry times were synthetic, written only to pin page order, so the
                    // real ones come back from what was recorded at send time.
                    File.SetLastWriteTimeUtc(pages[i].Path, pages[i].Modified);
                }
            }

            File.Delete(archive);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whatever the name box holds, turned into a usable .cbz file name.</summary>
    private static string EnsureCbz(string name)
    {
        var cleaned = string.Concat((name ?? "").Trim().Split(Path.GetInvalidFileNameChars()));
        if (cleaned.Length == 0)
            cleaned = "comic";

        return cleaned.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase) ? cleaned : cleaned + ".cbz";
    }

    /// <summary>
    /// Never overwrite. A name clash between two different files is entirely possible when
    /// pulling from several sources, and losing one silently would be the worst outcome here.
    /// </summary>
    private static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; n < 10_000; n++)
        {
            candidate = Path.Combine(folder, $"{stem}_{n}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{stem}_{Guid.NewGuid():N}{ext}");
    }
}
