using System.IO.Compression;
using System.Security.Cryptography;

namespace Meows.Disk;

/// <summary>What an archive holds, from its table of contents and nothing more.</summary>
public sealed record ArchiveSummary(int EntryCount, long UnpackedSize, string TopKind, int TopCount)
{
    /// <summary>The kind most of the entries are, as a share of all of them.</summary>
    public int TopShare => EntryCount == 0 ? 0 : TopCount * 100 / EntryCount;
}

public enum TwinVerdict
{
    /// <summary>Every entry in the archive is in the folder, byte for byte, and nothing else is.</summary>
    Twin,

    /// <summary>Every entry is in the folder, and the folder holds more besides.</summary>
    TwinWithExtras,

    /// <summary>Something in the archive is missing from the folder or differs from it.</summary>
    Differs,

    /// <summary>The archive could not be opened, so nothing can be said.</summary>
    Unreadable,
}

/// <summary>
/// An archive held up against a folder: is everything in the one also in the other, and identical?
/// </summary>
public sealed record TwinReport(
    string ArchivePath,
    string FolderPath,
    TwinVerdict Verdict,
    int Matched,
    int Missing,
    int Different,
    int Extra,
    long ArchiveSize,
    long MatchedBytes)
{
    public bool IsTwin => Verdict is TwinVerdict.Twin or TwinVerdict.TwinWithExtras;
}

