using System.Text.RegularExpressions;
using Meows.Disk;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Screenshot.Services;

/// <summary>One watched folder: where, what to call it, and whether it is on.</summary>
public sealed class ShotFolder
{
    public string Path { get; set; } = "";

    public string Label { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>The last scan, as kept: enough for the Home line with no scan in memory.</summary>
public sealed class ShotSummary
{
    public DateTime ScannedUtc { get; set; }

    public int Shots { get; set; }

    public long Bytes { get; set; }

    public int Duplicates { get; set; }

    public long DuplicateBytes { get; set; }
}

/// <summary>One image found, with what the scan worked out about it.</summary>
public sealed class Shot
{
    public string Path { get; set; } = "";

    public string Group { get; set; } = "";

    public long Size { get; set; }

    public DateTime TakenUtc { get; set; }

    public string? Hash { get; set; }

    /// <summary>True for every copy but the newest of identical bytes.</summary>
    public bool IsDuplicate { get; set; }

    /// <summary>How many shots share this burst, or zero when it stands alone.</summary>
    public int Burst { get; set; }
}

/// <summary>
/// Finding screenshots and sorting them: where the popular tools keep them, what belongs to
/// which game, which are identical bytes, and which are bursts.
///
/// Reading only, never deleting: what goes is decided on the tab or asked for by a rule, and
/// always through the Recycle Bin.
/// </summary>
public static class Shots
{
    /// <summary>What counts as a screenshot. Recordings live beside them and are left alone.</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    /// <summary>Shots this close together in one group are one burst pressing the key again.</summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Where the popular tools keep screenshots, proposed with names. Only folders that exist
    /// are named; the rest is not the user's problem.
    ///
    /// The roots are taken as arguments rather than read from the environment, which is how a
    /// test hands it a fake profile. In the app they are the real profile and AppData folders
    /// with Steam's own libraries beside them.
    /// </summary>
    public static IReadOnlyList<(string Folder, string Label)> FindSources(
        string profileRoot, string appDataRoot, IReadOnlyList<string> steamLibraries)
    {
        var found = new List<(string Folder, string Label)>();
        void Add(string folder, string label)
        {
            try
            {
                if (folder.Length > 0 && Directory.Exists(folder) &&
                    !found.Any(f => string.Equals(f.Folder, folder, StringComparison.OrdinalIgnoreCase)))
                    found.Add((folder, label));
            }
            catch (Exception)
            {
                // A folder that cannot even be asked about is not a source.
            }
        }

        // Steam keeps per-game screenshots under each library's userdata folder.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var library in steamLibraries)
            try
            {
                foreach (var install in SteamLibrary.InstalledIn(library))
                    names.TryAdd(install.AppId, install.Name);
            }
            catch (Exception)
            {
                // A library on a drive that is not there today.
            }

        foreach (var library in steamLibraries)
        {
            string userdata;
            try
            {
                userdata = Path.Combine(library, "userdata");
                if (!Directory.Exists(userdata))
                    continue;
            }
            catch (Exception)
            {
                continue;
            }

            string[] users;
            try
            {
                users = Directory.GetDirectories(userdata);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var user in users)
            {
                string remote;
                try
                {
                    remote = Path.Combine(user, "760", "remote");
                    if (!Directory.Exists(remote))
                        continue;
                }
                catch (Exception)
                {
                    continue;
                }

                string[] apps;
                try
                {
                    apps = Directory.GetDirectories(remote);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var app in apps)
                {
                    var screenshots = Path.Combine(app, "screenshots");
                    var appId = Path.GetFileName(app);
                    var label = names.TryGetValue(appId, out var name) ? name : $"Steam {appId}";
                    Add(screenshots, label);
                }
            }
        }

        // Xbox Game Bar, Windows' own key, NVIDIA's default, ShareX's default, Minecraft.
        Add(Path.Combine(profileRoot, "Videos", "Captures"), "Game Bar");
        Add(Path.Combine(profileRoot, "Pictures", "Screenshots"), "Windows");
        Add(Path.Combine(profileRoot, "Videos"), "Videos");
        Add(Path.Combine(profileRoot, "Documents", "ShareX", "Screenshots"), "ShareX");
        foreach (var configured in ShareXFolders(profileRoot))
            Add(configured, "ShareX");
        Add(Path.Combine(appDataRoot, ".minecraft", "screenshots"), "Minecraft");

        return found;
    }

    /// <summary>
    /// A ShareX that was told to save elsewhere says so in its own config. The keys are tried in
    /// order and the first value that is a folder on disk wins; a config in any other shape is
    /// simply not a source. Read-only: nothing here writes.
    /// </summary>
    public static IReadOnlyList<string> ShareXFolders(string profileRoot)
    {
        string config;
        try
        {
            config = Path.Combine(profileRoot, "Documents", "ShareX", "ApplicationConfig.json");
            if (!File.Exists(config))
                return [];
        }
        catch (Exception)
        {
            return [];
        }

        string text;
        try
        {
            text = File.ReadAllText(config);
        }
        catch (Exception)
        {
            return [];
        }

        var found = new List<string>();
        foreach (var key in new[] { "PersonalFolder", "PersonalPath", "ScreenshotsPath", "SavePath", "ImageSavePath" })
        {
            Match match;
            try
            {
                match = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"([^\"]+)\"",
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                continue;
            }

            if (!match.Success)
                continue;
            var folder = match.Groups[1].Value.Replace("\\\\", "\\");
            try
            {
                if (folder.Length > 0 && Directory.Exists(folder) && !found.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    found.Add(folder);
            }
            catch (Exception)
            {
                // A path that cannot be asked about is not a source.
            }
        }

        return found;
    }

    /// <summary>
    /// Folds freshly found sources into the kept folders: new ones arrive switched on, kept ones
    /// keep their switch, and nothing kept is ever dropped because a drive is unplugged today.
    /// </summary>
    public static int MergeFolders(List<ShotFolder> kept, IReadOnlyList<(string Folder, string Label)> found)
    {
        var added = 0;
        foreach (var (folder, label) in found)
        {
            var known = kept.FirstOrDefault(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                kept.Add(new ShotFolder { Path = folder, Label = label, Enabled = true });
                added++;
            }
            else if (known.Label != label && string.IsNullOrWhiteSpace(known.Label))
            {
                known.Label = label;
            }
        }
        return added;
    }

    /// <summary>
    /// Every image under the switched-on folders, newest first within each group. Folders that
    /// are gone today give nothing rather than an error.
    /// </summary>
    public static List<Shot> Scan(IReadOnlyList<ShotFolder> folders)
    {
        var shots = new List<Shot>();
        // Folders nest (Captures lives inside Videos): one file is one shot, whichever folder
        // named it first.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders.Where(f => f.Enabled))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists)
                        continue;
                }
                catch (Exception)
                {
                    continue;
                }

                DateTime taken;
                try
                {
                    taken = File.GetLastWriteTimeUtc(file);
                }
                catch (Exception)
                {
                    continue;
                }

                string full;
                try
                {
                    full = Path.GetFullPath(file);
                }
                catch (Exception)
                {
                    continue;
                }
                if (!seen.Add(full))
                    continue;

                shots.Add(new Shot
                {
                    Path = full,
                    Group = folder.Label,
                    Size = info.Length,
                    TakenUtc = taken,
                });
            }
        }

        MarkDuplicates(shots);
        MarkBursts(shots);
        return shots
            .OrderBy(s => s.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(s => s.TakenUtc)
            .ToList();
    }

    /// <summary>
    /// Identical bytes, staged the cheap way: sizes first, then the first 64 KB, then the whole
    /// file only where that still agrees. All but the newest of each set are marked.
    /// </summary>
    public static void MarkDuplicates(List<Shot> shots)
    {
        foreach (var sizeGroup in shots.GroupBy(s => s.Size).Where(g => g.Count() > 1))
        {
            var partials = new Dictionary<string, List<Shot>>(StringComparer.Ordinal);
            foreach (var shot in sizeGroup)
            {
                var partial = ContentHash.Partial(shot.Path);
                if (partial is null)
                    continue;
                if (!partials.TryGetValue(partial, out var group))
                    partials[partial] = group = [];
                group.Add(shot);
            }

            foreach (var partial in partials.Values.Where(g => g.Count > 1))
            {
                var fulls = new Dictionary<string, List<Shot>>(StringComparer.Ordinal);
                foreach (var shot in partial)
                {
                    var full = ContentHash.Full(shot.Path);
                    if (full is null)
                        continue;
                    shot.Hash = full;
                    if (!fulls.TryGetValue(full, out var group))
                        fulls[full] = group = [];
                    group.Add(shot);
                }

                foreach (var full in fulls.Values.Where(g => g.Count > 1))
                {
                    // The newest stays; every copy after it is the duplicate.
                    foreach (var copy in full.OrderByDescending(s => s.TakenUtc).Skip(1))
                        copy.IsDuplicate = true;
                }
            }
        }
    }

    /// <summary>
    /// Bursts: runs in one group taken inside the window. Standing alone is zero, not one.
    /// </summary>
    public static void MarkBursts(List<Shot> shots)
    {
        foreach (var group in shots.GroupBy(s => s.Group))
        {
            var ordered = group.OrderBy(s => s.TakenUtc).ToList();
            var run = new List<Shot> { ordered[0] };
            void Close()
            {
                if (run.Count > 1)
                    foreach (var shot in run)
                        shot.Burst = run.Count;
                run.Clear();
            }

            foreach (var shot in ordered.Skip(1))
            {
                if (shot.TakenUtc - run[^1].TakenUtc <= BurstWindow)
                    run.Add(shot);
                else
                {
                    Close();
                    run.Add(shot);
                }
            }
            Close();
        }
    }

    /// <summary>Copies but the newest of every identical set: what "recycle duplicates" removes.</summary>
    public static IReadOnlyList<Shot> DuplicateVictims(IReadOnlyList<Shot> shots) =>
        shots.Where(s => s.IsDuplicate).OrderByDescending(s => s.TakenUtc).ToList();

    public static ShotSummary Summarise(IReadOnlyList<Shot> shots, DateTime scannedUtc)
    {
        var dups = shots.Where(s => s.IsDuplicate).ToList();
        return new ShotSummary
        {
            ScannedUtc = scannedUtc,
            Shots = shots.Count,
            Bytes = shots.Sum(s => s.Size),
            Duplicates = dups.Count,
            DuplicateBytes = dups.Sum(s => s.Size),
        };
    }

    /// <summary>The line under the header from the last scan: what there is, and what copies hold.</summary>
    public static string SummaryOf(ShotSummary? summary, IMeowsText text) =>
        summary is null || summary.Shots == 0
            ? ""
            : summary.Duplicates > 0
                ? text.Format("screenshot.summary.dups", summary.Shots, Humanise(summary.Bytes),
                    summary.Duplicates, Humanise(summary.DuplicateBytes))
                : text.Format("screenshot.summary.clear", summary.Shots, Humanise(summary.Bytes));

    /// <summary>The Home line from the last scan. Red while copies are holding room.</summary>
    public static Glance? GlanceOf(ShotSummary? summary, IMeowsText text)
    {
        var line = SummaryOf(summary, text);
        return line.Length == 0 ? null : new Glance(line, (summary?.Duplicates ?? 0) > 0);
    }

    /// <summary>Bytes as a person would say them. The shell's humaniser lives behind a view model; this is the standalone one.</summary>
    public static string Humanise(long bytes) =>
        bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        };
}
