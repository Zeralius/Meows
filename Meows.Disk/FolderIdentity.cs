namespace Meows.Disk;

/// <summary>What removing a folder would cost.</summary>
public enum FolderVerdict
{
    /// <summary>Could not work it out. The default, and a valid answer.</summary>
    Unknown,

    /// <summary>Build output or a cache. Whatever wrote it will write it again.</summary>
    Rebuildable,

    /// <summary>An application's own state. Removing it loses settings rather than disk space.</summary>
    ApplicationData,

    /// <summary>An installed game. Uninstall through its launcher, not by deleting.</summary>
    Game,

    /// <summary>Looks like the user's own files rather than something a program made.</summary>
    Yours,
}

public sealed record FolderIdentity(
    string Headline,
    FolderVerdict Verdict,
    string Advice,
    IReadOnlyList<string> Evidence,
    bool InUse)
{
    public static FolderIdentity Nothing { get; } = new(
        "Not sure what this is",
        FolderVerdict.Unknown,
        "Nothing here says what wrote this, so treat it as yours until you know otherwise.",
        [],
        false);
}

/// <summary>
/// Works out what a folder is: what put it there, whether anything still uses it, and what
/// breaks if it goes.
///
/// Driven by what is on disk rather than a lookup table of folder names. A table needs constant
/// updating and is still wrong for anything it has not heard of, whereas the contents, the parent
/// application and whether a file is open work for folders nobody has catalogued. There is a
/// small name list too, but only for conventions that have not changed in years.
/// </summary>
public static class FolderInspector
{
    /// <summary>Enough files to judge a folder by, without walking an entire games library.</summary>
    private const int SampleLimit = 400;

    /// <summary>How many files to try opening. This is the expensive check, so keep it small.</summary>
    private const int LockProbes = 12;