/// <summary>
/// Archives sitting next to their own extracted contents, which is one of the ways a drive quietly
/// fills up. Reading one is only ever the zip family; 7-Zip does the rest and there is no reason
/// to carry a second unpacker for a question that is answered by looking.
///
/// "Already extracted" is a content check, never a name match. photos.zip beside photos\ is a twin
/// only if the entries match the files by size and, for what still agrees, by content. A name
/// alone would call them twins when one is three months newer than the other.
/// </summary>
public static class Archives
{
    /// <summary>Kinds recognised as archives. Only some of these can be looked inside.</summary>
    private static readonly HashSet<string> ArchiveKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".cbz", ".7z", ".rar", ".cbr", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst",
    };

    private static readonly HashSet<string> ReadableKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".cbz",
    };

    public static bool IsArchive(string path) => ArchiveKinds.Contains(Path.GetExtension(path));

    /// <summary>Whether the contents can be listed and read here, which is only the zip family.</summary>
    public static bool CanRead(string path) => ReadableKinds.Contains(Path.GetExtension(path));

    /// <summary>
    /// The folder name an archive would extract to. Double extensions come off as a pair, so
    /// photos.tar.gz and photos\ are a candidate pair.
    /// </summary>
    public static string Stem(string archiveName)
    {
        var stem = Path.GetFileNameWithoutExtension(archiveName);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        return stem;
    }

    /// <summary>The folder beside an archive that shares its name, if there is one.</summary>
    public static string? SiblingFolderOf(string archivePath)
    {
        var parent = Path.GetDirectoryName(archivePath);
        if (parent is null)
            return null;

        var folder = Path.Combine(parent, Stem(Path.GetFileName(archivePath)));
        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>
    /// The archive beside a folder that shares its name, if there is one. A readable one wins
    /// when several do, because it is the one that can actually be checked.
    /// </summary>
    public static string? SiblingArchiveOf(string folderPath)
    {
        var directory = new DirectoryInfo(folderPath);
        if (directory.Parent is not { } parent)
            return null;

        FileInfo[] candidates;
        try
        {
            candidates = parent.GetFiles(directory.Name + ".*");
        }
        catch (Exception)
        {
            return null;
        }

        return candidates
            .Where(f => IsArchive(f.Name))
            .Where(f => string.Equals(Stem(f.Name), directory.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => CanRead(f.Name))
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    /// <summary>The table of contents, or null when the archive cannot be opened.</summary>
    public static ArchiveSummary? Peek(string path)
    {
        if (!CanRead(path))
            return null;

        try
        {
            using var zip = ZipFile.OpenRead(path);
            var kinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            var unpacked = 0L;

            foreach (var entry in FileEntries(zip))
            {
                count++;
                unpacked += entry.Length;
                var kind = Path.GetExtension(entry.Name) is { Length: > 0 } ext ? ext : "(none)";
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            }

            var top = kinds.OrderByDescending(k => k.Value).FirstOrDefault();
            return new ArchiveSummary(count, unpacked, top.Key ?? "", top.Value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Holds an archive up against a folder. Sizes first, then the first 64 KB, then the whole
    /// entry only for what still agrees, the same staging Purrge uses; an entry is decompressed
    /// once and both hashes come out of that one pass.
    /// </summary>
    public static TwinReport Compare(string archivePath, string folderPath, CancellationToken token = default)
    {
        long archiveSize;
        try
        {
            archiveSize = new FileInfo(archivePath).Length;
        }
        catch (Exception)
        {
            archiveSize = 0;
        }

        if (!CanRead(archivePath))
            return Unreadable(archivePath, folderPath, archiveSize);

        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entries = FileEntries(zip).ToList();
            var root = Path.GetFullPath(folderPath);

            // 7-Zip's Extract Here and Windows' Extract All disagree about whether an archive
            // whose entries all sit under one top folder lands as folder\top\... or folder\...
            // Look for the top folder; when it is not there, the tool stripped it.
            var strip = CommonRoot(entries) is { } top && !Directory.Exists(Path.Combine(root, top))
                ? top.Length + 1
                : 0;

            var matched = 0;
            var missing = 0;
            var different = 0;
            var matchedBytes = 0L;
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                var relative = strip > 0 && entry.FullName.Length > strip ? entry.FullName[strip..] : entry.FullName;
                var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

                // An entry named ..\..\something must not send the check wandering out of the folder.
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    missing++;
                    continue;
                }

                claimed.Add(target);

                var file = new FileInfo(target);
                if (!file.Exists)
                {
                    missing++;
                    continue;
                }

                if (file.Length != entry.Length || !SameBytes(entry, file.FullName, token))
                {
                    different++;
                    continue;
                }

                matched++;
                matchedBytes += entry.Length;
            }

            var extra = CountUnclaimed(new DirectoryInfo(root), claimed, token);

            var verdict = missing > 0 || different > 0
                ? TwinVerdict.Differs
                : extra > 0
                    ? TwinVerdict.TwinWithExtras
                    : TwinVerdict.Twin;

            // An empty archive matches everything and means nothing.
            if (entries.Count == 0)
                verdict = TwinVerdict.Differs;

            return new TwinReport(archivePath, folderPath, verdict, matched, missing, different, extra, archiveSize, matchedBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Unreadable(archivePath, folderPath, archiveSize);
        }
    }

    private static TwinReport Unreadable(string archivePath, string folderPath, long archiveSize) =>
        new(archivePath, folderPath, TwinVerdict.Unreadable, 0, 0, 0, 0, archiveSize, 0);

    private static IEnumerable<ZipArchiveEntry> FileEntries(ZipArchive zip) =>
        zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name) && !e.FullName.EndsWith('/'));

    /// <summary>The one folder every entry sits under, or null when they do not share one.</summary>
    private static string? CommonRoot(IReadOnlyList<ZipArchiveEntry> entries)
    {
        string? root = null;

        foreach (var entry in entries)
        {
            var slash = entry.FullName.IndexOf('/');
            if (slash <= 0)
                return null;

            var first = entry.FullName[..slash];
            if (root is null)
                root = first;
            else if (!string.Equals(root, first, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return root;
    }

    /// <summary>
    /// Whether a compressed entry and a file on disk hold the same bytes. The file is read in two
    /// stages so a mismatch in the first 64 KB costs 64 KB; the entry has no way to seek, so it is
    /// decompressed once with both hashes taken as it goes past.
    /// </summary>
    private static bool SameBytes(ZipArchiveEntry entry, string path, CancellationToken token)
    {
        string entryPartial;
        string entryFull;

        try
        {
            using var stream = entry.Open();
            using var full = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var partial = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[64 * 1024];
            var seen = 0L;
            int read;

            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                full.AppendData(buffer, 0, read);

                var room = ContentHash.PartialBytes - seen;
                if (room > 0)
                    partial.AppendData(buffer, 0, (int)Math.Min(room, read));

                seen += read;
            }

            entryPartial = Convert.ToHexString(partial.GetHashAndReset());
            entryFull = Convert.ToHexString(full.GetHashAndReset());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }

        if (ContentHash.Partial(path) != entryPartial)
            return false;

        return ContentHash.Full(path) == entryFull;
    }

    /// <summary>Files in the folder the archive said nothing about.</summary>
    private static int CountUnclaimed(DirectoryInfo root, HashSet<string> claimed, CancellationToken token)
    {
        var extra = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = stack.Pop();

            foreach (var file in FolderWalk.Files(current))
            {
                if (!claimed.Contains(file.FullName))
                    extra++;
            }

            foreach (var child in FolderWalk.Into(current, skipSystemFolders: false))
                stack.Push(child);
        }

        return extra;
    }
}
