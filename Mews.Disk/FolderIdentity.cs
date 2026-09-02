namespace Mews.Disk;

/// <summary>What removing a folder would actually cost.</summary>
public enum FolderVerdict
{
    /// <summary>Nothing could be established. The honest default.</summary>
    Unknown,

    /// <summary>Build output or a cache. Whatever wrote it will write it again.</summary>
    Rebuildable,

    /// <summary>An application's own state. Removing it loses settings rather than disk space.</summary>
    ApplicationData,

    /// <summary>An installed game. Its launcher has to remove it, not you.</summary>
    Game,

    /// <summary>Looks like your own material rather than something a program made.</summary>
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
/// Answers the question every size scanner leaves the user holding: what put this here, is
/// anything still using it, and what breaks if it goes.
///
/// The answer is built from evidence on the disk rather than from a table of known folder names.
/// A table would need updating forever and would still be wrong for anything it had not heard of,
/// whereas what is inside a folder, who sits above it and whether a program has it open are true
/// of folders nobody has ever catalogued. A small set of conventions is consulted as well, but
/// only ones that have meant the same thing for a decade.
/// </summary>
public static class FolderInspector
{
    /// <summary>Enough files to characterise a folder without walking a games library.</summary>
    private const int SampleLimit = 400;

    /// <summary>How many files to actually try opening. Locks are the expensive evidence.</summary>
    private const int LockProbes = 12;

    /// <summary>
    /// Names that have meant the same thing for a decade and are not worth pretending to deduce.
    /// Deliberately short: this is a supplement to the evidence, not the mechanism.
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

        // Steam first, because it is the one case where the disk is not the best witness. The
        // manifest knows the real name and whether it has ever been launched.
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
    /// One of the user's own folders, according to Windows rather than to a guess about its name.
    /// Asking the system means it is still right when the folder has been moved to another drive
    /// or renamed in another language.
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
        // Your own folder outranks everything else. Documents sitting inside a profile does not
        // make it the profile's data, and nothing here should ever suggest removing it.
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
    /// The application a folder belongs to, worked out from where it sits rather than what it is
    /// called. A folder under Roaming, Local or LocalLow is named by the program that made it, and
    /// that is true of programs nobody has ever heard of.
    /// </summary>
    private static string? OwningApplication(DirectoryInfo directory)
    {
        // Temp lives under Local, so without this every scratch folder is confidently reported as
        // belonging to an application called Temp. Asked of the system rather than matched by
        // name, because TEMP can be pointed anywhere.
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

            // The application's own folder counts as much as anything under it. Roaming\Thunderbird
            // is precisely Thunderbird's data, and saying otherwise was an early mistake here.
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
        /// Documents and media rather than the many small files a program writes for itself. Not a
        /// certainty, which is why it only ever leads to "looks like".
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

            FileInfo[] files;
            try
            {
                files = current.GetFiles();
            }
            catch (Exception)
            {
                continue;
            }

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

            try
            {
                foreach (var child in current.GetDirectories())
                    if (WalkRules.ShouldDescend(child, skipSystemFolders: false))
                        stack.Push(child);
            }
            catch (Exception)
            {
            }
        }

        var top = kinds.OrderByDescending(k => k.Value).FirstOrDefault();
        return new Facts(seen, newest, top.Key ?? "", top.Value, locked, seen >= SampleLimit);
    }

    /// <summary>
    /// Whether a program has this file open. Asking for exclusive read is the cheapest honest
    /// test: a sharing violation means something else is holding it, and anything else means the
    /// question could not be answered, which is not the same as no.
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
            // No permission, or it vanished. Neither says anything about it being in use.
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
    /// Reads as a complaint about neglect or a warning about activity depending on which it is,
    /// because "nothing has been written here since today" is nonsense.
    ///
    /// The wording also has to survive the sample being cut short. Finding the newest write among
    /// 400 files out of thousands says something was written then, but says nothing whatever about
    /// nothing having been written since, and claiming otherwise here would put a sentence on
    /// screen that is simply untrue.
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
