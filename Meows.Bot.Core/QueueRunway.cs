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

    /// <summary>
    /// How far apart the bot will actually put this group's posts, given what is left in its
    /// queue. Null when the group is not being stretched, which is when the answer is simply
    /// the interval in config.json.
    ///
    /// This is a copy of stretched_interval in bot.py and has to stay one. The bot does the
    /// stretching; this only works out what it is going to do, so the number on screen is the
    /// real one rather than the configured one. StretchTests pins both against the same table.
    /// </summary>
    public static int? StretchedIntervalMinutes(GroupConfig group, int queued)
    {
        if (group.Stretch?.TargetDays is not { } target || target <= 0)
            return null;

        // Only an interval group has a gap to widen. A daily group already posts as rarely as
        // its schedule allows, and bot.py says so and ignores the setting.
        if (group.Schedule?.IntervalMinutes is not { } baseMinutes || baseMinutes <= 0)
            return null;

        var cap = group.Stretch.MaxIntervalMinutes is { } c && c > 0 ? c : DefaultCapMinutes;
        var perPost = Math.Max(1, group.FilesPerPost ?? 1);
        var postsLeft = (int)Math.Ceiling(queued / (double)perPost);

        // Nothing left to spread out. Posting less often would only slow the archive repeats.
        if (postsLeft <= 0)
            return baseMinutes;

        var wanted = target * 1440 / postsLeft;
        return (int)Math.Max(baseMinutes, Math.Min(cap, wanted));
    }

    /// <summary>Whether this group is being slowed down right now, rather than merely allowed to be.</summary>
    public static bool IsStretching(GroupConfig group, int queued) =>
        StretchedIntervalMinutes(group, queued) is { } effective &&
        effective > (group.Schedule?.IntervalMinutes ?? 0);

    /// <summary>Days of queue left, or null when the group is disabled or posts nothing.</summary>
    public static double? Days(GroupConfig group, int queued)
    {
        if (group.Enabled == false)
            return null;

        // A stretched group lasts longer than its configured rate suggests, and how long it
        // lasts is the whole question this answers, so use the pace it will really run at.
        if (StretchedIntervalMinutes(group, queued) is { } effective)
        {
            var perPost = Math.Max(1, group.FilesPerPost ?? 1);
            var postsLeft = (int)Math.Ceiling(queued / (double)perPost);
            return postsLeft * effective / 1440d;
        }

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

    /// <summary>What bot.py uses when a stretch does not name a limit of its own.</summary>
    public const int DefaultCapMinutes = 1440;
}
