using Meows.Plugins.Abstractions;

namespace Meows.Bot;

/// <summary>
/// How long a group's queue lasts at its own posting rate.
///
/// The point of the whole plugin, really. A raw count tells you nothing: 63 files sounds
/// healthier than 481 until you notice one group posts hourly and the other daily. Days is the
/// number that tells you where to put things.
/// </summary>
public static class QueueRunway
{
    /// <summary>Files this group sends per day, from its schedule and files_per_post.</summary>
    public static double FilesPerDay(GroupConfig group)
    {
        var perPost = Math.Max(1, group.FilesPerPost ?? 1);
        var interval = group.Schedule?.IntervalMinutes ?? 0;

        // An interval group posts 1440/interval times a day. A daily group posts once.
        return interval > 0 ? 1440d / interval * perPost : perPost;
    }

    /// <summary>Days of queue left, or null when the group is disabled or posts nothing.</summary>
    public static double? Days(GroupConfig group, int queued)
    {
        if (group.Enabled == false)
            return null;

        var rate = FilesPerDay(group);
        return rate <= 0 ? null : queued / rate;
    }

    public static string Describe(double? days)
    {
        if (days is null)
            return MeowsText.Current["bot.runway.unscheduled"];
        if (days == 0)
            return MeowsText.Current["bot.runway.dry"];
        if (days < 1)
            return MeowsText.Current.Format("bot.runway.hours", (days.Value * 24).ToString("0.#"));
        return MeowsText.Current.Format("bot.runway.days", days.Value.ToString("0.#"));
    }

    /// <summary>Anything under this is worth shouting about.</summary>
    public const double LowDays = 3;
}
