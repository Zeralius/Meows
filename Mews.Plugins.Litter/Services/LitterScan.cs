using Mews.Disk;

namespace Mews.Plugins.Litter.Services;

/// <summary>What a downloaded file appears to be, judged by its extension.</summary>
public enum LitterKind
{
    Installer,
    Archive,
    Image,
    Video,
    Audio,
    Document,
    Unfinished,
    Other,
}

/// <summary>How long it has been sitting there.</summary>
public enum LitterAge
{
    Today,
    ThisWeek,
    ThisMonth,
    Older,
}

public sealed record LitterItem(
    string Path,
    string Name,
    long Size,
    DateTime Modified,
    LitterKind Kind,
    LitterAge Age)
{
    public int Days => Math.Max(0, (int)(DateTime.Now - Modified).TotalDays);
}

/// <summary>
/// Reads a downloads folder and says what is in it. No judgement is applied here beyond naming
/// what a thing is and how old it is: what to do about it is the person's call, and a tool that
/// guesses tends to guess confidently and wrongly.
/// </summary>
public static class LitterScan
{
    private static readonly Dictionary<string, LitterKind> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".exe"] = LitterKind.Installer,
        [".msi"] = LitterKind.Installer,
        [".msix"] = LitterKind.Installer,
        [".appx"] = LitterKind.Installer,
        [".zip"] = LitterKind.Archive,
        [".7z"] = LitterKind.Archive,
        [".rar"] = LitterKind.Archive,
        [".gz"] = LitterKind.Archive,
        [".tar"] = LitterKind.Archive,
        [".cbz"] = LitterKind.Archive,
        [".jpg"] = LitterKind.Image,
        [".jpeg"] = LitterKind.Image,
        [".png"] = LitterKind.Image,
        [".gif"] = LitterKind.Image,
        [".webp"] = LitterKind.Image,
        [".bmp"] = LitterKind.Image,
        [".jfif"] = LitterKind.Image,
        [".avif"] = LitterKind.Image,
        [".mp4"] = LitterKind.Video,
        [".mkv"] = LitterKind.Video,
        [".avi"] = LitterKind.Video,
        [".mov"] = LitterKind.Video,
        [".webm"] = LitterKind.Video,
        [".mp3"] = LitterKind.Audio,
        [".flac"] = LitterKind.Audio,
        [".wav"] = LitterKind.Audio,
        [".ogg"] = LitterKind.Audio,
        [".m4a"] = LitterKind.Audio,
        [".pdf"] = LitterKind.Document,
        [".docx"] = LitterKind.Document,
        [".txt"] = LitterKind.Document,
        [".md"] = LitterKind.Document,
        [".epub"] = LitterKind.Document,

        // Downloads that never finished. Worth their own category because they are always junk.
        [".crdownload"] = LitterKind.Unfinished,
        [".part"] = LitterKind.Unfinished,
        [".partial"] = LitterKind.Unfinished,
        [".download"] = LitterKind.Unfinished,
    };

    public static LitterKind KindOf(string path) =>
        ByExtension.TryGetValue(System.IO.Path.GetExtension(path), out var kind) ? kind : LitterKind.Other;

    public static LitterAge AgeOf(DateTime modified, DateTime now) => (now - modified).TotalDays switch
    {
        < 1 => LitterAge.Today,
        < 7 => LitterAge.ThisWeek,
        < 30 => LitterAge.ThisMonth,
        _ => LitterAge.Older,
    };

    /// <summary>
    /// Everything one level down, plus folders as single entries. Downloads folders collect
    /// extracted folders as well as files, and a folder of a thousand files should count as one
    /// thing to deal with rather than a thousand.
    /// </summary>
    public static IReadOnlyList<LitterItem> Read(string folder, DateTime now)
    {
        var items = new List<LitterItem>();

        DirectoryInfo root;
        try
        {
            root = new DirectoryInfo(folder);
            if (!root.Exists)
                return items;
        }
        catch (Exception)
        {
            return items;
        }

        try
        {
            foreach (var file in root.EnumerateFiles())
            {
                var kind = KindOf(file.FullName);
                items.Add(new LitterItem(file.FullName, file.Name, Length(file), file.LastWriteTime,
                    kind, AgeOf(file.LastWriteTime, now)));
            }
        }
        catch (Exception)
        {
            // Unreadable folder. Whatever was gathered so far is still worth showing.
        }

        try
        {
            foreach (var directory in root.EnumerateDirectories())
            {
                if (!WalkRules.ShouldDescend(directory, skipSystemFolders: false))
                    continue;

                items.Add(new LitterItem(directory.FullName, directory.Name, FolderSize(directory),
                    directory.LastWriteTime, LitterKind.Other, AgeOf(directory.LastWriteTime, now)));
            }
        }
        catch (Exception)
        {
        }

        return items;
    }

    private static long Length(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Everything under a folder, so its row says what removing it would actually free.</summary>
    private static long FolderSize(DirectoryInfo folder)
    {
        var total = 0L;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(folder);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            try
            {
                foreach (var file in current.EnumerateFiles())
                    total += Length(file);
            }
            catch (Exception)
            {
                continue;
            }

            try
            {
                foreach (var child in current.EnumerateDirectories())
                    if (WalkRules.ShouldDescend(child, skipSystemFolders: false))
                        stack.Push(child);
            }
            catch (Exception)
            {
            }
        }

        return total;
    }

    public static string Humanise(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };

    public static string Describe(LitterAge age) => age switch
    {
        LitterAge.Today => "Today",
        LitterAge.ThisWeek => "This week",
        LitterAge.ThisMonth => "This month",
        _ => "Older",
    };
}
