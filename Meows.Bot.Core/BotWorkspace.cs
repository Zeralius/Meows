using System.Text.Json;

namespace Meows.Bot;

/// <summary>Reading and writing a telegram-posting-bot checkout on disk.</summary>
public sealed class BotWorkspace
{
    public BotWorkspace(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }

    public string ConfigPath => Path.Combine(Root, "config.json");

    public string BotScriptPath => Path.Combine(Root, "bot.py");

    public string EnvPath => Path.Combine(Root, ".env");

    public bool LooksValid => File.Exists(ConfigPath) && File.Exists(BotScriptPath);

    /// <summary>Is there a token? We never read the value itself, only whether one is set.</summary>
    public bool HasToken
    {
        get
        {
            try
            {
                if (!File.Exists(EnvPath))
                    return false;

                foreach (var line in File.ReadLines(EnvPath))
                {
                    var trimmed = line.TrimStart();
                    if (!trimmed.StartsWith("BOT_TOKEN=", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var parts = trimmed.Split('=', 2);
                    return parts.Length == 2 && parts[1].Trim().Length > 0;
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Where is the bot? A saved path if we have one, otherwise look for a
    /// telegram-posting-bot folder above the exe or the working directory.
    /// </summary>
    public static string? Probe(string? saved)
    {
        if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
            return Path.GetFullPath(saved);

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "telegram-posting-bot");
                if (File.Exists(Path.Combine(candidate, "bot.py")))
                    return candidate;
                dir = dir.Parent;
            }
        }

        return null;
    }

    public BotConfig LoadConfig()
    {
        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<BotConfig>(json, BotConfig.JsonOptions) ?? new BotConfig();
    }

    /// <summary>Temp file then move, the same way bot.py writes its own state.</summary>
    public void SaveConfig(BotConfig config)
    {
        var json = JsonSerializer.Serialize(config, BotConfig.JsonOptions);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    /// <summary>
    /// config.json uses forward slashes. Normalising keeps the display tidy and makes
    /// "groups/x" and "groups\x" compare equal, which the clash check relies on.
    /// </summary>
    public string GroupFolder(GroupConfig group)
    {
        var combined = Path.IsPathRooted(group.Folder) ? group.Folder : Path.Combine(Root, group.Folder);
        try
        {
            return Path.GetFullPath(combined);
        }
        catch (Exception)
        {
            // Let the validator report a bad path. Not worth throwing here.
            return combined;
        }
    }

    public string ToSendFolder(GroupConfig group) => Path.Combine(GroupFolder(group), "To_Send");

    public string AlreadySentFolder(GroupConfig group) => Path.Combine(GroupFolder(group), "Already_Sent");

    /// <summary>
    /// Where a file goes when it turns out to be one this group already has.
    ///
    /// Beside the two the bot reads rather than inside them. The bot lists a queue with
    /// iterdir(), so a folder within it would be passed over today, but "today" is doing a lot of
    /// work in that sentence: one change to a recursive walk and everything set aside here would
    /// be posted. Kept where nothing is looking, the question never arises.
    /// </summary>
    public string DuplicatesFolder(GroupConfig group) => Path.Combine(GroupFolder(group), "Duplicates");

    public IReadOnlyList<string> Scan(string folder, bool recursive = false)
    {
        if (!Directory.Exists(folder))
            return [];

        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.EnumerateFiles(folder, "*", option)
                .Where(MediaRules.IsPostable)
                .OrderBy(p => Path.GetFileName(p) ?? p, Comparer<string>.Create(MediaRules.CompareNatural))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Our version of bot.py's get_next_media. Queue ordered by mtime per post_order, and
    /// when it is empty, a random pick from Already_Sent that stays put.
    ///
    /// If this ever disagrees with the bot, this is the thing that is wrong.
    /// </summary>
    public NextUp ResolveNextUp(GroupConfig group)
    {
        var order = (group.PostOrder ?? "oldest").ToLowerInvariant();
        var count = Math.Max(1, group.FilesPerPost ?? 1);
        var queue = Scan(ToSendFolder(group));

        if (queue.Count == 0)
        {
            var archive = Scan(AlreadySentFolder(group), recursive: true);
            return archive.Count == 0
                ? new NextUp(NextUpKind.Nothing, [], count)
                : new NextUp(NextUpKind.FallbackRandom, [], count);
        }

        if (order == "random")
            return new NextUp(NextUpKind.RandomAtPostTime, [], count);

        var byTime = queue
            .Select(p => (Path: p, Time: SafeWriteTime(p)))
            .OrderBy(x => x.Time)
            .Select(x => x.Path)
            .ToList();

        if (order == "newest")
            byTime.Reverse();

        return new NextUp(NextUpKind.Known, byTime.Take(count).ToList(), count);
    }

    private static DateTime SafeWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.MaxValue;
        }
    }
}

public enum NextUpKind
{
    /// <summary>We know exactly which files go next.</summary>
    Known,

    /// <summary>post_order is random, so the bot picks at post time. Nothing to show.</summary>
    RandomAtPostTime,

    /// <summary>Queue is empty, so the bot re-posts something from the archive.</summary>
    FallbackRandom,

    /// <summary>Nothing anywhere. This group will not post at all.</summary>
    Nothing,
}

public sealed record NextUp(NextUpKind Kind, IReadOnlyList<string> Files, int FilesPerPost);
