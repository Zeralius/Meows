using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Naptime.Services;

/// <summary>One habit: a name, how often a week it is wanted, and the days it got its tick.</summary>
public sealed class Habit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>Times per calendar week, Monday to Sunday. Clamped to 1–7 wherever it is set.</summary>
    public int Target { get; set; } = 3;

    /// <summary>Days with a tick, as dates. Time of day is never part of one.</summary>
    public List<DateTime> DoneDays { get; set; } = [];
}

/// <summary>
/// The arithmetic behind the habits: weeks, streaks with one miss forgiven, and the header.
///
/// Pure, and told what today is rather than reading the clock, so a test can sit on a date and a
/// pass that starts before midnight cannot answer differently to the one after it.
/// </summary>
public static class Streaks
{
    /// <summary>Monday of the week the day sits in.</summary>
    public static DateTime WeekStart(DateTime day)
    {
        // Sunday is 0 and the week starts Monday, so it moves back six.
        var back = ((int)day.DayOfWeek + 6) % 7;
        return day.Date.AddDays(-back);
    }

    /// <summary>Ticks this week, Monday to today. The future is never counted.</summary>
    public static int ThisWeek(Habit habit, DateTime today)
    {
        var start = WeekStart(today);
        return habit.DoneDays.Count(d => d.Date >= start && d.Date <= today.Date);
    }

    /// <summary>
    /// The streak: ticked days back from today, where today may still be unticked and one
    /// missed day does not break the run when the day before it is ticked — the gap is
    /// bridged, not just excused. Two misses in a row always break it. A habit with no ticks
    /// has no streak, and a streak is counted in days, gaps included.
    /// </summary>
    public static int Of(Habit habit, DateTime today)
    {
        var done = new HashSet<DateTime>(habit.DoneDays.Select(d => d.Date));
        if (done.Count == 0)
            return 0;

        var streak = 0;
        var day = today.Date;

        // Today still unticked is a day in progress, not a miss — unless yesterday has one too.
        if (!done.Contains(day))
        {
            if (!done.Contains(day.AddDays(-1)))
                return 0;
            day = day.AddDays(-1);
        }

        while (true)
        {
            if (done.Contains(day))
            {
                streak++;
                day = day.AddDays(-1);
                continue;
            }
            // A miss bridges only to a ticked day before it; two in a row break the run.
            if (!done.Contains(day.AddDays(-1)))
                break;
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    /// <summary>Whether the habit met its target in the week the day sits in.</summary>
    public static bool MetTarget(Habit habit, DateTime today) =>
        ThisWeek(habit, today) >= Math.Clamp(habit.Target, 1, 7);

    /// <summary>The line under the header: ticks today, and weeks met.</summary>
    public static string SummaryOf(IReadOnlyList<Habit> habits, DateTime today, IMeowsText text)
    {
        if (habits.Count == 0)
            return "";

        var ticked = habits.Count(h => h.DoneDays.Any(d => d.Date == today.Date));
        var met = habits.Count(h => MetTarget(h, today));
        return text.Format("naptime.summary", ticked, habits.Count, met);
    }

    /// <summary>The Home line. Red while yesterday passed with nothing ticked anywhere.</summary>
    public static Glance? GlanceOf(IReadOnlyList<Habit> habits, DateTime today, IMeowsText text)
    {
        if (habits.Count == 0)
            return null;

        var summary = SummaryOf(habits, today, text);
        // Two days with nothing ticked anywhere is the nudge: yesterday is over, today is not.
        var missed = !habits.Any(h => h.DoneDays.Any(d => d.Date == today.Date || d.Date == today.Date.AddDays(-1)));
        return new Glance(summary, missed);
    }

    /// <summary>How a streak reads: days, or nothing when there is none.</summary>
    public static string StreakText(int streak, IMeowsText text) =>
        streak <= 0 ? "" : text.Format("naptime.streak", streak);
}
