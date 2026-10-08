using Meows.Disk;
using Meows.Media;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Purrge.Services;

/// <summary>One picture in a group of look-alikes, with its own size, since near copies differ by definition.</summary>
/// <param name="Distance">How many of the 64 bits differ from the group's best copy; zero for the best copy itself.</param>
public sealed record LookalikeFile(string Path, long Size, int Width, int Height, DateTime ModifiedUtc, int Distance)
{
    public long Pixels => (long)Width * Height;

    /// <summary>A video's length, set only for a video; a group is all pictures or all videos, never both.</summary>
    public TimeSpan? Duration { get; init; }

    public bool IsVideo => Duration is not null;
}

/// <summary>
/// Pictures that look the same without being the same bytes: the same image saved twice at
/// different sizes, re-encoded by a site, converted from PNG to JPEG. The first file is the one
/// suggested to keep: the most pixels, then the most bytes, then the oldest.
/// </summary>
public sealed record LookalikeSet(IReadOnlyList<LookalikeFile> Files)
{
    public LookalikeFile Best => Files[0];

    /// <summary>What the others take up, which is what could come back if the best one were kept.</summary>
    public long OthersBytes => Files.Skip(1).Sum(f => f.Size);
}

/// <summary>
/// Purrge's third question, apart from the other two on purpose. An exact duplicate is a fact:
/// identical bytes. A look-alike is an opinion: two pages of one comic can score as close as two
/// copies of one page. So the results are never mixed with exact duplicates, and the threshold
/// defaults to boring.
///
/// Groups are stars, never chains: each group is one picture and whatever is within the
/// threshold of that picture, so A like B and B like C does not make A like C.
/// </summary>
public sealed class LookalikeScanner
{
    /// <summary>The default: four bits of sixty-four. Tight on purpose; a finder that is exciting is about to lose something.</summary>
    public const int DefaultThreshold = 4;

    public Task<IReadOnlyList<LookalikeSet>> ScanAsync(string root, ScanOptions options, int threshold,
        IProgress<ScanProgress>? progress, CancellationToken token) =>
        Task.Run(() => Scan(root, options, threshold, progress, token), token);