    /// <summary>
    /// Folder names with a settled meaning, where deducing it from contents would be silly.
    /// Kept short on purpose: this supplements the evidence, it is not the main mechanism.
    /// </summary>
    private static readonly Dictionary<string, string> KnownRebuildable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_modules"] = "installed npm packages",
        ["obj"] = "intermediate build output",
        ["bin"] = "compiled build output",
        ["target"] = "build output",
        ["__pycache__"] = "compiled Python bytecode",
        ["Library"] = "Unity's imported asset cache",
        ["Temp"] = "scratch space",
        [".gradle"] = "Gradle's cache",
        ["CachedData"] = "cached data",
        ["ShaderCache"] = "compiled shaders",
        ["shader_cache"] = "compiled shaders",
        ["GPUCache"] = "cached GPU data",
        ["Crashpad"] = "crash reports",
    };

    public static FolderIdentity Of(string path, CancellationToken token = default)
    {
        DirectoryInfo directory;
        try
        {
            directory = new DirectoryInfo(path);
            if (!directory.Exists)
                return FolderIdentity.Nothing;
        }
        catch (Exception)
        {
            return FolderIdentity.Nothing;
        }

        // Steam first. Its manifest knows the real name and the last played time, which beats
        // anything we could infer from the files.
        if (SteamLibrary.GameAt(directory.FullName) is { } game)
            return ForGame(game);

        var facts = Sample(directory, token);
        var evidence = new List<string>();

        if (facts.FileCount == 0)
            evidence.Add("No files inside it at all.");
        else
            evidence.Add(Composition(facts));

        if (facts.NewestWrite is { } newest)
            evidence.Add(Recency(newest, facts.Truncated));

        var mine = UserFolder(directory);
        if (mine is not null)
            evidence.Add($"Windows keeps this as your {mine} folder.");

        var owner = OwningApplication(directory);
        if (owner is not null)
            evidence.Add($"It sits inside {owner}'s own folder.");

        if (facts.InUse)
            evidence.Add("Something has a file in here open right now.");

        var known = Rebuildable(directory);
        if (known is not null)
            evidence.Add($"Folders named {directory.Name} hold {known}.");

        return Decide(directory, facts, mine, owner, known, evidence);
    }

    /// <summary>
    /// One of the user's own folders, according to Windows rather than a guess from the name.
    /// Asking the system keeps this right when the folder has been moved to another drive or is
    /// named in another language.
    /// </summary>
    private static string? UserFolder(DirectoryInfo directory)
    {
        var known = new (Environment.SpecialFolder Folder, string Name)[]
        {
            (Environment.SpecialFolder.MyDocuments, "Documents"),
            (Environment.SpecialFolder.MyPictures, "Pictures"),
            (Environment.SpecialFolder.MyVideos, "Videos"),
            (Environment.SpecialFolder.MyMusic, "Music"),
            (Environment.SpecialFolder.Desktop, "Desktop"),
        };

        foreach (var (folder, name) in known)
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && Same(path, directory.FullName))
                return name;
        }

        return null;
    }

    private static bool Same(string a, string b) =>
        a.TrimEnd(Path.DirectorySeparatorChar).Equals(
            b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static FolderIdentity ForGame(SteamGame game)
    {
        var played = game.PlayedUnknown
            ? "Steam does not record when it was last played."
            : game.NeverPlayed
                ? "Steam says it has never been launched."
                : $"Last played {Ago(game.LastPlayed!.Value)}.";

        return new FolderIdentity(
            $"{game.Name}, installed through Steam",
            FolderVerdict.Game,
            "Uninstall it through Steam. Deleting the folder by hand leaves Steam still believing " +
            "it is installed.",
            [played, $"Steam records it as {FolderSize.Humanise(game.SizeOnDisk)}."],
            false);
    }

    private static FolderIdentity Decide(
        DirectoryInfo directory,
        Facts facts,
        string? mine,
        string? owner,
        string? known,
        List<string> evidence)
    {
        // Beats everything else. Documents living inside the user profile does not make it
        // profile data, and we should never suggest deleting it.
        if (mine is not null)
        {
            return new FolderIdentity(
                $"Your {mine} folder",
                FolderVerdict.Yours,
                "This is one of your own folders. Look inside it for what to remove rather than " +
                "removing the folder.",
                evidence,
                facts.InUse);
        }

        if (known is not null)
        {
            return new FolderIdentity(
                $"{directory.Name}, which holds {known}",
                FolderVerdict.Rebuildable,
                facts.InUse
                    ? "It will be rebuilt if you remove it, but something is using it right now. " +
                      "Close that first."
                    : "Safe to remove. Whatever wrote it will write it again, which costs time " +
                      "rather than anything you cannot get back.",
                evidence,
                facts.InUse);
        }

        if (owner is not null)
        {
            return new FolderIdentity(
                $"{owner}'s own data",
                FolderVerdict.ApplicationData,
                "This is settings and state rather than something you put here. Removing it will " +
                "not usually break the program, but it will forget whatever was in here.",
                evidence,
                facts.InUse);
        }

        if (facts.FileCount > 0 && facts.LooksPersonal)
        {
            return new FolderIdentity(
                "Looks like your own material",
                FolderVerdict.Yours,
                "Nothing here says a program made this, and the contents read like documents or " +
                "media. Treat it as yours.",
                evidence,
                facts.InUse);
        }

        return FolderIdentity.Nothing with { Evidence = evidence, InUse = facts.InUse };
    }

    /// <summary>
    /// Which application a folder belongs to, from where it sits rather than what it is called.
    /// Anything under Roaming, Local or LocalLow is named after the program that created it,
    /// including programs we have never heard of.
    /// </summary>
    private static string? OwningApplication(DirectoryInfo directory)
    {
        // Temp lives under Local, so without this every scratch folder gets reported as
        // belonging to an app called Temp. Asked of the system because TEMP can point anywhere.
        if (IsUnder(Path.GetTempPath(), directory.FullName))
            return null;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root))
                continue;

            // The app's own folder counts too, not just things under it: Roaming\Thunderbird
            // is Thunderbird's data. Excluding it was a bug here at first.
            if (FirstSegmentUnder(root, directory.FullName) is { } name)
                return name;
        }

        return null;
    }

    private static bool IsUnder(string root, string path)
    {
        if (string.IsNullOrEmpty(root))
            return false;

        var normalised = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalised, StringComparison.OrdinalIgnoreCase) ||
               Same(root, path);
    }

    private static string? FirstSegmentUnder(string root, string path)
    {
        var normalisedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(normalisedRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = path[normalisedRoot.Length..];
        var segment = rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segment.Length > 0 ? segment[0] : null;
    }

    private static string? Rebuildable(DirectoryInfo directory) =>
        KnownRebuildable.TryGetValue(directory.Name, out var what) ? what : null;

    private sealed record Facts(int FileCount, DateTime? NewestWrite, string TopKind, int TopCount, bool InUse, bool Truncated)
    {
        /// <summary>
        /// Documents and media rather than the small files a program writes for itself. A guess,
        /// which is why the verdict it leads to only ever says "looks like".
        /// </summary>
        public bool LooksPersonal => PersonalKinds.Contains(TopKind);

        private static readonly HashSet<string> PersonalKinds = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".mp4", ".mkv", ".webm", ".mp3", ".flac",
            ".pdf", ".docx", ".xlsx", ".psd", ".blend", ".zip", ".cbz", ".rar", ".7z", ".epub",
        };
    }

    private static Facts Sample(DirectoryInfo directory, CancellationToken token)
    {
        var kinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;
        DateTime? newest = null;
        var probed = 0;
        var locked = false;

        var stack = new Stack<DirectoryInfo>();
        stack.Push(directory);

        while (stack.Count > 0 && seen < SampleLimit && !token.IsCancellationRequested)
        {
            var current = stack.Pop();

            var files = FolderWalk.Files(current);

            foreach (var file in files)
            {
                if (seen >= SampleLimit)
                    break;

                seen++;

                var extension = file.Extension.Length > 0 ? file.Extension : "(none)";
                kinds[extension] = kinds.GetValueOrDefault(extension) + 1;

                try
                {
                    if (newest is null || file.LastWriteTime > newest)
                        newest = file.LastWriteTime;
                }
                catch (Exception)
                {
                }

                if (!locked && probed < LockProbes)
                {
                    probed++;
                    locked = IsLocked(file);
                }
            }

            foreach (var child in FolderWalk.Into(current, skipSystemFolders: false))
                stack.Push(child);
        }

        var top = kinds.OrderByDescending(k => k.Value).FirstOrDefault();
        return new Facts(seen, newest, top.Key ?? "", top.Value, locked, seen >= SampleLimit);
    }

    /// <summary>
    /// Whether a program has this file open. Opening it with FileShare.None is the cheap test:
    /// a sharing violation is a yes, anything else means we could not tell, which is not a no.
    /// </summary>
    private static bool IsLocked(FileInfo file)
    {
        try
        {
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (Exception)
        {
            // No permission, or it vanished. Neither tells us whether it is in use.
            return false;
        }
    }

    private static string Composition(Facts facts)
    {
        var many = facts.FileCount >= SampleLimit ? $"at least {SampleLimit}" : facts.FileCount.ToString();
        var share = facts.FileCount == 0 ? 0 : facts.TopCount * 100 / facts.FileCount;

        return facts.TopKind is "" or "(none)"
            ? $"{many} files, mostly without an extension."
            : $"{many} files, {share}% of them {facts.TopKind}.";
    }

    /// <summary>
    /// Wording for how recently the folder was written to. Two cases, because "nothing has been
    /// written here since today" is nonsense.
    ///
    /// Also has to account for the sample stopping early. The newest write among 400 files out of
    /// thousands says something was written then; it says nothing about what happened since, so
    /// do not claim it did.
    /// </summary>
    private static string Recency(DateTime newest, bool truncated)
    {
        if ((DateTime.Now - newest).TotalDays < 2)
            return $"Something wrote to it {Ago(newest)}.";

        return truncated
            ? $"Of the files looked at, the most recent was written {Ago(newest)}."
            : $"Nothing has been written here since {Ago(newest)}.";
    }

    private static string Ago(DateTime when)
    {
        var days = (DateTime.Now - when).TotalDays;

        return days switch
        {
            < 0 => when.ToString("d MMMM yyyy"),
            < 1 => "today",
            < 2 => "yesterday",
            < 31 => $"{(int)days} days ago",
            < 365 => $"{(int)(days / 30)} months ago",
            _ => when.ToString("MMMM yyyy"),
        };
    }
}
