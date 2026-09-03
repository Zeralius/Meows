using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meows.Bot;

/// <summary>config.json as bot.py reads it. Names map through the snake_case policy.</summary>
public sealed class BotConfig
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<GroupConfig> Groups { get; set; } = [];
}

public sealed class GroupConfig
{
    public string Name { get; set; } = "";

    public string ChatId { get; set; } = "";

    public string Folder { get; set; } = "";

    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>Absent means enabled. bot.py only skips on an explicit false.</summary>
    public bool? Enabled { get; set; }

    public int? JitterMinutes { get; set; }

    /// <summary>
    /// Minutes to wait before this group's first run, so eleven hourly groups do not all fire
    /// on the hour. The bot reads it; nothing here changes it, but it has to be modelled or
    /// saving the file would drop it.
    /// </summary>
    public int? StartOffsetMinutes { get; set; }

    public int? FilesPerPost { get; set; }

    public string? PostOrder { get; set; }

    public string? ComicOrder { get; set; }

    /// <summary>Absent means the group posts at its configured rate whatever is left.</summary>
    public StretchConfig? Stretch { get; set; }

    /// <summary>
    /// Anything in config.json this does not model, kept so it survives a save.
    ///
    /// Saving rewrites the whole file from these objects, so a key we have no property for is
    /// a key we delete. That is not hypothetical: start_offset_minutes was being dropped, and
    /// with it the spacing that stops every group posting at the same second. A property was
    /// added for that one, and this is here so the next key the bot gains does not need one.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public GroupConfig Clone() => new()
    {
        Name = Name,
        ChatId = ChatId,
        Folder = Folder,
        Schedule = new ScheduleConfig
        {
            Hour = Schedule.Hour,
            Minute = Schedule.Minute,
            IntervalMinutes = Schedule.IntervalMinutes,
        },
        Enabled = Enabled,
        JitterMinutes = JitterMinutes,
        StartOffsetMinutes = StartOffsetMinutes,
        FilesPerPost = FilesPerPost,
        PostOrder = PostOrder,
        ComicOrder = ComicOrder,
        Stretch = Stretch is null ? null : new StretchConfig
        {
            TargetDays = Stretch.TargetDays,
            MaxIntervalMinutes = Stretch.MaxIntervalMinutes,
        },
        Extra = Extra is null ? null : new Dictionary<string, JsonElement>(Extra),
    };
}

/// <summary>
/// Slows a group down when its queue runs short, so what is left lasts longer instead of
/// running out and leaving the bot to repeat the archive.
///
/// The bot does the stretching, because only it knows the queue at the moment it posts and
/// only it can retime a running job. This is the part written down.
/// </summary>
public sealed class StretchConfig
{
    /// <summary>How long the remaining queue should be made to last.</summary>
    public double? TargetDays { get; set; }

    /// <summary>
    /// As far apart as the posts are ever allowed to get. Without a limit a group with three
    /// files left would post once a fortnight, which is not a channel any more.
    /// </summary>
    public int? MaxIntervalMinutes { get; set; }
}

public sealed class ScheduleConfig
{
    public int? Hour { get; set; }

    public int? Minute { get; set; }

    public int? IntervalMinutes { get; set; }
}
