using Meows.Plugins.Abstractions;

namespace Meows.Disk;

/// <summary>
/// The history line every plugin writes when it sends something to the Recycle Bin, and the way
/// back from it. One shape for all of them is what lets the shell's History tab put any of them
/// back, from Litter's downloads to Molt's caches, whether the plugin's tab is open or not:
/// the kind is <see cref="Kind"/>, and the paths are in the data under <c>paths</c>, one a line.
/// </summary>
public static class Recycled
{
    public const string Kind = "recycled";

    private const string PathsKey = "paths";

    /// <summary>
    /// Records what went. The subject is the one path when there is one, or what the plugin
    /// says about the lot; the paths themselves always go in the data.
    /// </summary>
    public static void Record(IMeowsStore store, IReadOnlyCollection<string> paths, string? subject = null,
        string? detail = null, IReadOnlyDictionary<string, string>? data = null)
    {
        if (paths.Count == 0)
            return;

        var all = data is null ? new Dictionary<string, string>() : new Dictionary<string, string>(data);
        all[PathsKey] = string.Join('\n', paths);
        store.Record(Kind, subject ?? (paths.Count == 1 ? paths.First() : $"{paths.Count}"), detail, all);
    }

    /// <summary>The paths a recycled line is about: its data, or its subject when that is a full path.</summary>
    public static IReadOnlyList<string> PathsOf(StoredEvent stored)
    {
        if (!string.Equals(stored.Kind, Kind, StringComparison.OrdinalIgnoreCase))
            return [];
        if (stored.Data.TryGetValue(PathsKey, out var joined))
            return joined.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return Path.IsPathFullyQualified(stored.Subject) ? [stored.Subject] : [];
    }

    /// <summary>
    /// What of it is still in the bin and could go back: in this user's bin on its own drive,
    /// and nothing in its place now. Newest first when the same path went more than once.
    /// </summary>
    public static IReadOnlyList<BinItem> StillInBin(StoredEvent stored)
    {
        var paths = PathsOf(stored);
        if (paths.Count == 0)
            return [];

        var sid = RecycleBinContents.CurrentUserSid();
        if (sid is null)
            return [];

        var found = new List<BinItem>();
        foreach (var drive in paths.GroupBy(p => Path.GetPathRoot(p) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (drive.Key.Length == 0)
                continue;
            var bin = RecycleBinContents.Read(Path.Combine(drive.Key, "$Recycle.Bin", sid));
            foreach (var path in drive)
            {
                if (bin.Where(i => string.Equals(i.OriginalPath, path, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(i => i.DeletedAt)
                        .FirstOrDefault() is { IsBlocked: false } item)
                    found.Add(item);
            }
        }
        return found;
    }

    /// <summary>Puts back what is still in the bin. Null when all of it went back, otherwise what did not.</summary>
    public static string? PutBack(StoredEvent stored)
    {
        var items = StillInBin(stored);
        if (items.Count == 0)
            return MeowsText.Current["disk.recycled.gone"];

        var failures = items.Select(RecycleBinContents.Restore).OfType<string>().ToList();
        return failures.Count == 0 ? null : string.Join(" ", failures);
    }
}
