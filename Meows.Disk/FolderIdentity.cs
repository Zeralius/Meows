using Meows.Plugins.Abstractions;

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
    /// <summary>
    /// Built fresh each time rather than held as one instance, because the language can change
    /// between two people asking for it and a cached one would still be in the old one.
    /// </summary>
    public static FolderIdentity Nothing => new(
        MeowsText.Current["disk.unknown.headline"],
        FolderVerdict.Unknown,
        MeowsText.Current["disk.unknown.advice"],
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
        ["node_modules"] = "disk.known.node_modules",
        ["obj"] = "disk.known.obj",
        ["bin"] = "disk.known.bin",
        ["target"] = "disk.known.target",
        ["__pycache__"] = "disk.known.pycache",
        ["Library"] = "disk.known.library",
        ["Temp"] = "disk.known.temp",
        [".gradle"] = "disk.known.gradle",
        ["CachedData"] = "disk.known.cacheddata",
        ["ShaderCache"] = "disk.known.shadercache",
        ["shader_cache"] = "disk.known.shadercache",
        ["GPUCache"] = "disk.known.gpucache",
        ["Crashpad"] = "disk.known.crashpad",
    };

    private static string Say(string key) => MeowsText.Current[key];

    private static string Say(string key, params object?[] values) => MeowsText.Current.Format(key, values);

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
            evidence.Add(Say("disk.evidence.nofiles"));
        else
            evidence.Add(Composition(facts));

        if (facts.NewestWrite is { } newest)
            evidence.Add(Recency(newest, facts.Truncated));

        var mine = UserFolder(directory);
        if (mine is not null)
            evidence.Add(Say("disk.evidence.userfolder", Say(mine)));

        var owner = OwningApplication(directory);
        if (owner is not null)
            evidence.Add(Say("disk.evidence.owner", owner));

        if (facts.InUse)
            evidence.Add(Say("disk.evidence.inuse"));

        var known = Rebuildable(directory);
        if (known is not null)
            evidence.Add(Say("disk.evidence.known", directory.Name, Say(known)));

        return Decide(directory, facts, mine, owner, known, evidence);
    }

    /// <summary>
    /// One of the user's own folders, according to Windows rather than a guess from the name.
    /// Asking the system keeps this right when the folder has been moved to another drive or is
    /// named in another language.
    /// </summary>
    private static string? UserFolder(DirectoryInfo directory)
    {
        // Keys rather than names. Windows already has its own translated name for each of
        // these, but it is the name on disk, and the sentence around it is ours.
        var known = new (Environment.SpecialFolder Folder, string Key)[]
        {
            (Environment.SpecialFolder.MyDocuments, "disk.folder.documents"),
            (Environment.SpecialFolder.MyPictures, "disk.folder.pictures"),
            (Environment.SpecialFolder.MyVideos, "disk.folder.videos"),
            (Environment.SpecialFolder.MyMusic, "disk.folder.music"),
            (Environment.SpecialFolder.Desktop, "disk.folder.desktop"),
        };

        foreach (var (folder, key) in known)
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && Same(path, directory.FullName))
                return key;
        }

        return null;
    }

    private static bool Same(string a, string b) =>
        a.TrimEnd(Path.DirectorySeparatorChar).Equals(
            b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static FolderIdentity ForGame(SteamGame game)
    {
        var played = game.PlayedUnknown
            ? Say("disk.game.playedunknown")
            : game.NeverPlayed
                ? Say("disk.game.neverplayed")
                : Say("disk.game.lastplayed", Ago(game.LastPlayed!.Value));

        return new FolderIdentity(
            Say("disk.game.headline", game.Name),
            FolderVerdict.Game,
            Say("disk.game.advice"),
            [played, Say("disk.game.size", FolderSize.Humanise(game.SizeOnDisk))],
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
                Say("disk.yours.headline", Say(mine)),
                FolderVerdict.Yours,
                Say("disk.yours.advice"),
                evidence,
                facts.InUse);
        }

        if (known is not null)
        {
            return new FolderIdentity(
                Say("disk.rebuildable.headline", directory.Name, Say(known)),
                FolderVerdict.Rebuildable,
                Say(facts.InUse ? "disk.rebuildable.advice.inuse" : "disk.rebuildable.advice"),
                evidence,
                facts.InUse);
        }

        if (owner is not null)
        {
            return new FolderIdentity(
                Say("disk.appdata.headline", owner),
                FolderVerdict.ApplicationData,
                Say("disk.appdata.advice"),
                evidence,
                facts.InUse);
        }

        if (facts.FileCount > 0 && facts.LooksPersonal)
        {
            return new FolderIdentity(
                Say("disk.personal.headline"),
                FolderVerdict.Yours,
                Say("disk.personal.advice"),
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
        var many = facts.FileCount >= SampleLimit
            ? Say("disk.count.atleast", SampleLimit)
            : facts.FileCount.ToString();
        var share = facts.FileCount == 0 ? 0 : facts.TopCount * 100 / facts.FileCount;

        return facts.TopKind is "" or "(none)"
            ? Say("disk.evidence.composition.noextension", many)
            : Say("disk.evidence.composition", many, share, facts.TopKind);
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
            return Say("disk.evidence.justwritten", Ago(newest));

        return Say(truncated ? "disk.evidence.newest.sampled" : "disk.evidence.newest", Ago(newest));
    }

    private static string Ago(DateTime when)
    {
        var days = (DateTime.Now - when).TotalDays;

        return days switch
        {
            // The two date formats follow the machine's own culture rather than the language
            // picked here, which is what everything else on this computer writes dates in.
            < 0 => when.ToString("d MMMM yyyy"),
            < 1 => Say("disk.ago.today"),
            < 2 => Say("disk.ago.yesterday"),
            < 31 => Say("disk.ago.days", (int)days),
            < 365 => Say("disk.ago.months", (int)(days / 30)),
            _ => when.ToString("MMMM yyyy"),
        };
    }
}
