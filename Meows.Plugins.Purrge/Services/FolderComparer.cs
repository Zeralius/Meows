using Meows.Disk;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Purrge.Services;

/// <summary>
/// How a compare may take shortcuts. Trusting timestamps means a file whose copy has the same
/// size and modified time is taken as identical without being read, which is what most backup
/// tools assume and is fast; not trusting them reads both, which is the only way to catch a copy
/// that went bad without anything touching its date.
/// </summary>
public sealed record CompareOptions(bool TrustTimestamps = false, bool SkipSystemFolders = true);

public enum FindingKind
{
    /// <summary>In the source, not in the copy at all.</summary>
    Missing,

    /// <summary>In both, different, and the copy is older. The copy fell behind.</summary>
    Stale,

    /// <summary>In both, different, and the copy is not older. Something changed on the copy side.</summary>
    Different,

    /// <summary>In the copy, not in the source. Not wrong, but worth knowing.</summary>
    Extra,

    /// <summary>One side could not be read, so nothing can be said about it.</summary>
    Unreadable,
}

/// <summary>One thing the copy gets wrong, or one thing it has that the source has not.</summary>
public sealed record Finding(
    FindingKind Kind,
    string RelativePath,
    string SourcePath,
    string CopyPath,
    long? SourceSize,
    long? CopySize,
    DateTime? SourceModifiedUtc,
    DateTime? CopyModifiedUtc)
{
    /// <summary>The bigger of the two, for ordering: what is most worth looking at first.</summary>
    public long Size => Math.Max(SourceSize ?? 0, CopySize ?? 0);
}

/// <summary>The whole answer: how much was looked at, and everything that was not right.</summary>
public sealed record CompareReport(int Checked, long BytesChecked, int ReadInFull, IReadOnlyList<Finding> Findings)
{
    public int Count(FindingKind kind) => Findings.Count(f => f.Kind == kind);

    /// <summary>Whether the copy is complete and identical. Extras do not count against it.</summary>
    public bool CopyIsGood => Findings.All(f => f.Kind == FindingKind.Extra);
}

/// <summary>
/// Is everything in the source also in the copy, and is it the same?
///
/// A verifier and nothing more. It reads two trees and reports; it never writes, copies, or
/// deletes, and there is deliberately no method on it that could. The moment it started fixing
/// what it found it would be a backup tool with all of a backup tool's failure modes, and the
/// honest scope that makes it worth having would be gone.
///
/// The check is Purrge's staged one turned to face two roots: size first, then the first 64 KB,
/// then the whole file only when the first two agree. A file whose sizes differ is settled without
/// being opened.
/// </summary>
public sealed class FolderComparer
{
    public async Task<CompareReport> CompareAsync(
        string source,
        string copy,
        CompareOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        return await Task.Run(() => Compare(source, copy, options, progress, token), token).ConfigureAwait(true);
    }

    public CompareReport Compare(
        string source,
        string copy,
        CompareOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        var sourceRoot = Path.GetFullPath(source);
        var copyRoot = Path.GetFullPath(copy);
        var walk = new ScanOptions(MinimumBytes: 0, options.SkipSystemFolders);

        // Everything on the source side, by its path relative to the root. The relative path is
        // the identity: the same file under both roots is the same file.
        var sourceFiles = new List<string>();
        foreach (var path in DuplicateScanner.EnumerateFiles(sourceRoot, walk, token))
        {
            sourceFiles.Add(path);
            if (sourceFiles.Count % 500 == 0)
                progress?.Report(new ScanProgress(ScanPhase.Enumerating, sourceFiles.Count, 0, path));
        }

        var copySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in DuplicateScanner.EnumerateFiles(copyRoot, walk, token))
            copySeen.Add(Path.GetRelativePath(copyRoot, path));

        progress?.Report(new ScanProgress(ScanPhase.Hashing, sourceFiles.Count, sourceFiles.Count, MeowsText.Current["purrge.comparing"]));

        var findings = new List<Finding>();
        long bytes = 0;
        var readInFull = 0;
        var done = 0;

        foreach (var sourcePath in sourceFiles)
        {
            token.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceRoot, sourcePath);
            var copyPath = Path.Combine(copyRoot, relative);
            copySeen.Remove(relative);

            var s = Describe(sourcePath);
            if (s is null)
            {
                findings.Add(new Finding(FindingKind.Unreadable, relative, sourcePath, copyPath, null, null, null, null));
                continue;
            }

            bytes += s.Value.Size;

            var c = File.Exists(copyPath) ? Describe(copyPath) : null;
            if (c is null)
            {
                findings.Add(new Finding(
                    File.Exists(copyPath) ? FindingKind.Unreadable : FindingKind.Missing,
                    relative, sourcePath, copyPath, s.Value.Size, null, s.Value.ModifiedUtc, null));
                continue;
            }

            var same = Judge(sourcePath, copyPath, s.Value, c.Value, options, ref readInFull);
            if (same is null)
            {
                findings.Add(new Finding(FindingKind.Unreadable, relative, sourcePath, copyPath,
                    s.Value.Size, c.Value.Size, s.Value.ModifiedUtc, c.Value.ModifiedUtc));
            }
            else if (same == false)
            {
                // Older by more than a filesystem's rounding, or it is not "older", it is "other".
                var copyIsOlder = c.Value.ModifiedUtc < s.Value.ModifiedUtc.AddSeconds(-2);
                findings.Add(new Finding(copyIsOlder ? FindingKind.Stale : FindingKind.Different,
                    relative, sourcePath, copyPath, s.Value.Size, c.Value.Size, s.Value.ModifiedUtc, c.Value.ModifiedUtc));
            }

            done++;
            if (done % 200 == 0)
            {
                progress?.Report(new ScanProgress(ScanPhase.Hashing, sourceFiles.Count, sourceFiles.Count - done,
                    $"{findings.Count} finding(s) so far"));
            }
        }

        // What the copy has that the source has not. Not wrong, and reported last.
        foreach (var relative in copySeen)
        {
            var copyPath = Path.Combine(copyRoot, relative);
            var c = Describe(copyPath);
            findings.Add(new Finding(FindingKind.Extra, relative, Path.Combine(sourceRoot, relative), copyPath,
                null, c?.Size, null, c?.ModifiedUtc));
        }

        var ordered = findings
            .OrderBy(f => f.Kind)
            .ThenByDescending(f => f.Size)
            .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        progress?.Report(new ScanProgress(ScanPhase.Done, sourceFiles.Count, 0, $"{ordered.Count} finding(s)"));
        return new CompareReport(sourceFiles.Count, bytes, readInFull, ordered);
    }

    /// <summary>
    /// The staged verdict for one pair. Sizes differ: different, no reading. Sizes agree and
    /// timestamps are trusted and agree: same, no reading. Otherwise the content decides.
    /// </summary>
    private static bool? Judge(string sourcePath, string copyPath, Described s, Described c, CompareOptions options, ref int readInFull)
    {
        if (s.Size != c.Size)
            return false;

        if (options.TrustTimestamps && SameMoment(s.ModifiedUtc, c.ModifiedUtc))
            return true;

        readInFull++;
        return ContentHash.Same(sourcePath, copyPath);
    }

    /// <summary>
    /// FAT rounds to two seconds and some copies land a tick apart, so "the same time" allows
    /// that much. Anything wider is a different time.
    /// </summary>
    public static bool SameMoment(DateTime a, DateTime b) => Math.Abs((a - b).TotalSeconds) <= 2;

    private readonly record struct Described(long Size, DateTime ModifiedUtc);

    private static Described? Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new Described(info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
