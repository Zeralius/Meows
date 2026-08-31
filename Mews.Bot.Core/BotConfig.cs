using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mews.Bot;

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

    public int? FilesPerPost { get; set; }

    public string? PostOrder { get; set; }

    public string? ComicOrder { get; set; }

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
        FilesPerPost = FilesPerPost,
        PostOrder = PostOrder,
        ComicOrder = ComicOrder,
    };
}

public sealed class ScheduleConfig
{
    public int? Hour { get; set; }

    public int? Minute { get; set; }

    public int? IntervalMinutes { get; set; }
}
