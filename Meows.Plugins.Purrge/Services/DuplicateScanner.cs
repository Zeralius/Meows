using Meows.Disk;
using System.Security.Cryptography;

namespace Meows.Plugins.Purrge.Services;

public sealed record ScanOptions(long MinimumBytes = 4096, bool SkipSystemFolders = true);

public enum ScanPhase
{
    Enumerating,
    Hashing,
    Done,
}

public sealed record ScanProgress(ScanPhase Phase, int FilesSeen, int Candidates, string Detail);

public sealed record DuplicateFile(string Path, long Size, DateTime CreatedUtc, DateTime ModifiedUtc);

public sealed record DuplicateSet(long Size, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>What you get back by keeping one copy.</summary>
    public long RedundantBytes => Size * (Files.Count - 1);
}

/// <summary>
/// Duplicate detection in three stages, because reading every file on a drive is not viable.
/// Each stage only looks at whatever survived the one before it.
///
///   1. group by exact size. A size with one file in it cannot be a duplicate, so we never
///      open that file at all. This throws away almost everything.
///   2. hash the first 64 KB of what is left, which is enough to separate most of it.
///   3. hash in full, only for whatever still collides.
/// </summary>
public sealed class DuplicateScanner
{
    private const int PartialBytes = 64 * 1024;

    public async Task<IReadOnlyList<DuplicateSet>> ScanAsync(
        string root,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        return await Task.Run(() => Scan(root, options, progress, token), token).ConfigureAwait(true);
    }

    private IReadOnlyList<DuplicateSet> Scan(
        string root,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        var bySize = new Dictionary<long, List<string>>();
        var seen = 0;

        foreach (var path in EnumerateFiles(root, options, token))
        {
            token.ThrowIfCancellationRequested();
            seen++;

            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                continue;
            }

            if (size < options.MinimumBytes)
                continue;

            if (!bySize.TryGetValue(size, out var list))
                bySize[size] = list = [];
            list.Add(path);

            if (seen % 500 == 0)
                progress?.Report(new ScanProgress(ScanPhase.Enumerating, seen, 0, path));
        }

        // A size with one file in it cannot contain a duplicate.
        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        var candidateCount = candidates.Sum(kv => kv.Value.Count);
        progress?.Report(new ScanProgress(ScanPhase.Hashing, seen, candidateCount, "Comparing content…"));

        var results = new List<DuplicateSet>();
        var hashed = 0;

        foreach (var (size, paths) in candidates)
        {
            token.ThrowIfCancellationRequested();

            foreach (var partialGroup in GroupBy(paths, p => HashOf(p, PartialBytes), token))
            {
                if (partialGroup.Count < 2)
                    continue;

                foreach (var fullGroup in GroupBy(partialGroup, p => HashOf(p, null), token))
                {
                    if (fullGroup.Count < 2)
                        continue;

                    var files = fullGroup
                        .Select(Describe)
                        .OfType<DuplicateFile>()
                        .ToList();

                    if (files.Count > 1)
                        results.Add(new DuplicateSet(size, files));
                }
            }

            hashed += paths.Count;
            progress?.Report(new ScanProgress(ScanPhase.Hashing, seen, candidateCount - hashed,
                $"{results.Count} duplicate set(s) so far"));
        }

        progress?.Report(new ScanProgress(ScanPhase.Done, seen, 0, $"{results.Count} duplicate set(s)"));
        return results.OrderByDescending(r => r.RedundantBytes).ToList();
    }

    /// <summary>Groups by whatever key you give it, dropping anything unreadable.</summary>
    private static List<List<string>> GroupBy(
        IEnumerable<string> paths,
        Func<string, string?> key,
        CancellationToken token)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var computed = key(path);
            if (computed is null)
                continue;
            if (!groups.TryGetValue(computed, out var list))
                groups[computed] = list = [];
            list.Add(path);
        }

        return groups.Values.ToList();
    }

    private static string? HashOf(string path, int? maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            using var sha = SHA256.Create();

            if (maxBytes is null)
                return Convert.ToHexString(sha.ComputeHash(stream));

            var buffer = new byte[maxBytes.Value];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception)
        {
            // Locked, gone, or unreadable. It just does not take part.
            return null;
        }
    }

    private static DuplicateFile? Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return new DuplicateFile(path, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Walked by hand rather than with EnumerateFiles, for two reasons: one unreadable folder
    /// should not kill the whole scan, and reparse points need skipping or a junction pointing
    /// at a parent sends this round forever.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root, ScanOptions options, CancellationToken token)
    {
        // DirectoryInfo objects rather than path strings, because a string has to be turned back
        // into one to walk it and that round trip is where Windows quietly drops a trailing space.
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = stack.Pop();

            foreach (var file in FolderWalk.Files(current))
                yield return file.FullName;

            foreach (var child in FolderWalk.Into(current, options.SkipSystemFolders))
                stack.Push(child);
        }
    }
}
