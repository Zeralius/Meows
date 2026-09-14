using Meows.Disk;

namespace Meows.Plugins.Catnip.Services;

/// <summary>One file and the three stamps that say what happened to it.</summary>
public sealed record NeglectedFile(string Path, long Size, DateTime Created, DateTime Written, DateTime Accessed)
{
    /// <summary>When it arrived: written or created, whichever is later, since a copy is created after it was written.</summary>
    public DateTime Arrived => Created > Written ? Created : Written;

    /// <summary>
    /// Not opened since it arrived. Windows stamps the access time when a file is written or
    /// copied, and updates it at most about once an hour afterwards, so an access inside that
    /// hour is indistinguishable from the arrival and is treated as none.
    /// </summary>
    public bool NeverOpened => Accessed <= Arrived.AddHours(1);

    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>What a walk found, and what it skipped.</summary>
public sealed record NeglectReport(IReadOnlyList<NeglectedFile> Files, int Folders, int Unreadable);

/// <summary>
/// The walk that reads the one stamp nothing else reads. Sizes and dates come from directory
/// metadata, so nothing is opened and the stamps stay as they were; the other scanners in
/// Meows put the stamp back after a read, through <see cref="AccessTime"/>, for the same reason.
/// </summary>
public static class Neglect
{
    public static NeglectReport Scan(IReadOnlyList<string> roots, bool skipSystemFolders, long minBytes,
        Action<string>? report, CancellationToken token)
    {
        var files = new List<NeglectedFile>();
        var folders = 0;
        var unreadable = 0;

        foreach (var root in roots)
        {
            var directory = new DirectoryInfo(root);
            if (!directory.Exists)
                continue;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(directory);

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = pending.Pop();
                report?.Invoke(current.FullName);
                folders++;

                if (!FolderWalk.CanRead(current))
                {
                    unreadable++;
                    continue;
                }

                foreach (var file in FolderWalk.Files(current))
                {
                    try
                    {
                        if (file.Length < minBytes)
                            continue;
                        files.Add(new NeglectedFile(file.FullName, file.Length, file.CreationTime, file.LastWriteTime, file.LastAccessTime));
                    }
                    catch (Exception)
                    {
                        // A file that vanished mid-walk, or one whose stamps cannot be read.
                    }
                }

                foreach (var child in FolderWalk.Into(current, skipSystemFolders))
                    pending.Push(child);
            }
        }

        return new NeglectReport(files, folders, unreadable);
    }

    /// <summary>The ones worth showing: untouched for the window, never opened if asked, least recently touched first.</summary>
    public static IReadOnlyList<NeglectedFile> Sift(IReadOnlyList<NeglectedFile> files, bool onlyNeverOpened, int olderThanDays, DateTime now)
    {
        var cutoff = now.AddDays(-Math.Max(0, olderThanDays));
        return files
            .Where(f => f.Accessed <= cutoff)
            .Where(f => !onlyNeverOpened || f.NeverOpened)
            .OrderBy(f => f.Accessed)
            .ThenByDescending(f => f.Size)
            .ToList();
    }

    public static string Humanise(long bytes) => FolderSize.Humanise(bytes);

    /// <summary>"3 days", "5 weeks", "7 months", "2 years": how long since a stamp.</summary>
    public static string Ago(DateTime stamp, DateTime now, Func<string, string> text)
    {
        var days = Math.Max(0, (now - stamp).TotalDays);
        return days switch
        {
            < 1 => text("catnip.ago.today"),
            < 14 => string.Format(text("catnip.ago.days"), (int)days),
            < 60 => string.Format(text("catnip.ago.weeks"), (int)(days / 7)),
            < 700 => string.Format(text("catnip.ago.months"), (int)(days / 30.4)),
            _ => string.Format(text("catnip.ago.years"), (int)(days / 365.25)),
        };
    }
}
