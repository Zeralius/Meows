namespace Meows.Plugins.Mouser.Services;

/// <summary>
/// A file that only makes sense beside another one, and the rule saying what that other one is.
/// Every rule is named, shown and switched on its own, because the rules are where the false
/// positives live: a .gcode kept after its model was deleted is not rubbish, so that rule starts
/// off.
/// </summary>
public sealed record ShadowRule(string Id, string NameKey, string DetailKey, bool OnByDefault);

/// <summary>
/// Shadow, the other half of the original Mouser sketch. Everything else Mouser reports is judged
/// on its own; an orphan is only an orphan relative to something else, so each one is judged
/// against the folder it sits in, and when a rule cannot be sure the file keeps its place.
/// </summary>
public static class Shadow
{
    public static readonly ShadowRule Subtitles = new("subtitles", "mouser.shadow.subtitles", "mouser.detail.orphan.subtitles", true);
    public static readonly ShadowRule Sidecar = new("sidecar", "mouser.shadow.sidecar", "mouser.detail.orphan.sidecar", true);
    public static readonly ShadowRule DownloadJson = new("json", "mouser.shadow.json", "mouser.detail.orphan.json", true);
    public static readonly ShadowRule UnityMeta = new("meta", "mouser.shadow.meta", "mouser.detail.orphan.meta", true);
    public static readonly ShadowRule Gcode = new("gcode", "mouser.shadow.gcode", "mouser.detail.orphan.gcode", false);

    public static readonly IReadOnlyList<ShadowRule> Rules = [Subtitles, Sidecar, DownloadJson, UnityMeta, Gcode];

    public static IReadOnlyList<string> DefaultOn => Rules.Where(r => r.OnByDefault).Select(r => r.Id).ToList();

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv",
    };

    private static readonly HashSet<string> SubtitleKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx",
    };

    private static readonly HashSet<string> SidecarKinds = new(StringComparer.OrdinalIgnoreCase) { ".xmp", ".aae" };

    private static readonly HashSet<string> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ".stl", ".3mf", ".obj", ".step", ".stp", ".amf", ".ply",
    };

    /// <summary>Folders subtitles are kept in apart from their video, one level down.</summary>
    private static readonly HashSet<string> SubtitleFolders = new(StringComparer.OrdinalIgnoreCase) { "subs", "subtitles", "sub" };

    /// <summary>
    /// The orphans among one folder's files, each with the rule that says so and the name it was
    /// expecting beside it.
    /// </summary>
    public static IEnumerable<(string Path, ShadowRule Rule, string Expected)> In(
        string folder, IReadOnlyList<string> fileNames, IReadOnlySet<string> enabled)
    {
        if (enabled.Count == 0 || fileNames.Count == 0)
            yield break;

        var names = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        var stems = fileNames
            .GroupBy(n => Path.GetFileNameWithoutExtension(n), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(Path.GetExtension).ToList(), StringComparer.OrdinalIgnoreCase);
        var videos = fileNames.Where(n => Videos.Contains(Path.GetExtension(n))).ToList();

        foreach (var name in fileNames)
        {
            var extension = Path.GetExtension(name);
            var stem = Path.GetFileNameWithoutExtension(name);
            var found = Judge(folder, name, stem, extension, names, stems, videos, enabled);
            if (found is { } orphan)
                yield return (Path.Combine(folder, name), orphan.Rule, orphan.Expected);
        }
    }

    private static (ShadowRule Rule, string Expected)? Judge(
        string folder, string name, string stem, string extension,
        HashSet<string> names, Dictionary<string, List<string>> stems, List<string> videos, IReadOnlySet<string> enabled)
    {
        if (SubtitleKinds.Contains(extension) && enabled.Contains(Subtitles.Id))
        {
            // movie.srt, movie.en.srt and movie.en.forced.srt all belong to movie.mkv; a folder
            // with a single video owns whatever subtitles sit in it, whatever they are called;
            // and Subs\2_English.srt belongs to the film one folder up.
            if (videos.Count == 1)
                return null;
            if (videos.Select(Path.GetFileNameWithoutExtension).Any(v => stem.Equals(v, StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith(v + ".", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (SubtitleFolders.Contains(Path.GetFileName(folder)) && ParentHasVideo(folder))
                return null;
            return (Subtitles, BaseStem(stem));
        }

        if (SidecarKinds.Contains(extension) && enabled.Contains(Sidecar.Id))
        {
            // photo.xmp beside photo.cr2, or photo.cr2.xmp beside photo.cr2; IMG_0001.AAE beside IMG_0001.HEIC.
            if (names.Contains(stem))
                return null;
            if (stems.TryGetValue(stem, out var others) && others.Any(e => !SidecarKinds.Contains(e)))
                return null;
            return (Sidecar, stem);
        }

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) && enabled.Contains(DownloadJson.Id))
        {
            // Takeout's photo.jpg.json wants photo.jpg; yt-dlp's clip.info.json wants a clip.* that
            // is not json. Any other json is somebody's data and is left alone.
            var inner = Path.GetExtension(stem);
            if (inner.Equals(".info", StringComparison.OrdinalIgnoreCase))
            {
                var media = Path.GetFileNameWithoutExtension(stem);
                return stems.TryGetValue(media, out var kinds) && kinds.Any(e => !e.Equals(".json", StringComparison.OrdinalIgnoreCase))
                    ? null
                    : (DownloadJson, media);
            }
            if (IsMedia(inner))
                return names.Contains(stem) ? null : (DownloadJson, stem);
            return null;
        }

        if (extension.Equals(".meta", StringComparison.OrdinalIgnoreCase) && enabled.Contains(UnityMeta.Id))
        {
            // Unity writes one per asset and one per folder, and only inside a project's Assets or
            // Packages. Elsewhere .meta means something else to someone else.
            if (!InsideUnityProject(folder))
                return null;
            var partner = Path.Combine(folder, stem);
            return names.Contains(stem) || Directory.Exists(partner) ? null : (UnityMeta, stem);
        }

        if (extension.Equals(".gcode", StringComparison.OrdinalIgnoreCase) && enabled.Contains(Gcode.Id))
        {
            // Slicers name the output after the model, sometimes with the settings stuck on the end.
            var hasModel = stems.Any(s => s.Value.Any(Models.Contains) &&
                stem.StartsWith(s.Key, StringComparison.OrdinalIgnoreCase));
            return hasModel ? null : (Gcode, stem);
        }

        return null;
    }

    private static readonly HashSet<string> MediaKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".mp3", ".m4a",
    };

    private static bool IsMedia(string extension) => MediaKinds.Contains(extension);

    /// <summary>movie.en.forced for movie.en.forced.srt is shown as movie, the name that was missing.</summary>
    private static string BaseStem(string stem)
    {
        var dot = stem.IndexOf('.');
        return dot > 0 ? stem[..dot] : stem;
    }

    private static bool ParentHasVideo(string folder)
    {
        try
        {
            var parent = Path.GetDirectoryName(folder);
            return parent is not null && Directory.EnumerateFiles(parent).Any(f => Videos.Contains(Path.GetExtension(f)));
        }
        catch (Exception)
        {
            // Not knowing is not the same as knowing it is alone.
            return true;
        }
    }

    private static bool InsideUnityProject(string folder)
    {
        var parts = folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.Equals("Assets", StringComparison.OrdinalIgnoreCase) || p.Equals("Packages", StringComparison.OrdinalIgnoreCase));
    }
}
