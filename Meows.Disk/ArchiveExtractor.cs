using System.IO.Compression;

namespace Meows.Disk;

/// <summary>
/// What unpacking an archive would cost, worked out before a byte is written, or why it will not
/// be done: a folder of that name is already there, the archive wants a password, the drive has
/// no room. A plan with a refusal is never carried out.
/// </summary>
public sealed record ExtractPlan(string Archive, string Folder, int Files, long Bytes, long? Free, string? Refusal)
{
    public bool CanGo => Refusal is null;
}

/// <summary>How an extraction went, and what the check afterwards found.</summary>
/// <param name="Check">The archive held up against what was unpacked, or null when unpacking did not finish.</param>
public sealed record ExtractReport(bool Ok, int Files, long Bytes, string Folder, TwinReport? Check, string? Error)
{
    /// <summary>Every listed file is in the folder with its bytes intact, so the archive is now a copy.</summary>
    public bool Verified => Check is { IsTwin: true };
}

/// <summary>
/// Chonk's verb for an archive: unpack it beside itself, then check the result against the
/// archive's own listing with the same comparison that finds twins. Only the zip family, as
/// everywhere else here; 7-Zip does the rest.
///
/// Nothing is overwritten. The unpacking goes into a folder with a temporary name that is only
/// given the real one once every entry is out, so a cancel or a failure halfway leaves the
/// archive and nothing else, and a folder that already has the name is a refusal, not a merge.
/// An entry whose name climbs out of the folder stops the whole thing.
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>What an unfinished extraction is called, beside where it will land.</summary>
    public const string PartSuffix = ".meows-part";

    /// <summary>How much room past the unpacked size is wanted, so the drive is not left at zero.</summary>
    public const double Headroom = 1.05;

    /// <summary>Where, how much, and whether: read from the table of contents, nothing unpacked.</summary>
    public static ExtractPlan Plan(string archive, Func<string, long?>? freeSpace = null)
    {
        var parent = Path.GetDirectoryName(archive) ?? "";
        var folder = Path.Combine(parent, Archives.Stem(Path.GetFileName(archive)));

        if (!Archives.CanRead(archive))
            return new ExtractPlan(archive, folder, 0, 0, null, Refusal.NotZip);
        if (Directory.Exists(folder) || File.Exists(folder))
            return new ExtractPlan(archive, folder, 0, 0, null, Refusal.FolderThere);

        int files;
        long bytes;
        try
        {
            (files, bytes, var locked) = AccessTime.Preserving(archive, () =>
            {
                using var zip = ZipFile.OpenRead(archive);
                var entries = zip.Entries.Where(e => !IsFolder(e)).ToList();
                return (entries.Count, entries.Sum(e => e.Length), entries.Any(e => e.IsEncrypted));
            });
            if (locked)
                return new ExtractPlan(archive, folder, files, bytes, null, Refusal.Password);
        }
        catch (Exception)
        {
            return new ExtractPlan(archive, folder, 0, 0, null, Refusal.Unreadable);
        }

        if (files == 0)
            return new ExtractPlan(archive, folder, 0, 0, null, Refusal.Empty);

        var free = (freeSpace ?? FreeOn)(folder);
        if (free is { } room && room < bytes * Headroom)
            return new ExtractPlan(archive, folder, files, bytes, free, Refusal.NoRoom);

        return new ExtractPlan(archive, folder, files, bytes, free, null);
    }

    /// <summary>Why a plan was refused, as a code the caller turns into words.</summary>
    public static class Refusal
    {
        public const string NotZip = "notzip";
        public const string FolderThere = "folderthere";
        public const string Password = "password";
        public const string Unreadable = "unreadable";
        public const string Empty = "empty";
        public const string NoRoom = "noroom";
    }

    private static long? FreeOn(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsFolder(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    /// <summary>
    /// Unpacks as planned, then checks. Throws only when cancelled, having taken the half-made
    /// folder away again; everything else comes back in the report.
    /// </summary>
    public static ExtractReport Extract(ExtractPlan plan, IProgress<(int Done, int Total)>? progress, CancellationToken token)
    {
        if (!plan.CanGo)
            return new ExtractReport(false, 0, 0, plan.Folder, null, plan.Refusal);

        var part = plan.Folder + PartSuffix;
        var done = 0;
        long bytes = 0;
        try
        {
            // A part folder here is one of ours from a run that did not finish.
            if (Directory.Exists(part))
                Directory.Delete(part, recursive: true);
            Directory.CreateDirectory(part);
            var root = Path.GetFullPath(part) + Path.DirectorySeparatorChar;

            AccessTime.Preserving(plan.Archive, () =>
            {
                using var zip = ZipFile.OpenRead(plan.Archive);
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var target = Path.GetFullPath(Path.Combine(part, entry.FullName.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"{entry.FullName} would land outside the folder, so nothing was kept.");

                    if (IsFolder(entry))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: false);
                    done++;
                    bytes += entry.Length;
                    progress?.Report((done, plan.Files));
                }
                return 0;
            });

            Directory.Move(part, plan.Folder);
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(part);
            return new ExtractReport(false, done, bytes, plan.Folder, null, ex.Message);
        }

        var check = Archives.Compare(plan.Archive, plan.Folder, token);
        return new ExtractReport(check.IsTwin, done, bytes, plan.Folder, check, check.IsTwin ? null : ArchiveInspector.NotTwin(check));
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
