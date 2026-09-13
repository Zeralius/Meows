using Meows.Bot;
using Meows.Plugins.Perch.Services;
using Meows.Plugins.Perch.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The projection. Every test hands in a fixed start and a fixed now, so a run that crosses
/// midnight answers the same as one that does not.
/// </summary>
public class ForecastTests
{
    private static readonly DateTime Start = new(2026, 9, 13, 20, 0, 0);

    private static GroupConfig Interval(string name, int minutes, int perPost = 1, int offset = 0, StretchConfig? stretch = null) =>
        new()
        {
            Name = name,
            Folder = "groups/" + name.ToLowerInvariant(),
            Schedule = new ScheduleConfig { IntervalMinutes = minutes },
            FilesPerPost = perPost,
            StartOffsetMinutes = offset,
            JitterMinutes = 0,
            Stretch = stretch,
        };

    private static GroupConfig Daily(string name, int hour, int minute = 0) =>
        new()
        {
            Name = name,
            Folder = "groups/" + name.ToLowerInvariant(),
            Schedule = new ScheduleConfig { Hour = hour, Minute = minute },
            JitterMinutes = 0,
        };

    private static GroupQueue Queue(GroupConfig group, int files, bool archive = true) =>
        new(group, Enumerable.Range(1, files).Select(i => $"{group.Name}-{i}.jpg").ToList(), archive);

    [Fact]
    public void An_interval_group_posts_from_its_offset_and_then_every_interval()
    {
        var slots = Forecast.Build([Queue(Interval("Paws", 60, offset: 10), 3)], Start, Start, TimeSpan.FromHours(6));

        Assert.Equal(4, slots.Count);
        Assert.Equal(Start.AddMinutes(10), slots[0].At);
        Assert.Equal(Start.AddMinutes(70), slots[1].At);
        Assert.Equal(Start.AddMinutes(130), slots[2].At);
        Assert.Equal(["Paws-1.jpg"], slots[0].Files);
        Assert.Equal(["Paws-3.jpg"], slots[2].Files);

        // The fourth is the moment it runs dry, and then it stops being listed.
        Assert.Equal(SlotKind.RunsDry, slots[3].Kind);
        Assert.Equal(Start.AddMinutes(190), slots[3].At);
    }

    [Fact]
    public void A_daily_group_fires_at_its_time_tomorrow_when_that_time_has_passed()
    {
        var slots = Forecast.Build([Queue(Daily("Evening", 19), 5)], Start, Start, TimeSpan.FromHours(48));

        Assert.Equal(2, slots.Count);
        Assert.Equal(new DateTime(2026, 9, 14, 19, 0, 0), slots[0].At);
        Assert.Equal(new DateTime(2026, 9, 15, 19, 0, 0), slots[1].At);
    }

    [Fact]
    public void A_daily_group_still_ahead_today_fires_today()
    {
        var slots = Forecast.Build([Queue(Daily("Late", 21, 30), 5)], Start, Start, TimeSpan.FromHours(6));

        Assert.Equal(new DateTime(2026, 9, 13, 21, 30, 0), Assert.Single(slots).At);
    }

    [Fact]
    public void Several_files_per_post_go_out_together()
    {
        var slots = Forecast.Build([Queue(Interval("Bundle", 60, perPost: 3), 7)], Start, Start, TimeSpan.FromHours(5));

        Assert.Equal(3, slots[0].Files.Count);
        Assert.Equal(3, slots[1].Files.Count);
        Assert.Single(slots[2].Files);
        Assert.Equal(4, slots[0].QueuedAfter);
        Assert.Equal(SlotKind.RunsDry, slots[3].Kind);
    }

    [Fact]
    public void A_bot_started_earlier_has_already_used_up_the_first_posts()
    {
        // Started three hours ago, hourly: three posts have gone, so the first one shown takes
        // the fourth file and lands on the next tick after now.
        var started = Start.AddHours(-3);
        var slots = Forecast.Build([Queue(Interval("Paws", 60), 10)], started, Start, TimeSpan.FromHours(2));

        Assert.Equal(Start, slots[0].At);
        Assert.Equal(["Paws-4.jpg"], slots[0].Files);
    }

    [Fact]
    public void Stretching_widens_the_gap_as_the_queue_shortens()
    {
        var stretch = new StretchConfig { TargetDays = 1, MaxIntervalMinutes = 720 };
        var slots = Forecast.Build([Queue(Interval("Thin", 60, stretch: stretch), 4)], Start, Start, TimeSpan.FromDays(3));

        // After the first post three are left, so the bot wants them to last a day: 480 min.
        Assert.Equal(Start, slots[0].At);
        Assert.True(slots[0].Stretched);
        Assert.Equal(480, slots[0].IntervalMinutes);
        Assert.Equal(Start.AddMinutes(480), slots[1].At);

        // Two left: 720, which is the cap.
        Assert.Equal(720, slots[1].IntervalMinutes);
        Assert.Equal(Start.AddMinutes(480 + 720), slots[2].At);
    }

    [Fact]
    public void Random_order_names_no_files_but_still_takes_them()
    {
        var group = Interval("Lucky", 60);
        group.PostOrder = "random";

        var slots = Forecast.Build([Queue(group, 2)], Start, Start, TimeSpan.FromHours(4));

        Assert.Equal(SlotKind.Random, slots[0].Kind);
        Assert.Empty(slots[0].Files);
        Assert.Equal(SlotKind.RunsDry, slots[2].Kind);
    }

    [Fact]
    public void Nothing_at_all_is_told_apart_from_running_dry()
    {
        var slots = Forecast.Build([Queue(Interval("Empty", 60), 0, archive: false)], Start, Start, TimeSpan.FromHours(2));

        Assert.Equal(SlotKind.Nothing, Assert.Single(slots).Kind);
    }

    [Fact]
    public void Disabled_groups_are_left_out_and_the_rest_are_merged_by_time()
    {
        var off = Interval("Off", 30);
        off.Enabled = false;

        var slots = Forecast.Build(
            [Queue(Interval("A", 60), 2), Queue(Interval("B", 45), 2), Queue(off, 5)],
            Start, Start, TimeSpan.FromHours(1.5));

        Assert.DoesNotContain(slots, s => s.Group.Name == "Off");
        Assert.Equal(["A", "B", "B", "A"], slots.Select(s => s.Group.Name));
    }

    [Fact]
    public void The_queue_is_ordered_by_modified_time_the_way_the_bot_takes_it()
    {
        var t = new DateTime(2026, 1, 1);
        (string, DateTime)[] files = [("b", t.AddDays(2)), ("a", t.AddDays(1)), ("c", t.AddDays(3))];

        Assert.Equal(["a", "b", "c"], Forecast.Order(files, null));
        Assert.Equal(["c", "b", "a"], Forecast.Order(files, "newest"));
        Assert.Equal(["b", "a", "c"], Forecast.Order(files, "random"));
    }

    [Fact]
    public void A_typed_start_is_read_in_the_ways_people_type_it()
    {
        Assert.Equal(new DateTime(2026, 9, 13, 21, 0, 0), PerchViewModel.ParseStart("2026-09-13 21:00"));
        Assert.Equal(new DateTime(2026, 9, 13, 21, 0, 0), PerchViewModel.ParseStart("13.09.2026 21:00"));
        Assert.Null(PerchViewModel.ParseStart(""));
        Assert.Null(PerchViewModel.ParseStart("yesterday-ish"));

        // A bare time is today unless that would be in the future, in which case it was yesterday.
        var bare = PerchViewModel.ParseStart("00:01")!.Value;
        Assert.True(bare <= DateTime.Now);
    }
}