    public static IReadOnlyList<LookalikeSet> Scan(string root, ScanOptions options, int threshold,
        IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var pictures = new List<(string Path, long Size, DateTime Modified)>();
        var seen = 0;
        foreach (var path in DuplicateScanner.EnumerateFiles(root, options, token))
        {
            token.ThrowIfCancellationRequested();
            seen++;
            if (!Thumbnails.IsRenderable(path))
                continue;
            try
            {
                var info = new FileInfo(path);
                if (info.Length >= options.MinimumBytes)
                    pictures.Add((path, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception)
            {
            }
            if (seen % 500 == 0)
                progress?.Report(new ScanProgress(ScanPhase.Enumerating, seen, 0, path));
        }

        // Every picture decoded, small, once. The access time goes back afterwards, so Catnip's
        // "never opened" still means never opened by a person.
        var looks = new (string Path, long Size, DateTime Modified, Look Look)?[pictures.Count];
        var done = 0;
        Parallel.For(0, pictures.Count, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
        }, i =>
        {
            var (path, size, modified) = pictures[i];
            var look = AccessTime.Preserving(path, () => PerceptualHash.Of(path));
            if (look is { } found)
                looks[i] = (path, size, modified, found);
            var count = Interlocked.Increment(ref done);
            if (count % 50 == 0)
                progress?.Report(new ScanProgress(ScanPhase.Hashing, seen, pictures.Count - count, MeowsText.Current.Format("purrge.lookalike.progress", count, pictures.Count)));
        });

        return Group(looks.Where(l => l is not null).Select(l => l!.Value).ToList(), threshold, token);
    }

    /// <summary>
    /// The same question of videos, through ffmpeg: five frames each, at the same fractions of
    /// their length, and lengths that agree. Asked apart from pictures and never mixed with them.
    /// </summary>
    public Task<IReadOnlyList<LookalikeSet>> ScanVideosAsync(string root, ScanOptions options, int threshold, (string Ffmpeg, string Ffprobe) tools,
        IProgress<ScanProgress>? progress, CancellationToken token) =>
        Task.Run(() => ScanVideos(root, options, threshold, tools, progress, token), token);

    public static IReadOnlyList<LookalikeSet> ScanVideos(string root, ScanOptions options, int threshold, (string Ffmpeg, string Ffprobe) tools,
        IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var videos = new List<(string Path, long Size, DateTime Modified)>();
        foreach (var path in DuplicateScanner.EnumerateFiles(root, options, token))
        {
            token.ThrowIfCancellationRequested();
            if (!VideoLooks.IsVideo(path))
                continue;
            try
            {
                var info = new FileInfo(path);
                if (info.Length >= options.MinimumBytes)
                    videos.Add((path, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception)
            {
            }
        }

        // Decoding frames is the slow part; two at a time keeps the machine usable.
        var looks = new (string Path, long Size, DateTime Modified, VideoLook Look)?[videos.Count];
        var done = 0;
        Parallel.For(0, videos.Count, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 2 }, i =>
        {
            var (path, size, modified) = videos[i];
            var look = AccessTime.Preserving(path, () => VideoLooks.Of(path, tools, token));
            if (look is not null)
                looks[i] = (path, size, modified, look);
            var count = Interlocked.Increment(ref done);
            progress?.Report(new ScanProgress(ScanPhase.Hashing, videos.Count, videos.Count - count, MeowsText.Current.Format("purrge.lookalike.progress.video", count, videos.Count)));
        });

        return GroupVideos(looks.Where(l => l is not null).Select(l => l!.Value).ToList(), threshold, token);
    }

    /// <summary>Stars, as for pictures: the most pixels first, each group around the copy worth keeping.</summary>
    public static IReadOnlyList<LookalikeSet> GroupVideos(IReadOnlyList<(string Path, long Size, DateTime Modified, VideoLook Look)> looks, int threshold, CancellationToken token = default)
    {
        var order = looks
            .OrderByDescending(l => l.Look.Pixels)
            .ThenByDescending(l => l.Size)
            .ThenBy(l => l.Modified)
            .ToList();
        var taken = new bool[order.Count];
        var sets = new List<LookalikeSet>();

        static LookalikeFile Video((string Path, long Size, DateTime Modified, VideoLook Look) l, int distance) =>
            new(l.Path, l.Size, l.Look.Width, l.Look.Height, l.Modified, distance) { Duration = l.Look.Duration };

        for (var i = 0; i < order.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (taken[i])
                continue;
            var centre = order[i];
            var members = new List<LookalikeFile> { Video(centre, 0) };
            for (var j = i + 1; j < order.Count; j++)
            {
                if (taken[j])
                    continue;
                if (VideoLooks.Distance(centre.Look, order[j].Look) is not { } distance || distance > threshold)
                    continue;
                if (centre.Size == order[j].Size && ContentHash.Full(centre.Path) is { } x && x == ContentHash.Full(order[j].Path))
                    continue;
                members.Add(Video(order[j], distance));
                taken[j] = true;
            }
            if (members.Count > 1)
            {
                taken[i] = true;
                sets.Add(new LookalikeSet(members));
            }
        }
        return sets.OrderByDescending(s => s.OthersBytes).ToList();
    }

    /// <summary>
    /// The star grouping. Pictures are taken best first, so each group forms around the copy
    /// worth keeping, and a picture already in a group is not offered to another.
    /// </summary>
    public static IReadOnlyList<LookalikeSet> Group(IReadOnlyList<(string Path, long Size, DateTime Modified, Look Look)> looks, int threshold, CancellationToken token = default)
    {
        var order = looks
            .OrderByDescending(l => l.Look.Pixels)
            .ThenByDescending(l => l.Size)
            .ThenBy(l => l.Modified)
            .ToList();
        var taken = new bool[order.Count];
        var sets = new List<LookalikeSet>();

        for (var i = 0; i < order.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (taken[i])
                continue;

            var centre = order[i];
            var members = new List<LookalikeFile> { File(centre, 0) };
            for (var j = i + 1; j < order.Count; j++)
            {
                if (taken[j])
                    continue;
                var distance = PerceptualHash.Distance(centre.Look.Hash, order[j].Look.Hash);
                if (distance > threshold || SamePicture(centre, order[j]))
                    continue;
                members.Add(File(order[j], distance));
                taken[j] = true;
            }

            if (members.Count > 1)
            {
                taken[i] = true;
                sets.Add(new LookalikeSet(members));
            }
        }

        return sets.OrderByDescending(s => s.OthersBytes).ToList();
    }

    /// <summary>
    /// Byte-for-byte copies are the duplicate scan's business and a fact, not an opinion; they
    /// are left out here so the two lists never say the same thing in two voices.
    /// </summary>
    private static bool SamePicture((string Path, long Size, DateTime Modified, Look Look) a, (string Path, long Size, DateTime Modified, Look Look) b) =>
        a.Size == b.Size && a.Look.Hash == b.Look.Hash && ContentHash.Full(a.Path) is { } x && x == ContentHash.Full(b.Path);

    private static LookalikeFile File((string Path, long Size, DateTime Modified, Look Look) l, int distance) =>
        new(l.Path, l.Size, l.Look.Width, l.Look.Height, l.Modified, distance);
}
