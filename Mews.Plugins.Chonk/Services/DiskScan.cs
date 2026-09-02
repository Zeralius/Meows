using Mews.Disk;

namespace Mews.Plugins.Chonk.Services;

/// <summary>One thing taking up room: a folder, a file, or the small stuff rolled together.</summary>
public sealed class DiskEntry
{
    public DiskEntry(string path, string name, DiskEntryKind kind, DiskEntry? parent)
    {
        Path = path;
        Name = name;
        Kind = kind;
        Parent = parent;
    }

    public string Path { get; }

    public string Name { get; }

    public DiskEntryKind Kind { get; }

    public DiskEntry? Parent { get; }

    /// <summary>Everything underneath, for a folder. Its own size, for a file.</summary>
    public long Size { get; set; }

    /// <summary>Files underneath, so a folder can say what it is made of.</summary>
    public int FileCount { get; set; }

    public List<DiskEntry> Children { get; } = [];

    public bool IsFolder => Kind == DiskEntryKind.Folder;

    /// <summary>Only a real folder is worth opening. The rolled up row has nothing inside it.</summary>
    public bool CanDrillInto => Kind == DiskEntryKind.Folder;

    /// <summary>The rolled up row stands for many files, so it is not one thing you can remove.</summary>
    public bool CanDelete => Kind != DiskEntryKind.SmallFiles;
}

public enum DiskEntryKind
{
    Folder,
    File,

    /// <summary>Everything under the listing threshold in one folder, added up.</summary>
    SmallFiles,
}

public sealed record ScanOptions
{
    /// <summary>Files smaller than this are counted but not listed individually.</summary>
    public long ListFilesFrom { get; init; } = 1024 * 1024;

    public bool SkipSystemFolders { get; init; } = true;
}

public sealed record ScanProgress(int FoldersSeen, long BytesSeen, string Current);

/// <summary>
/// Measures where the room went. Sizes only: no file is ever opened, which is what makes this
/// far cheaper than a Purrge scan over the same tree, and the reason a whole drive is tolerable.
/// </summary>
public static class DiskScan
{
    /// <summary>How often to say where we are. Reporting every folder costs more than the walk.</summary>
    private const int ReportEvery = 200;

    public static DiskEntry Run(
        string root,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken token)
    {
        var full = System.IO.Path.GetFullPath(root);
        var top = new DiskEntry(full, Label(full), DiskEntryKind.Folder, null);

        // Discovery order, so a parent always appears before its children. Rolling the sizes
        // up is then just a walk backwards through this list, with no recursion to overflow
        // on a pathologically deep tree.
        var discovered = new List<DiskEntry> { top };

        // The DirectoryInfo is carried rather than rebuilt from the path at each step. Windows
        // strips a trailing space from a path string, so a folder genuinely named " " turns back
        // into its own parent the moment it round trips through a string, and the walk goes round
        // that pair forever. One really does exist, inside a game called TOK 2, and it took a
        // scan to 45 TB across five million folders on an eighteen terabyte machine.
        var stack = new Stack<(DiskEntry Entry, DirectoryInfo Directory)>();
        stack.Push((top, new DirectoryInfo(full)));

        var foldersSeen = 0;
        var bytesSeen = 0L;

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (current, directory) = stack.Pop();
            foldersSeen++;

            bytesSeen += MeasureFiles(current, directory, options);

            foreach (var (child, childDirectory) in SubFolders(current, directory, options))
            {
                current.Children.Add(child);
                discovered.Add(child);
                stack.Push((child, childDirectory));
            }

            if (progress is not null && foldersSeen % ReportEvery == 0)
                progress.Report(new ScanProgress(foldersSeen, bytesSeen, current.Path));
        }

        // Backwards, so every child has its final size before its parent asks for it.
        for (var i = discovered.Count - 1; i >= 1; i--)
        {
            var entry = discovered[i];
            if (entry.Parent is not { } parent)
                continue;

            parent.Size += entry.Size;
            parent.FileCount += entry.FileCount;
        }

        progress?.Report(new ScanProgress(foldersSeen, top.Size, full));
        return top;
    }

    /// <summary>
    /// Adds this folder's own files to it. Big ones are listed individually because they are
    /// the answer to "what can I remove"; the rest are rolled into one row so a folder of ten
    /// thousand thumbnails costs one entry rather than ten thousand.
    /// </summary>
    private static long MeasureFiles(DiskEntry folder, DirectoryInfo directory, ScanOptions options)
    {
        FileInfo[] files;
        try
        {
            files = directory.GetFiles();
        }
        catch (Exception)
        {
            // Unreadable is not fatal. The rest of the drive is still worth measuring.
            return 0;
        }

        var smallBytes = 0L;
        var smallCount = 0;
        var total = 0L;

        foreach (var file in files)
        {
            long length;
            try
            {
                length = file.Length;
            }
            catch (Exception)
            {
                continue;
            }

            total += length;
            folder.FileCount++;

            if (length >= options.ListFilesFrom)
            {
                folder.Children.Add(new DiskEntry(file.FullName, file.Name, DiskEntryKind.File, folder)
                {
                    Size = length,
                    FileCount = 1,
                });
            }
            else
            {
                smallBytes += length;
                smallCount++;
            }
        }

        if (smallCount > 0)
        {
            var label = smallCount == 1 ? "1 smaller file" : $"{smallCount} smaller files";
            folder.Children.Add(new DiskEntry(folder.Path, label, DiskEntryKind.SmallFiles, folder)
            {
                Size = smallBytes,
                FileCount = smallCount,
            });
        }

        folder.Size += total;
        return total;
    }

    private static List<(DiskEntry Entry, DirectoryInfo Directory)> SubFolders(
        DiskEntry folder, DirectoryInfo directory, ScanOptions options)
    {
        var found = new List<(DiskEntry, DirectoryInfo)>();

        DirectoryInfo[] directories;
        try
        {
            directories = directory.GetDirectories();
        }
        catch (Exception)
        {
            return found;
        }

        foreach (var child in directories)
        {
            if (!WalkRules.ShouldDescend(child, options.SkipSystemFolders))
                continue;

            if (!WalkRules.LeadsSomewhereNew(child, directory))
                continue;

            found.Add((new DiskEntry(child.FullName, child.Name, DiskEntryKind.Folder, folder), child));
        }

        return found;
    }

    /// <summary>A drive root has no name of its own, so show the root itself.</summary>
    private static string Label(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

        return name.Length > 0 ? name : path;
    }

    /// <summary>
    /// Takes an entry out of the tree after it has been removed from the disk, and gives every
    /// ancestor its size back. Cheaper and less surprising than rescanning the whole drive to
    /// find out about one deletion.
    /// </summary>
    public static void Forget(DiskEntry entry)
    {
        entry.Parent?.Children.Remove(entry);

        for (var ancestor = entry.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestor.Size -= entry.Size;
            ancestor.FileCount -= entry.FileCount;
        }
    }

    /// <summary>Human sizes. Disk tools that only speak bytes make you do arithmetic to read them.</summary>
    public static string Humanise(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024 / 1024:0.##} TB",
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };
}
