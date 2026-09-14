using System.Text.Json;
using System.Text.Json.Serialization;
using Meows.Disk;

namespace Meows.Plugins.WeighIn.Services;

/// <summary>One folder's total at the time of a reading, down to a fixed depth under the drive.</summary>
public sealed record FolderReading(string Path, long Size);

/// <summary>One drive at the time of a reading: the numbers Windows gives, and the folders under it.</summary>
public sealed record DriveReading(string Root, long Total, long Free, IReadOnlyList<FolderReading> Folders)
{
    public long Used => Total - Free;
}

/// <summary>Every drive, once, on a date. One file per reading under the plugin's data folder.</summary>
public sealed record Reading(DateTime At, IReadOnlyList<DriveReading> Drives)
{
    public DriveReading? Drive(string root) =>
        Drives.FirstOrDefault(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What grew, between two readings, for one drive.</summary>
public sealed record Growth(string Path, long Before, long After)
{
    public long Delta => After - Before;

    public bool IsNew => Before == 0 && After > 0;

    public bool IsGone => Before > 0 && After == 0;
}

/// <summary>
/// The readings on disk and the arithmetic between them. A reading is a walk of a drive with
/// the totals kept to a fixed depth, which is what makes a daily one affordable: the walk is
/// the same walk Chonk does, but what is kept is a few thousand numbers rather than every file,
/// and the history stays small enough never to become the thing filling the disk.
/// </summary>
public static class Readings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Takes a reading of the drives given, walking each and keeping folders to the depth.</summary>
    public static Reading Take(IReadOnlyList<string> roots, int depth, bool skipSystemFolders,
        Action<string>? report, CancellationToken token)
    {
        var drives = new List<DriveReading>();
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            report?.Invoke(root);

            long total = 0, free = 0;
            try
            {
                var info = new DriveInfo(root);
                total = info.TotalSize;
                free = info.AvailableFreeSpace;
            }
            catch (Exception)
            {
                // A drive that vanished between the list and the reading is left with zeros.
            }

            var folders = new List<FolderReading>();
            try
            {
                var tree = DiskScan.Run(root, new ScanOptions
                {
                    SkipSystemFolders = skipSystemFolders,
                    ListFilesFrom = long.MaxValue,
                }, null, token);
                Flatten(tree, 0, depth, folders);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Unreadable root; the drive's own numbers still count.
            }

            drives.Add(new DriveReading(root, total, free, folders));
        }

        return new Reading(DateTime.Now, drives);
    }

    private static void Flatten(DiskEntry entry, int level, int depth, List<FolderReading> into)
    {
        if (level > 0)
            into.Add(new FolderReading(entry.Path, entry.Size));
        if (level >= depth)
            return;
        foreach (var child in entry.Children.Where(c => c.IsFolder))
            Flatten(child, level + 1, depth, into);
    }

    // ---- on disk ----

    public static string FileFor(string folder, DateTime at) => Path.Combine(folder, $"{at:yyyy-MM-dd}.json");

    public static void Save(string folder, Reading reading)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FileFor(folder, reading.At), JsonSerializer.Serialize(reading, Json));
    }

    /// <summary>Every reading kept, oldest first. One that cannot be read is skipped rather than fatal.</summary>
    public static IReadOnlyList<Reading> Load(string folder)
    {
        if (!Directory.Exists(folder))
            return [];

        var readings = new List<Reading>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var reading = JsonSerializer.Deserialize<Reading>(File.ReadAllText(file), Json);
                if (reading is not null)
                    readings.Add(reading);
            }
            catch (Exception)
            {
            }
        }

        return readings.OrderBy(r => r.At).ToList();
    }

    /// <summary>Keeps the last so many readings, so the history never becomes the thing filling the disk.</summary>
    public static int Prune(string folder, int keep)
    {
        if (!Directory.Exists(folder))
            return 0;
        var files = Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var gone = 0;
        foreach (var file in files.Take(Math.Max(0, files.Count - keep)))
        {
            try
            {
                File.Delete(file);
                gone++;
            }
            catch (Exception)
            {
            }
        }
        return gone;
    }

    // ---- the arithmetic ----

    /// <summary>The newest reading at or before a moment, or null when there is none that old.</summary>
    public static Reading? Before(IReadOnlyList<Reading> readings, DateTime moment) =>
        readings.Where(r => r.At <= moment).OrderByDescending(r => r.At).FirstOrDefault();

    /// <summary>
    /// What changed on one drive between two readings, biggest movement first. Only the folders
    /// that moved by at least the threshold are listed, which keeps a folder that grew by a
    /// megabyte from being the answer to "where did 200 GB go".
    /// </summary>
    public static IReadOnlyList<Growth> Compare(DriveReading? before, DriveReading after, long threshold)
    {
        var previous = (before?.Folders ?? []).ToDictionary(f => f.Path, f => f.Size, StringComparer.OrdinalIgnoreCase);
        var growth = new List<Growth>();

        foreach (var folder in after.Folders)
        {
            var was = previous.GetValueOrDefault(folder.Path);
            if (Math.Abs(folder.Size - was) >= threshold)
                growth.Add(new Growth(folder.Path, was, folder.Size));
            previous.Remove(folder.Path);
        }

        // Folders that were there and are not any more.
        foreach (var (path, size) in previous)
        {
            if (size >= threshold)
                growth.Add(new Growth(path, size, 0));
        }

        return growth.OrderByDescending(g => Math.Abs(g.Delta)).ToList();
    }

    /// <summary>
    /// The folders that explain a drive's growth: the deepest ones that moved, without the
    /// parents that only moved because their children did. A folder whose growth is accounted
    /// for by listed children is left out, so the answer is "Steam\steamapps\common" rather than
    /// "Steam" and "Steam\steamapps" and "Steam\steamapps\common" three times over.
    /// </summary>
    public static IReadOnlyList<Growth> Responsible(IReadOnlyList<Growth> growth, int take)
    {
        var kept = new List<Growth>();
        foreach (var candidate in growth)
        {
            // The listed folders directly under this one: a grandchild that is also listed is
            // already inside its parent's number, and counting both would explain it twice.
            var children = growth
                .Where(g => g != candidate && IsUnder(g.Path, candidate.Path))
                .Where(g => !growth.Any(o => o != g && o != candidate && IsUnder(g.Path, o.Path) && IsUnder(o.Path, candidate.Path)))
                .ToList();
            var explained = children.Sum(c => c.Delta);
            // Keep it only if what is left after the children is still most of it.
            if (children.Count == 0 || Math.Abs(candidate.Delta - explained) > Math.Abs(candidate.Delta) / 2)
                kept.Add(candidate);
        }
        return kept.Take(take).ToList();
    }

    private static bool IsUnder(string path, string parent)
    {
        var p = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    public static string Humanise(long bytes) => FolderSize.Humanise(Math.Abs(bytes));

    public static string Signed(long bytes) => bytes switch
    {
        > 0 => "+" + Humanise(bytes),
        < 0 => "−" + Humanise(bytes),
        _ => "±0",
    };
}
