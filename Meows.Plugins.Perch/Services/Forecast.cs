using Meows.Bot;

namespace Meows.Plugins.Perch.Services;

/// <summary>What a slot on the timeline is.</summary>
public enum SlotKind
{
    /// <summary>These files, at this time. The ordinary case.</summary>
    Post,

    /// <summary>post_order is random, so the bot picks at post time and nothing can be named.</summary>
    Random,

    /// <summary>
    /// The queue is empty from here: the bot starts repeating the archive, and the channel
    /// looks busy while nothing new goes out. One of these per group, at the moment it happens.
    /// </summary>
    RunsDry,

    /// <summary>Nothing in the queue and nothing in the archive. The group posts nothing at all.</summary>
    Nothing,
}

/// <summary>One thing the bot is going to do, and when.</summary>
public sealed record Slot(
    DateTime At,
    int JitterMinutes,
    GroupConfig Group,
    SlotKind Kind,
    IReadOnlyList<string> Files,
    int? IntervalMinutes,
    bool Stretched,
    int QueuedAfter)
{
    /// <summary>The latest it could actually happen, since jitter only ever delays.</summary>
    public DateTime Latest => At.AddMinutes(JitterMinutes);
}

/// <summary>
/// A group's queue in the order the bot will take it, which is by modified time and
/// post_order, never by name. Worked out once from the folder and handed in, so the
/// projection itself touches no disk.
/// </summary>
public sealed record GroupQueue(GroupConfig Group, IReadOnlyList<string> Ordered, bool HasArchive);

/// <summary>
/// What the bot will post, where, and when, as far ahead as the queues last.
///
/// Nothing in here is new. bot.py's scheduling is an interval trigger phased by
/// start_offset_minutes or a daily cron at hour:minute, either way followed by a forward-only
/// jitter; the files it takes are the next files_per_post of the queue by modified time; and
/// after each post it widens the interval towards the stretch target from what is left. Every
/// one of those rules is already written down in <see cref="QueueRunway"/> and
/// <see cref="BotWorkspace"/>, and this only runs them forward instead of describing now.
///
/// It is a projection, not a promise. The bot's own clock starts when the bot does, which this
/// machine cannot see, so the phase of an interval group is taken from the start it is given.
/// </summary>
public static class Forecast
{
    /// <summary>bot.py's defaults, for the fields a group leaves out.</summary>
    public const int DefaultJitterMinutes = 15;

    public const int DefaultHour = 12;

    /// <summary>
    /// Every slot inside the horizon, all groups merged, soonest first.
    /// </summary>
    /// <param name="queues">Each enabled group with its queue in posting order.</param>
    /// <param name="botStart">When the bot was, or will be, started. Interval groups phase from this.</param>
    /// <param name="now">The left edge of the picture. Nothing before it is shown.</param>
    /// <param name="horizon">How far ahead to look.</param>
    public static IReadOnlyList<Slot> Build(IReadOnlyList<GroupQueue> queues, DateTime botStart, DateTime now, TimeSpan horizon)
    {
        var end = now + horizon;
        var slots = new List<Slot>();

        foreach (var queue in queues)
        {
            if (queue.Group.Enabled == false)
                continue;

            slots.AddRange(ForGroup(queue, botStart, now, end));
        }

        return slots.OrderBy(s => s.At).ThenBy(s => s.Group.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<Slot> ForGroup(GroupQueue queue, DateTime botStart, DateTime now, DateTime end)
    {
        var group = queue.Group;
        var perPost = Math.Max(1, group.FilesPerPost ?? 1);
        var jitter = Math.Max(0, group.JitterMinutes ?? DefaultJitterMinutes);
        var random = string.Equals(group.PostOrder, "random", StringComparison.OrdinalIgnoreCase);
        var interval = group.Schedule?.IntervalMinutes is { } i && i > 0 ? i : (int?)null;

        var remaining = new Queue<string>(queue.Ordered);
        var at = interval is { } minutes
            ? botStart.AddMinutes(Math.Max(0, group.StartOffsetMinutes ?? 0))
            : FirstDaily(group, botStart);

        // A bot that has been running since before now has already fired some of these. Roll
        // forward to the first slot that is still ahead, taking the files those posts took.
        var guard = 0;
        while (at < end && guard++ < 100_000)
        {
            var visible = at >= now;

            if (remaining.Count == 0)
            {
                if (visible)
                    yield return new Slot(at, jitter, group, queue.HasArchive ? SlotKind.RunsDry : SlotKind.Nothing, [], interval, false, 0);
                yield break;
            }

            var taken = new List<string>();
            while (taken.Count < perPost && remaining.Count > 0)
                taken.Add(remaining.Dequeue());

            var stretched = interval is { } && QueueRunway.IsStretching(group, remaining.Count);
            var effective = interval is { } ? QueueRunway.StretchedIntervalMinutes(group, remaining.Count) ?? interval : null;

            if (visible)
            {
                yield return new Slot(at, jitter, group, random ? SlotKind.Random : SlotKind.Post,
                    random ? [] : taken, effective, stretched, remaining.Count);
            }

            at = interval is { } ? at.AddMinutes(effective!.Value) : at.AddDays(1);
        }
    }

    /// <summary>
    /// The first daily firing at or after the bot starts. A cron at 21:00 started at 22:00 fires
    /// tomorrow, not in an hour.
    /// </summary>
    public static DateTime FirstDaily(GroupConfig group, DateTime botStart)
    {
        var hour = Math.Clamp(group.Schedule?.Hour ?? DefaultHour, 0, 23);
        var minute = Math.Clamp(group.Schedule?.Minute ?? 0, 0, 59);
        var candidate = botStart.Date.AddHours(hour).AddMinutes(minute);
        return candidate >= botStart ? candidate : candidate.AddDays(1);
    }

    /// <summary>
    /// The queue as bot.py's get_next_media orders it: by modified time, oldest first unless
    /// post_order says newest. Random is left in folder order, since the bot shuffles at post
    /// time and the names are not shown for it anyway.
    /// </summary>
    public static IReadOnlyList<string> Order(IEnumerable<(string Path, DateTime Modified)> files, string? postOrder)
    {
        var order = (postOrder ?? "oldest").ToLowerInvariant();
        var list = files.ToList();

        return order switch
        {
            "newest" => list.OrderByDescending(f => f.Modified).Select(f => f.Path).ToList(),
            "random" => list.Select(f => f.Path).ToList(),
            _ => list.OrderBy(f => f.Modified).Select(f => f.Path).ToList(),
        };
    }
}
