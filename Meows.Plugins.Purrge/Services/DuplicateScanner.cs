using Meows.Plugins.Abstractions;
using Meows.Disk;

namespace Meows.Plugins.Purrge.Services;

/// <param name="OnlyCopiesOf">
/// Ask only about these files: every other size is skipped on the walk, and only sets holding
/// one of them come back. What a tab hands over when it wants to know "is there another copy
/// of this before I bin it", which is cheap because a drive has few files of exactly that size.
/// </param>
public sealed record ScanOptions(long MinimumBytes = 4096, bool SkipSystemFolders = true, IReadOnlyList<string>? OnlyCopiesOf = null);

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
    public async Task<IReadOnlyList<DuplicateSet>> ScanAsync(
        string root,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        return await Task.Run(() => Scan([root], options, progress, token), token).ConfigureAwait(true);
    }

    /// <summary>Several roots at once, which is what "anywhere on the machine" means for a handful of files.</summary>
    public async Task<IReadOnlyList<DuplicateSet>> ScanAsync(
        IReadOnlyList<string> roots,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        return await Task.Run(() => Scan(roots, options, progress, token), token).ConfigureAwait(true);
    }

    private IReadOnlyList<DuplicateSet> Scan(
        IReadOnlyList<string> roots,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        var bySize = new Dictionary<long, List<string>>();
        var seen = 0;

        // Asked about particular files: only their sizes can hold a copy, so nothing else is
        // even opened, and the files themselves are in the pool whether the walk meets them or not.
        HashSet<long>? wantedSizes = null;
        HashSet<string>? wanted = null;
        if (options.OnlyCopiesOf is { Count: > 0 } asked)
        {
            wantedSizes = [];
            wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in asked)
            {
                try
                {
                    var size = new FileInfo(path).Length;
                    wantedSizes.Add(size);
                    wanted.Add(Path.GetFullPath(path));
                    if (!bySize.TryGetValue(size, out var list))
                        bySize[size] = list = [];
                    list.Add(Path.GetFullPath(path));
                }
                catch (Exception)
                {
                }
            }
        }

        foreach (var path in roots.SelectMany(root => EnumerateFiles(root, options, token)))
        {
            token.ThrowIfCancellationRequested();
            seen++;
            if (wanted is not null && wanted.Contains(path))
                continue;

            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                continue;
            }

            if (size < options.MinimumBytes && wantedSizes is null)
                continue;
            if (wantedSizes is not null && !wantedSizes.Contains(size))
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
        progress?.Report(new ScanProgress(ScanPhase.Hashing, seen, candidateCount, MeowsText.Current["purrge.comparing"]));

        var results = new List<DuplicateSet>();
        var hashed = 0;

        foreach (var (size, paths) in candidates)
        {
            token.ThrowIfCancellationRequested();

            foreach (var partialGroup in GroupBy(paths, ContentHash.Partial, token))
            {
                if (partialGroup.Count < 2)
                    continue;

                foreach (var fullGroup in GroupBy(partialGroup, ContentHash.Full, token))
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

        if (wanted is not null)
            results = results.Where(r => r.Files.Any(f => wanted.Contains(f.Path))).ToList();

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
    internal static IEnumerable<string> EnumerateFiles(string root, ScanOptions options, CancellationToken token)
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
